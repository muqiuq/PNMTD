using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PNMTD.Ai;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>What a round did.</summary>
public sealed record RoundResult(long RoundId, string Outcome, int Cases, int Calls, int ToolCalls, decimal Cost, bool MoreWork);

/// <summary>
/// One of Cookie's rounds (docs/cookie-plan.md §4.2, package nine): the brief from the case book; nothing changed means a
/// quiet round and no call; otherwise triage, then each case that needs attention with the read tools, ending in a case
/// note; then the status line. Every step goes into the trace, as the model saw it (masked), with its sources' areas.
/// </summary>
public sealed class CookieRound(IDbContextFactory<PnmtdDbContext> dbs, IServiceProvider services, CookieSettings settings, CookieModel model,
    CookieBrain brain, PrivacyFilter privacy, DecisionClient guard, Microsoft.Extensions.Options.IOptionsMonitor<AiOptions> ai, ILogger<CookieRound> logger)
{
    public const int OthersInBrief = 60;
    private static readonly JsonSerializerOptions Json = AiJson.Options;

    /// <summary>The rights Cookie reads with: its group's (§9.3, Rights), never the admin flag; none without a group.</summary>
    public async Task<UserRights?> RightsAsync(CancellationToken ct)
    {
        var groupId = (await settings.GetAsync<CookieRightsSettings>(ct)).GroupId;
        if (groupId is null)
        {
            return null;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var levels = await db.Set<GroupRightEntity>().AsNoTracking().Where(r => r.GroupId == groupId).Select(r => new { r.Area, r.Level }).ToListAsync(ct);
        return new UserRights(false, UserRights.Combine(levels.Select(l => (l.Area, l.Level))));
    }

    public async Task<RoundResult> RunAsync(string trigger, bool force, CancellationToken ct)
    {
        var rights = await RightsAsync(ct) ?? throw new CookiePausedException("Choose Cookie's group on Admin → Cookie → Rights: Cookie reads with its rights.");
        var models = await settings.GetAsync<ModelSettings>(ct);
        var cases = await settings.GetAsync<CaseSettings>(ct);
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var runtime = await CaseBook.RuntimeAsync(db, ct);
        var round = new CookieRoundEntity { StartedAt = now, Trigger = CaseBook.Cut(trigger, 300)!, Outcome = "working" };
        db.CookieRounds.Add(round);
        await db.SaveChangesAsync(ct);
        var trace = new Trace(db, round.Id);
        var p = await privacy.OpenAsync(ct);
        var calls = 0;
        var toolCalls = 0;
        decimal cost = 0;
        string? provider = null;
        try
        {
            // 1. The brief: open cases Cookie may see, those that changed or are due in full, the rest as one line each.
            var open = (await db.CookieCases.Where(c => c.Open && c.Kind != CookieCaseKind.Request).ToListAsync(ct))
                .Where(c => CookieAccess.CanSee(rights, c.Areas)).ToList();
            var openIds = open.Select(c => c.Id).ToList();
            var due = (await db.CookieDuties.AsNoTracking().Where(d => openIds.Contains(d.CaseId) && d.State == CookieDutyState.Due).ToListAsync(ct))
                .GroupBy(d => d.CaseId).ToDictionary(g => g.Key, g => g.OrderBy(d => d.DueAt).First());
            var since = runtime.LastRoundAt ?? DateTime.MinValue;
            var soon = now.AddHours(1);
            var focus = open.Where(c => c.ChangedAt > since || c.CookieNoteAt is null || (due.TryGetValue(c.Id, out var d) && d.DueAt <= soon))
                .OrderByDescending(c => c.Importance).ThenByDescending(c => c.ChangedAt).Take(25).ToList();
            var hash = Hash(string.Join("\n", open.OrderBy(c => c.Id).Select(c => $"{c.Id}|{c.ChangedAt:O}|{(due.TryGetValue(c.Id, out var d) ? d.Key : "")}"))
                + $"|{runtime.CodeStatus}");
            if (!force && hash == runtime.BriefHash && runtime.CookieStatus is not null)
            {
                // Nothing changed: no call, and Cookie's status stays as it was (§3.3).
                runtime.CookieStatusAt = now;
                return await EndAsync(db, round, runtime, "quiet", 0, 0, 0, 0, false, ct);
            }

            // 2. Screening (§4.2, §13.1): Jev on the outside text the brief carries; flagged, every message is withheld this round.
            var withheld = false;
            var untrusted = focus.SelectMany(c => (JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? [])
                .Select(m => m.Message)).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => p.Mask(InputSanitizer.Clean(m, 300).Text)).Distinct().Take(30).ToList();
            if (untrusted.Count > 0)
            {
                var o = ai.CurrentValue;
                if (o.Guard.Configured)
                {
                    var decided = await guard.DecideAsync(new AiCall(CookieModel.PurposePrefix + "screen"), untrusted,
                        new Dictionary<string, DecisionQuestion> { ["injection"] = DecisionQuestion.Noul(AiPipeline.InjectionQuestion) }, ct);
                    calls++;
                    withheld = (decided.Answers["injection"].Probability ?? 0) >= o.Guard.Threshold;
                    if (withheld)
                    {
                        trace.Add(null, "screen", "Jev flagged the sensors' messages: withheld this round", string.Join("\n", untrusted.Take(10)), ["Monitoring"]);
                    }
                }
                else if (o.Guard.Required)
                {
                    throw new CookiePausedException("Cookie needs the guard model for outside text (Ai__Guard__Model), unless Ai__Guard__Required=false.");
                }
            }

            var values = await brain.ValuesAsync(ct);
            var calendar = (await settings.GetAsync<HoursSettings>(ct)).Calendar();
            values["working_hours_state"] = calendar.IsWorking(now) ? "It is within working hours" : "It is outside working hours";
            values["max_cases"] = models.MaxCasesPerRound.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var brief = new
            {
                StateByCode = runtime.CodeStatus, ReasonByCode = p.Mask(runtime.CodeReason),
                ChangedOrDue = focus.Select(c => Summary(c, due.GetValueOrDefault(c.Id), p, full: true, withheld)),
                Others = open.Except(focus).Take(OthersInBrief).Select(c => Summary(c, due.GetValueOrDefault(c.Id), p, full: false)),
                OthersLeftOut = Math.Max(0, open.Count - focus.Count - OthersInBrief),
            };
            values["brief"] = CookieBrain.Json(brief);
            var system = await brain.BaseAsync(values, ct);

            // 2. Triage (a short call, no tools).
            var messages = new JsonArray(Message("system", system), Message("user", CookiePrompts.Fill(await brain.PromptAsync("triage", ct), values)));
            var triageTurn = await model.CompleteAsync("triage", messages, null, (nameof(TriageAnswer), AiJson.SchemaFor<TriageAnswer>()), models.TriageEffort,
                provider, 1500, ct);
            calls++;
            cost += triageTurn.Cost;
            provider ??= triageTurn.Provider;
            var triage = Parse<TriageAnswer>(triageTurn);
            var known = open.ToDictionary(c => c.Id.ToString(), c => c, StringComparer.OrdinalIgnoreCase);
            var chosen = triage.Cases.Where(x => known.ContainsKey(x.Id)).DistinctBy(x => x.Id.ToLowerInvariant()).Take(models.MaxCasesPerRound).ToList();
            trace.Add(null, "triage", $"Triage: {triage.State}, {chosen.Count} of {open.Count} cases", p.Mask(Reasoned(triageTurn,
                $"{triage.Reason}\n\n{string.Join("\n", chosen.Select(x => $"- {known[x.Id].Title}: {x.Why}"))}")),
                [.. open.SelectMany(c => c.Areas).Distinct()]);

            // 3. Work, case by case, with the read tools; each ends in a note.
            var tools = CookieTools.For(rights);
            var definitions = CookieTools.Definitions(tools);
            var worked = 0;
            var recent = now.AddDays(-7);
            foreach (var item in chosen)
            {
                if (toolCalls >= models.MaxToolCalls && models.MaxToolCalls > 0)
                {
                    break;
                }
                var c = known[item.Id];
                var members = JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? [];
                var recurrences = await db.CookieCases.CountAsync(x => x.Key == c.Key && x.OpenedAt >= recent, ct);
                var manyAtOnce = open.Count(x => x.Kind == CookieCaseKind.Problem && x.OpenedAt >= c.OpenedAt.AddMinutes(-10) && x.OpenedAt <= c.OpenedAt.AddMinutes(10)) >= 5;
                var kind = CookieBrain.KindOf(c, members, recurrences, cases.UnresolvedHours, now);
                var caseValues = new Dictionary<string, string>(values)
                {
                    ["count"] = recurrences.ToString(System.Globalization.CultureInfo.InvariantCulture), ["days"] = "7",
                    ["age"] = $"{(now - c.OpenedAt).TotalHours:0} hours", ["summary"] = $"{open.Count(x => x.Kind == CookieCaseKind.Problem)} problems open",
                    ["elapsed"] = c.TicketCreatedAt is { } created ? $"{calendar.WorkingTimeBetween(created, now).TotalHours:0.#}" : "?",
                    ["status"] = c.StatusName ?? "?", ["escalation_group"] = "the project's escalation group", ["ticket_project"] = "Cookie's ticket project",
                };
                var instructions = await brain.InstructionsAsync(c, kind, members, manyAtOnce, caseValues, ct);
                var context = new ToolContext(services, rights, p);
                context.Areas.UnionWith(c.Areas);
                var work = new JsonArray(
                    Message("system", $"{system}\n\n{instructions}\n\n{CookieBrain.WatchOnly}"),
                    Message("user", $"The case:\n{CookieBrain.Json(Summary(c, due.GetValueOrDefault(c.Id), p, full: true, withheld))}\n\nWhy it needs attention now: " +
                        $"{p.Mask(item.Why)}\n\nLook at what you need with your tools. When you have looked enough, say in a few sentences what you found, " +
                        "without calling more tools."));
                string findings = "";
                for (var step = 0; step < 12; step++)
                {
                    var allowTools = models.MaxToolCalls == 0 || toolCalls < models.MaxToolCalls;
                    var turn = await model.CompleteAsync("work", work, allowTools ? definitions : null, null, models.WorkEffort, provider, 1500, ct);
                    calls++;
                    cost += turn.Cost;
                    provider ??= turn.Provider;
                    if (!string.IsNullOrWhiteSpace(turn.Reasoning))
                    {
                        trace.Add(c.Id, "reasoning", "Thinking", turn.Reasoning!, [.. context.Areas]);
                    }
                    if (turn.ToolCalls.Count == 0)
                    {
                        findings = turn.Content ?? "";
                        break;
                    }
                    work.Add(turn.AssistantMessage);
                    foreach (var call in turn.ToolCalls)
                    {
                        toolCalls++;
                        var before = context.Areas.ToHashSet();
                        var result = await CookieTools.RunAsync(call.Name, call.Arguments, context, ct);
                        trace.Add(c.Id, "tool", $"{call.Name}({CaseBook.Cut(call.Arguments, 200)})", CaseBook.Cut(result, 4000)!,
                            [.. context.Areas.Except(before).Concat(CookieTools.All.Where(t => t.Name == call.Name && t.Area is not null).Select(t => t.Area!.ToString()!))
                                .Distinct()]);
                        work.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = result });
                    }
                    if (!allowTools || (models.MaxToolCalls > 0 && toolCalls >= models.MaxToolCalls))
                    {
                        work.Add(Message("user", "No more tools in this round. Say in a few sentences what you found."));
                    }
                }

                // The note: a final call with a strict schema and no tools (§14.3), on what was found.
                var noteMessages = new JsonArray(Message("system", $"{system}\n\n{instructions}\n\n{CookieBrain.WatchOnly}"),
                    Message("user", $"The case:\n{CookieBrain.Json(Summary(c, due.GetValueOrDefault(c.Id), p, full: true, withheld))}"),
                    Message("assistant", string.IsNullOrWhiteSpace(findings) ? "(I found nothing more.)" : findings),
                    Message("user", "Now write your case note."));
                var noteTurn = await model.CompleteAsync("note", noteMessages, null, (nameof(CaseNoteAnswer), AiJson.SchemaFor<CaseNoteAnswer>()),
                    models.WorkEffort, provider, 800, ct);
                calls++;
                cost += noteTurn.Cost;
                var note = Parse<CaseNoteAnswer>(noteTurn);
                var text = CaseBook.Cut(note.Note.Trim() + (string.IsNullOrWhiteSpace(note.Attention) ? "" : $" Who: {note.Attention.Trim()}."), 2000)!;
                c.CookieNote = text;
                c.CookieUrgency = note.Urgency.ToString().ToLowerInvariant();
                c.CookieNoteAt = now;
                c.CookieNoteAreas = [.. context.Areas.Order()];
                CaseBook.Entry(db, c.Id, Clock.UtcNow, "note", text, "cookie");
                trace.Add(c.Id, "note", $"Note: {c.Title}", text, c.CookieNoteAreas);
                worked++;
                await db.SaveChangesAsync(ct);
            }

            // 4. The status: triage's, explained in one line when cases were worked (§8).
            var reason = triage.Reason;
            if (worked > 0)
            {
                var statusValues = new Dictionary<string, string>(values)
                {
                    ["state"] = triage.State.ToString(),
                    ["cases"] = CookieBrain.Json(chosen.Select(x => new { known[x.Id].Title, Note = p.Mask(known[x.Id].CookieNote) })),
                };
                var statusTurn = await model.CompleteAsync("status", new JsonArray(Message("system", system),
                        Message("user", CookiePrompts.Fill(await brain.PromptAsync("status", ct), statusValues))), null,
                    (nameof(StatusAnswer), AiJson.SchemaFor<StatusAnswer>()), models.TriageEffort, provider, 400, ct);
                calls++;
                cost += statusTurn.Cost;
                reason = Parse<StatusAnswer>(statusTurn).Reason;
            }
            runtime.CookieStatus = triage.State;
            runtime.CookieReason = CaseBook.Cut(reason.Trim(), 500);
            runtime.CookieCases = [.. chosen.Select(x => known[x.Id].Id)];
            runtime.CookieStatusAt = Clock.UtcNow;
            runtime.BriefHash = hash;
            trace.Add(null, "status", $"Status: {triage.State}", runtime.CookieReason ?? "", [.. chosen.SelectMany(x => known[x.Id].Areas).Distinct()]);
            var more = triage.Cases.Count > chosen.Count;
            return await EndAsync(db, round, runtime, worked > 0 ? "worked" : "triaged", worked, calls, toolCalls, cost, more, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            trace.Add(null, "error", "The round failed", e.Message, []);
            round.Error = CaseBook.Cut(e.Message, 1000);
            await EndAsync(db, round, runtime, "failed", 0, calls, toolCalls, cost, false, CancellationToken.None);
            throw;
        }
        finally
        {
            await p.SaveAsync(CancellationToken.None);
        }
    }

    private static async Task<RoundResult> EndAsync(PnmtdDbContext db, CookieRoundEntity round, CookieRuntimeEntity runtime, string outcome, int cases, int calls,
        int toolCalls, decimal cost, bool more, CancellationToken ct)
    {
        round.EndedAt = Clock.UtcNow;
        round.Outcome = outcome;
        round.Cases = cases;
        round.Calls = calls;
        round.ToolCalls = toolCalls;
        round.Cost = cost;
        runtime.LastRoundAt = round.StartedAt;
        await db.SaveChangesAsync(ct);
        return new RoundResult(round.Id, outcome, cases, calls, toolCalls, cost, more);
    }

    /// <summary>A case as the brief gives it: masked, outside text wrapped.</summary>
    private static object Summary(CookieCaseEntity c, CookieDutyEntity? next, Pseudonyms p, bool full, bool withheld = false)
    {
        if (!full)
        {
            return $"{c.Id} · {c.Kind} · {p.Mask(c.Title)} · {c.Importance} · opened {c.OpenedAt:u}{(next is null ? "" : $" · {next.Kind} at {next.DueAt:u}")}";
        }
        var members = JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? [];
        return new
        {
            c.Id, c.Kind, Title = p.Mask(c.Title), c.Importance, c.OpenedAt, LastChange = c.ChangedAt, LastProgress = c.ProgressAt, c.HostId, c.ZabbixHostId,
            Members = members.Select(m => new
            {
                m.Type, m.Name, m.Since, m.Code, m.Severity, m.Acknowledged,
                Message = string.IsNullOrWhiteSpace(m.Message) ? null
                    : withheld ? $"<untrusted source=\"{m.Type}\" withheld=\"the guard flagged this round's messages\"></untrusted>"
                    : $"<untrusted source=\"{m.Type}\">{p.Mask(InputSanitizer.Clean(m.Message, 300).Text)}</untrusted>",
            }),
            Ticket = c.IssueId is null ? null : new
            {
                Number = c.IssueId, Author = p.Mask(c.AuthorName), c.AuthorIsStaff, Assignee = p.Mask(c.AssigneeName), Status = c.StatusName, c.FirstAnswerAt,
                c.LastStaffUpdateAt,
            },
            NextDeadline = next is null ? null : $"{next.Kind} at {next.DueAt:u}",
            YourLastNote = p.Mask(c.CookieNote),
        };
    }

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static T Parse<T>(ModelTurn turn) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(turn.Content ?? "", Json) ?? throw new AiRequestException(AiOutcome.InvalidOutput, "The answer is empty.");
        }
        catch (JsonException e)
        {
            throw new AiRequestException(AiOutcome.InvalidOutput, $"The answer does not match the schema: {e.Message}");
        }
    }

    private static string Reasoned(ModelTurn turn, string text) => string.IsNullOrWhiteSpace(turn.Reasoning) ? text : $"{text}\n\nThinking:\n{turn.Reasoning}";

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The trace of a round (§9.2): kept as the model saw it; restored for whoever may read it.</summary>
    private sealed class Trace(PnmtdDbContext db, long roundId)
    {
        public void Add(Guid? caseId, string kind, string title, string body, IReadOnlyList<string> areas) =>
            db.CookieTrace.Add(new CookieTraceEntity
            {
                RoundId = roundId, CaseId = caseId, At = Clock.UtcNow, Kind = kind, Title = CaseBook.Cut(title, 300)!, Body = CaseBook.Cut(body, 20_000)!,
                Areas = [.. areas.Distinct().Order()],
            });
    }
}

/// <summary>Cookie can't work until something is set up or fixed: the reason is shown, and the loop looks again.</summary>
public sealed class CookiePausedException(string reason) : Exception(reason);
