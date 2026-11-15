using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Tools;

namespace PNMTD.Cookie.Access;

/// <summary>Cookie's verdict on an access case (docs/cookie-access-plan.md §5.4).</summary>
public sealed record AccessVerdictAnswer(
    [property: Description("normal, unusual, suspicious or alarming")] AccessVerdict Verdict,
    [property: Description("How sure you are, 0 to 1; 0.9 means wrong 1 time in 10")] double Confidence,
    [property: Description("One sentence for the people who look after access, at most 300 characters: what is new about it, and why it matters or not")] string Reason,
    [property: Description("Your case note, at most 800 characters: what you looked at and what you found")] string Note,
    [property: Description("The ids of the evidence: logins, tickets, knowledge base pages, cases")] IReadOnlyList<string> Evidence);

/// <summary>What judging the access cases of a round did.</summary>
public sealed record AccessJudged(int Worked, int Calls, int ToolCalls, decimal Cost, bool More);

/// <summary>
/// Cookie's step for the access cases (docs/cookie-access-plan.md §5): before the other cases and without triage, at most
/// <see cref="PerRound"/> a round. Each case's logins, their signals and their context go to Cookie as untrusted text, screened by
/// Jev; Cookie looks with its read tools and answers with a verdict; code holds the verdict to the signals (§5.4); the person is
/// asked <i>Was this you?</i>; and the notice goes through the executor, with Seraph. A strong signal whose notice doesn't go out
/// (Seraph rejects it, can't review it, the level isn't <i>Auto</i>) gets code's notice at once (§6.4, §6.5).
/// </summary>
public sealed class AccessJudge(IDbContextFactory<PnmtdDbContext> dbs, IServiceProvider services, CookieSettings settings, CookieModel model, CookieBrain brain,
    DecisionClient guard, IOptionsMonitor<AiOptions> ai, ILogger<AccessJudge> logger)
{
    public const int PerRound = 5;
    public const string DecisionKind = "access";
    public const string Instruction = "unusualLogin";

    public delegate void TraceLine(Guid? caseId, string kind, string title, string body, IReadOnlyList<string> areas);

    public async Task<AccessJudged> RunAsync(long roundId, UserRights rights, Pseudonyms p, ModelSettings models, string? provider, int toolCallsSoFar,
        TraceLine trace, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var waiting = await db.AccessReviews.AsNoTracking().Where(r => r.State == AccessReviewState.Waiting && r.CaseId != null)
            .Select(r => new { CaseId = r.CaseId!.Value, r.Strong, r.CreatedAt }).ToListAsync(ct);
        if (waiting.Count == 0)
        {
            return new AccessJudged(0, 0, 0, 0, false);
        }
        var order = waiting.GroupBy(w => w.CaseId).OrderByDescending(g => g.Any(w => w.Strong)).ThenBy(g => g.Min(w => w.CreatedAt)).Select(g => g.Key).ToList();
        var cases = await db.CookieCases.Where(c => order.Contains(c.Id) && c.Open && c.Kind == CookieCaseKind.Access).ToListAsync(ct);
        var mine = order.Select(id => cases.FirstOrDefault(c => c.Id == id)).OfType<CookieCaseEntity>()
            .Where(c => CookieAccess.CanSee(rights, c.Areas) && c.StoppedAt is null).ToList();
        if (mine.Count < cases.Count)
        {
            var missing = cases.Count - mine.Count;
            trace(null, "access", $"{missing} access {(missing == 1 ? "case" : "cases")} Cookie can't judge",
                "Cookie's group needs Monitoring: Edit for servers' and routers' logins, and Audit log: View for PNMT's sign-ins; a stopped case waits. Code's notice covers their strong signals.",
                [CookieAccess.LoginsArea]);
        }
        int calls = 0, toolCalls = 0, worked = 0;
        decimal cost = 0;
        foreach (var c in mine.Take(PerRound))
        {
            try
            {
                var (k, t, cc) = await JudgeAsync(db, c, roundId, rights, p, models, provider, toolCallsSoFar + toolCalls, trace, ct);
                calls += k;
                toolCalls += t;
                cost += cc;
                worked++;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One case that fails doesn't stop the round: code's notice covers it after 10 minutes (§6.5).
                logger.LogWarning(e, "Cookie couldn't judge the access case {Case}", c.Id);
                trace(c.Id, "error", $"Couldn't judge: {c.Title}", e.Message, c.Areas);
            }
        }
        return new AccessJudged(worked, calls, toolCalls, cost, mine.Count > PerRound);
    }

    private async Task<(int Calls, int ToolCalls, decimal Cost)> JudgeAsync(PnmtdDbContext db, CookieCaseEntity c, long roundId, UserRights rights, Pseudonyms p,
        ModelSettings models, string? provider, int toolCallsSoFar, TraceLine trace, CancellationToken ct)
    {
        int calls = 0, toolCalls = 0;
        decimal cost = 0;
        var s = await settings.GetAsync<AccessSettings>(ct);
        var reviews = await db.AccessReviews.Where(r => r.CaseId == c.Id && r.State == AccessReviewState.Waiting).OrderBy(r => r.At).Take(20).ToListAsync(ct);
        if (reviews.Count == 0)
        {
            return (0, 0, 0);
        }
        var weekAgo = Clock.UtcNow.AddDays(-7);
        var judged = await db.AccessReviews.AsNoTracking().Where(r => r.CaseId == c.Id && r.State != AccessReviewState.Waiting && r.CreatedAt >= weekAgo)
            .OrderByDescending(r => r.At).Take(10).ToListAsync(ct);
        var (brief, untrusted) = await BriefAsync(db, c, reviews, judged, p, ct);

        // Screening (§9): Jev on the outside text; a flagged login is at least suspicious, whatever Cookie says.
        var flagged = reviews.Any(r => r.Flagged);
        var o = ai.CurrentValue;
        if (untrusted.Count > 0 && o.Guard.Configured)
        {
            var decided = await guard.DecideAsync(new AiCall(CookieModel.PurposePrefix + "screen"), untrusted.Distinct().Take(40).ToList(),
                new Dictionary<string, DecisionQuestion> { ["injection"] = DecisionQuestion.Noul(AiPipeline.InjectionQuestion) }, ct);
            calls++;
            if ((decided.Answers["injection"].Probability ?? 0) >= o.Guard.Threshold)
            {
                flagged = true;
                foreach (var r in reviews)
                {
                    r.Flagged = true;
                }
                trace(c.Id, "screen", "Jev flagged the logins' text: at least suspicious", string.Join("\n", untrusted.Take(10)), c.Areas);
            }
        }

        var values = await brain.ValuesAsync(ct);
        var system = await brain.BaseAsync(values, ct);
        var instructions = await brain.InstructionsAsync(c, Instruction, [], false, values, ct);
        var context = new ToolContext(services, rights, p);
        context.Areas.UnionWith(c.Areas);
        var tools = CookieTools.For(rights);
        var definitions = CookieTools.Definitions(tools);
        var caseText = $"The access case:\n{brief}";
        var work = new JsonArray(Message("system", $"{system}\n\n{instructions}"),
            Message("user", $"{caseText}\n\nLook at what you need with your tools: the account's history, its other logins, the tickets, the knowledge base, the plan. " +
                "When you have looked enough, say in a few sentences what you found, without calling more tools."));
        var findings = "";
        for (var step = 0; step < 8; step++)
        {
            var allow = models.MaxToolCalls == 0 || toolCallsSoFar + toolCalls < models.MaxToolCalls;
            var turn = await model.CompleteAsync("access", work, allow ? definitions : null, null, models.WorkEffort, provider, 6000, ct);
            calls++;
            cost += turn.Cost;
            provider ??= turn.Provider;
            if (!string.IsNullOrWhiteSpace(turn.Reasoning))
            {
                trace(c.Id, "reasoning", "Thinking", turn.Reasoning!, [.. context.Areas]);
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
                var result = await CookieTools.RunAsync(call.Name, call.Arguments, context, ct);
                trace(c.Id, "tool", $"{call.Name}({CaseBook.Cut(call.Arguments, 200)})", CaseBook.Cut(result, 4000)!, [.. context.Areas]);
                work.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = result });
            }
            if (!allow)
            {
                work.Add(Message("user", "No more tools in this round. Say in a few sentences what you found."));
            }
        }

        var verdictMessages = new JsonArray(Message("system", $"{system}\n\n{instructions}"), Message("user", caseText),
            Message("assistant", string.IsNullOrWhiteSpace(findings) ? "(I found nothing more.)" : findings),
            Message("user", "Now give your verdict on these logins: normal, unusual, suspicious or alarming, how sure you are, one sentence for the people " +
                "who look after access, and your case note."));
        var answerTurn = await model.CompleteAsync("access-verdict", verdictMessages, null, (nameof(AccessVerdictAnswer), AiJson.SchemaFor<AccessVerdictAnswer>()),
            models.WorkEffort, provider, 3000, ct);
        calls++;
        cost += answerTurn.Cost;
        var answer = Parse(answerTurn);

        var strong = reviews.Any(r => r.Strong);
        var late = reviews.All(r => r.Late);
        var verdict = AccessSignals.Hold(answer.Verdict, answer.Confidence, strong, flagged, late);
        var actions = await settings.GetAsync<ActionSettings>(ct);
        var rule = actions.Levels.AccessNotice;
        var noticeVerdict = answer.Confidence < rule.MinConfidence && verdict > AccessVerdict.Unusual ? AccessSignals.Lower(verdict) : verdict;
        var held = verdict != answer.Verdict ? $" (Cookie said {AccessSignals.Name(answer.Verdict)}; held to the signals: {AccessSignals.Name(verdict)})" : "";

        var now = Clock.UtcNow;
        var reason = CaseBook.Cut(p.Unmask(answer.Reason?.Trim()), 300) ?? "";
        var log = new CookieDecisionEntity
        {
            At = now, CaseId = c.Id, RoundId = roundId, Kind = DecisionKind, Title = CaseBook.Cut($"Access: {AccessSignals.Name(verdict)} · {c.Title}", 300)!,
            Target = $"case:{c.Id}", Proposal = JsonSerializer.Serialize(answer, AiJson.Options), Confidence = Math.Clamp(answer.Confidence, 0, 1),
            Outcome = AccessSignals.Name(verdict), Detail = CaseBook.Cut($"{reason}{held}", 1000), Areas = [.. context.Areas.Distinct().Order()],
        };
        db.CookieDecisions.Add(log);
        await db.SaveChangesAsync(ct);
        foreach (var r in reviews)
        {
            r.State = AccessSignals.StateOf(verdict);
            r.Confidence = log.Confidence;
            r.Reason = reason;
            r.JudgedBy = "cookie";
            r.JudgedAt = now;
            r.DecisionId = log.Id;
        }
        var before = AccessSignals.VerdictOf(c.AccessVerdict);
        var highest = before is { } b && b > verdict ? b : verdict;
        c.AccessVerdict = AccessSignals.Name(highest);
        c.CookieNote = CaseBook.Cut(answer.Note?.Trim(), 2000);
        c.CookieNoteAt = now;
        c.CookieNoteAreas = [.. context.Areas.Order()];
        c.CookieUrgency = verdict >= AccessVerdict.Suspicious ? "urgent" : verdict == AccessVerdict.Unusual ? "important" : "info";
        c.ChangedAt = now;
        CaseBook.Entry(db, c.Id, now, "verdict", $"{AccessNotices.Label(verdict)} ({answer.Confidence:0.00}){held}: {reason}", "cookie");
        trace(c.Id, "verdict", $"Access: {AccessSignals.Name(verdict)} · {c.Title}", $"{p.Mask(reason)}{held}\n\n{answer.Note}", c.CookieNoteAreas);
        if (highest == AccessVerdict.Normal && c.AccessAnswer is null)
        {
            c.Open = false;
            c.ClosedAt = now;
            CaseBook.Entry(db, c.Id, now, "closed", "Cookie judged the logins normal.", "cookie");
        }
        await db.SaveChangesAsync(ct);
        if (verdict == AccessVerdict.Normal)
        {
            return (calls, toolCalls, cost);
        }

        if (await services.GetRequiredService<WasThisYou>().AskAsync(c.Id, verdict, ct) is { } asked)
        {
            trace(c.Id, "was-this-you", "Was this you?", asked, c.Areas);
        }

        // The notice (§6.4): through the executor and Seraph; its recipients are the Access tab's, never the model's.
        var (subjectVerdict, proposal) = (noticeVerdict, Notice(c, noticeVerdict, answer, strong));
        async Task<ProposedAction?> Retry(string why, CancellationToken token)
        {
            var again = new JsonArray([.. verdictMessages.Select(m => m!.DeepClone())]);
            again.Add(Message("assistant", answerTurn.Content ?? ""));
            again.Add(Message("user", $"Seraph, the reviewer, sent your notice back: {why}\nGive your verdict again, with that corrected."));
            var turn = await model.CompleteAsync("access-verdict", again, null, (nameof(AccessVerdictAnswer), AiJson.SchemaFor<AccessVerdictAnswer>()),
                models.WorkEffort, provider, 3000, token);
            calls++;
            cost += turn.Cost;
            var second = Parse(turn);
            var v = AccessSignals.Hold(second.Verdict, second.Confidence, strong, flagged, late);
            return v == AccessVerdict.Normal ? null : Notice(c, second.Confidence < rule.MinConfidence && v > AccessVerdict.Unusual ? AccessSignals.Lower(v) : v, second, strong);
        }
        var executor = services.GetRequiredService<CookieExecutor>();
        var decision = await executor.HandleAsync(new ActionRequest(new CookieProposal(proposal, Access: new AccessNoticePayload(c.Id, AccessSignals.Name(subjectVerdict), strong)),
            c, roundId, rights, p, [.. context.Areas], Retry), ct);
        trace(c.Id, "proposal", $"{decision.Title}: {decision.Outcome}",
            (decision.Verdict is null ? "" : $"Seraph: {decision.Verdict}, {decision.VerdictReason}\n") + (decision.Detail ?? ""), c.Areas);
        if (strong && s.CodeNotice && decision.Outcome is "rejected" or "off" or "proposed" or "failed" or "revised")
        {
            var strongCount = reviews.SelectMany(AccessWatch.SignalsOf).Count(x => x.Strength == SignalStrength.Strong);
            var code = AccessSignals.CodeVerdict(strongCount, flagged, late);
            var sent = await services.GetRequiredService<AccessNotices>().NotifyAsync(c.Id, code, null, "code", ct);
            trace(c.Id, "notice", $"Code's notice: {AccessSignals.Name(code)}", sent.Detail, c.Areas);
        }
        return (calls, toolCalls, cost);
    }

    private static ProposedAction Notice(CookieCaseEntity c, AccessVerdict verdict, AccessVerdictAnswer answer, bool strong) =>
        new(CookieActionKind.AccessNotice, null, 0, c.Id.ToString(), $"{AccessNotices.Label(verdict)} login: {c.Title}", answer.Reason ?? "", answer.Reason ?? "",
            answer.Evidence ?? [], Math.Clamp(answer.Confidence, 0, 1), verdict >= AccessVerdict.Suspicious || strong ? Urgency.Urgent : Urgency.Important);

    private static AccessVerdictAnswer Parse(ModelTurn turn)
    {
        try
        {
            return JsonSerializer.Deserialize<AccessVerdictAnswer>(turn.Content ?? "", AiJson.Options)
                ?? throw new AiRequestException(AiOutcome.InvalidOutput, "Cookie's verdict is empty.");
        }
        catch (JsonException e)
        {
            throw new AiRequestException(AiOutcome.InvalidOutput, $"Cookie's verdict isn't the JSON asked for: {e.Message}");
        }
    }

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    private static string Untrusted(string source, string? text, Pseudonyms p, List<string> untrusted, int max = 200)
    {
        var clean = p.Mask(InputSanitizer.Clean(text, max).Text);
        untrusted.Add(clean);
        return $"<untrusted source=\"{source}\">{clean}</untrusted>";
    }

    /// <summary>The case as Cookie reads it (§5.2): the target, each login with its signals, the person, and the context. Masked; outside text wrapped.</summary>
    private static async Task<(string Brief, List<string> Untrusted)> BriefAsync(PnmtdDbContext db, CookieCaseEntity c, List<AccessReviewEntity> reviews,
        List<AccessReviewEntity> judged, Pseudonyms p, CancellationToken ct)
    {
        var untrusted = new List<string>();
        var first = reviews[0];
        object? target = null;
        string? hostNotes = null;
        var maintenance = new List<string>();
        var tickets = new List<string>();
        if (first.Target != AccessTargetKind.Pnmt && c.HostId is { } hostId)
        {
            var host = await db.Hosts.AsNoTracking().Where(h => h.Id == hostId)
                .Select(h => new { h.Name, h.Importance, h.Notes, Location = h.AssignedLocation != null ? h.AssignedLocation.Name : h.Location }).SingleOrDefaultAsync(ct);
            var agent = first.SensorId is { } sid ? await db.Agents.AsNoTracking().Where(a => a.SensorId == sid).Select(a => a.OsCaption).FirstOrDefaultAsync(ct) : null;
            var router = first.SensorId is { } rid ? await db.MikrotikRouters.AsNoTracking().Where(r => r.SensorId == rid).Select(r => new { r.Version, r.Identity }).FirstOrDefaultAsync(ct) : null;
            target = new { Kind = first.Target.ToString(), Name = host?.Name, host?.Location, host?.Importance, Os = agent ?? (router is null ? null : $"RouterOS {router.Version}") };
            hostNotes = string.IsNullOrWhiteSpace(host?.Notes) ? null : Untrusted("host notes", host.Notes, p, untrusted, 1000);
            var now = Clock.UtcNow;
            var hostText = hostId.ToString();
            var locationText = c.LocationId?.ToString();
            maintenance = (await db.CookieInstructions.AsNoTracking().Where(i => i.Kind == "mute" && i.Confirmed && i.EndedAt == null && i.Until > now
                    && (i.Scope == "host" && i.Value == hostText || i.Scope == "location" && i.Value == locationText)).ToListAsync(ct))
                .Select(i => $"Muted until {Clock.ToLocal(i.Until):yyyy-MM-dd HH:mm} by {p.Mask(i.CreatedByName)}: {Untrusted("mute", i.Text, p, untrusted, 300)}").ToList();
            var linked = await db.RedmineLinks.AsNoTracking().Where(l => l.HostId == hostId).Select(l => l.IssueId).ToListAsync(ct);
            tickets = (await db.RedmineIssues.AsNoTracking().Where(i => linked.Contains(i.IssueId) && !i.IsClosed).Take(10).ToListAsync(ct))
                .Select(i => $"#{i.IssueId}: {Untrusted("ticket subject", i.Subject, p, untrusted, 200)}").ToList();
        }
        else
        {
            target = new { Kind = "PNMT", Name = AccessWatch.PnmtName };
        }
        var fingerprints = reviews.Select(r => r.KeyFingerprint).OfType<string>().Distinct().ToList();
        var keys = await db.SshKeys.AsNoTracking().Where(k => fingerprints.Contains(k.Fingerprint)).ToDictionaryAsync(k => k.Fingerprint, ct);
        var account = first.Account;
        var known = (await db.AccessKnown.AsNoTracking().Where(k => k.EndedAt == null).ToListAsync(ct))
            .Where(k => k.Account is null || AccessSignals.SameAccount(k.Account, account))
            .Select(k => $"{k.Label}: {(k.Account is null ? "any account" : p.Mask(k.Account))} from {k.Source ?? "anywhere"}, {(k.Groups.Count == 0 ? "any way" : string.Join(", ", k.Groups))}" +
                $"{(k.Until is { } u ? $", until {Clock.ToLocal(u):yyyy-MM-dd}" : "")}: {Untrusted("comment", k.Comment, p, untrusted, 200)}").Take(10).ToList();
        var earlier = await db.CookieCases.AsNoTracking().Where(x => x.Key == c.Key && x.Id != c.Id && x.AccessAnswer != null).OrderByDescending(x => x.ClosedAt)
            .Take(5).Select(x => new { x.AccessAnswer, x.AccessAnsweredAt, x.AccessVerdict }).ToListAsync(ct);
        string? person = null;
        if (first.PersonId is { } personId && await db.Users.AsNoTracking().Where(u => u.Id == personId).Select(u => new { u.DisplayName, u.IsActive }).FirstOrDefaultAsync(ct) is { } u)
        {
            person = $"{p.Mask(u.DisplayName)}, a PNMT user{(u.IsActive ? "" : " (deactivated)")}";
        }
        var brief = new
        {
            Case = c.Id,
            Target = target,
            Account = Untrusted("account", account, p, untrusted, 200),
            Shared = AccessSignals.Shared(account),
            Person = person ?? (AccessSignals.Shared(account) ? "a shared account: nobody, unless a key says who" : "no PNMT user is linked to the account"),
            Logins = reviews.Select(r => new
            {
                r.Id,
                At = Clock.ToLocal(r.At).ToString("yyyy-MM-dd HH:mm ddd", CultureInfo.InvariantCulture),
                Admin = r.IsAdmin,
                Way = AccessWatch.Way(r.Way),
                r.Source,
                Public = AccessSignals.IsPublic(r.Source),
                Device = r.Device is null ? null : Untrusted("device", r.Device, p, untrusted, 200),
                Key = r.KeyFingerprint is { } f ? keys.TryGetValue(f, out var key) ? $"{Untrusted("key name", key.Name, p, untrusted, 100)}{(key.State == SshKeyState.Retired ? " (retired)" : "")}" : $"{f}, unknown to PNMT" : null,
                BecameUser = r.TargetUser is null ? null : Untrusted("target user", r.TargetUser, p, untrusted, 100),
                r.Late,
                Signals = AccessWatch.SignalsOf(r).Select(x => $"{x.Strength}: {p.Mask(x.Text)}"),
            }),
            EarlierInThisCase = judged.Select(r => $"{Clock.ToLocal(r.At):yyyy-MM-dd HH:mm} {r.State}: {p.Mask(r.Reason)}"),
            EarlierAnswers = earlier.Select(e => $"{e.AccessAnsweredAt:yyyy-MM-dd}: judged {e.AccessVerdict}, answered {e.AccessAnswer}"),
            KnownAccess = known,
            Maintenance = maintenance,
            OpenTickets = tickets,
            HostNotes = hostNotes,
        };
        return (CookieBrain.Json(brief), untrusted);
    }

    /// <summary>The evidence for Seraph (§6.4), read again by code: each login and its signals, masked.</summary>
    public static async Task<string> EvidenceAsync(PnmtdDbContext db, Guid caseId, Pseudonyms privacy, CancellationToken ct)
    {
        var reviews = await db.AccessReviews.AsNoTracking().Where(r => r.CaseId == caseId).OrderByDescending(r => r.At).Take(10).ToListAsync(ct);
        var text = new StringBuilder("The logins, as code read them:\n");
        foreach (var r in reviews)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {privacy.Mask(AccessWatch.Describe(r))}; {r.State}");
            foreach (var signal in AccessWatch.SignalsOf(r))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  · {signal.Strength}: {privacy.Mask(signal.Text)}");
            }
        }
        return text.ToString();
    }
}
