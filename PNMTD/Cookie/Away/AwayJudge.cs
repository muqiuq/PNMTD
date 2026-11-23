using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Decisions;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Away;

/// <summary>How important a comment is (docs/cookie-redmine-tab-plan.md §6.4): high, normal or low, as the model writes it.</summary>
public enum AwayImportance { High, Normal, Low }

/// <summary>Cookie's verdict on the comments of one ticket while its assignee is away (docs/cookie-redmine-tab-plan.md §6.4).</summary>
public sealed record AwayCommentAnswer(
    [property: Description("High when someone should act before the assignee is back; Normal when it can wait; Low for thanks, \"any news?\", an automatic reply or a status note")]
    AwayImportance Importance,
    [property: Description("One sentence, at most 200 characters, quoting the comment: why")] string Reason,
    [property: Description("How sure you are, 0 to 1")] double Confidence);

/// <summary>
/// The away watch's judging, in Cookie's round before the cases (docs/cookie-redmine-tab-plan.md §6.4), like the mails: at most 5
/// tickets a round, their pending comments judged together in one structured call without tools; the scan and Jev screen the comments
/// first, and a flagged one is withheld, for code (§6.7). <i>High</i> is the action <i>away notice</i> through the executor and Seraph;
/// <i>normal</i> or <i>low</i> tells nobody. The model never names whom to tell.
/// </summary>
public sealed class AwayJudge(IDbContextFactory<PnmtdDbContext> dbs, CookieModel model, CookieBrain brain, CookieExecutor executor, CookieSettings settings,
    DecisionClient guard, IOptionsMonitor<AiOptions> ai, IOptionsMonitor<CookieOptions> options)
{
    public const int PerRound = 5;

    public const string Withheld = "Cookie didn't read it: it looks like it tries to instruct an AI.";

    public sealed record Result(int Judged, int Calls, decimal Cost, bool More);

    public async Task<Result> RunAsync(long roundId, UserRights rights, Pseudonyms p, Action<Guid?, string, string> trace, CancellationToken ct)
    {
        var actions = await settings.GetAsync<ActionSettings>(ct);
        if (!actions.Duties.AwayComments || actions.Levels.AwayNotice.Level == ActionLevel.Off)
        {
            return new Result(0, 0, 0, false);
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var pending = await db.CookieAwayComments.Where(a => a.State == AwayCommentState.Pending && a.DecisionId == null).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var tickets = pending.GroupBy(a => a.CaseId).ToList();
        int judged = 0, calls = 0;
        decimal cost = 0;
        foreach (var ticket in tickets.Take(PerRound))
        {
            try
            {
                var (j, c, k) = await OneAsync(db, [.. ticket], roundId, rights, p, trace, ct);
                judged += j;
                calls += c;
                cost += k;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One ticket that fails doesn't stop the round: after 30 minutes code handles its comments as without a verdict (§6.7).
                trace(ticket.Key, "Away: not judged", e.Message);
            }
        }
        return new Result(judged, calls, cost, tickets.Count > PerRound);
    }

    private async Task<(int Judged, int Calls, decimal Cost)> OneAsync(PnmtdDbContext db, List<CookieAwayCommentEntity> rows, long roundId, UserRights rights,
        Pseudonyms p, Action<Guid?, string, string> trace, CancellationToken ct)
    {
        int calls = 0;
        decimal cost = 0;
        var caseId = rows[0].CaseId;
        var c = await db.CookieCases.SingleOrDefaultAsync(x => x.Id == caseId, ct);
        var now = Clock.UtcNow;
        if (c is null)
        {
            return (0, 0, 0);
        }
        // The screen (§6.4): the scan and Jev read each comment, masked; a flagged one Cookie never reads.
        var o = ai.CurrentValue;
        var read = new List<CookieAwayCommentEntity>();
        foreach (var r in rows)
        {
            var text = InputSanitizer.Clean(r.Text, AwayRules.MaxText).Text;
            var scan = InjectionScanner.Check(text);
            double? injection = null;
            if (o.Guard.Configured)
            {
                var decided = await guard.DecideAsync(new AiCall(CookieModel.PurposePrefix + "away.screen"), [p.Mask(text)],
                    new Dictionary<string, DecisionQuestion> { ["injection"] = DecisionQuestion.Noul(AiPipeline.InjectionQuestion) }, ct);
                calls++;
                injection = decided.Answers["injection"].Probability;
            }
            if (scan.Score >= InjectionScanner.BlockScore || injection >= o.Guard.Threshold)
            {
                r.State = AwayCommentState.Withheld;
                r.Detail = Withheld;
                CaseBook.Entry(db, c.Id, now, "away", $"A comment by {r.AuthorName ?? "someone"} withheld: {Withheld}");
                trace(c.Id, "Away: withheld", $"#{r.IssueId}, {r.AuthorName}: the guard or the scan");
                continue;
            }
            read.Add(r);
        }
        await db.SaveChangesAsync(ct);
        if (read.Count == 0)
        {
            return (0, calls, cost);
        }

        var assigneeId = read[^1].AssigneeUserId;
        var assignee = await db.Users.AsNoTracking().Where(u => u.Id == assigneeId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        var values = await brain.ValuesAsync(ct);
        values["assignee"] = p.Mask(assignee ?? "the assignee");
        values["away_until"] = read[^1].AwayUntil is { } back ? back.ToString("dddd yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "further notice";
        var facts = new
        {
            Ticket = c.IssueId, Title = p.Mask(c.Title), c.StatusName, Importance = c.Importance.ToString(), Author = p.Mask(c.AuthorName), c.AuthorIsStaff,
            Assignee = p.Mask(assignee), AwayUntil = read[^1].AwayUntil,
        };
        var comments = string.Join("\n\n", read.Select(r =>
            $"Comment {r.Id} by {p.Mask(r.AuthorName ?? "someone")}{(r.AuthorRedmineId == c.AuthorId ? " (the requester)" : "")}, {Clock.ToLocal(r.CommentAt):ddd HH:mm}" +
            $"{(r.IsPrivate ? ", private" : "")}:\n<untrusted source=\"redmine-comment\">{p.Mask(InputSanitizer.Clean(r.Text, AwayRules.MaxText).Text)}</untrusted>"));
        var messages = new JsonArray(
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = $"{await brain.BaseAsync(values, ct)}\n\n{CookiePrompts.Fill(await brain.CaseInstructionAsync("awayComment", ct), values)}",
            },
            new JsonObject { ["role"] = "user", ["content"] = $"The ticket:\n{CookieBrain.Json(facts)}\n\nThe comments:\n{comments}" });
        var turn = await model.CompleteAsync("away", messages, null, (nameof(AwayCommentAnswer), AiJson.SchemaFor<AwayCommentAnswer>()), "low", null,
            options.CurrentValue.Tokens.AwayComment, ct);
        calls++;
        cost += turn.Cost;
        var answer = JsonSerializer.Deserialize<AwayCommentAnswer>(turn.Content ?? "", AiJson.Options)
            ?? throw new AiRequestException(AiOutcome.InvalidOutput, "The answer is empty.");
        var reason = CaseBook.Cut(p.Unmask(answer.Reason.Trim()), 200)!;
        var importance = answer.Importance.ToString().ToLowerInvariant();
        if (answer.Importance != AwayImportance.High)
        {
            foreach (var r in read)
            {
                r.State = AwayCommentState.NotImportant;
                r.Importance = importance;
                r.Confidence = answer.Confidence;
                r.Reason = reason;
                r.JudgedBy = "cookie";
                r.JudgedAt = now;
                r.Detail = "Nobody told.";
            }
            CaseBook.Entry(db, c.Id, now, "away", $"Cookie: {importance}, nobody told. {reason}", "cookie");
            await db.SaveChangesAsync(ct);
            trace(c.Id, $"Away: {importance}", $"#{c.IssueId}: {reason}");
            return (read.Count, calls, cost);
        }

        var proposal = new CookieProposal(
            new ProposedAction(CookieActionKind.AwayNotice, null, c.IssueId ?? 0, null, $"An important comment on #{c.IssueId} while {values["assignee"]} is away",
                answer.Reason, answer.Reason, [.. read.Select(r => r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))], answer.Confidence,
                Urgency.Important),
            Away: new AwayNoticePayload(c.Id, c.IssueId ?? 0, [.. read.Select(r => r.Id)]));
        var log = await executor.HandleAsync(new ActionRequest(proposal, c, roundId, rights, p, c.Areas), ct);
        trace(c.Id, $"Away: high, {log.Outcome}", $"#{c.IssueId}: {reason}{(log.Detail is null ? "" : $"\n{log.Detail}")}");
        // The executor told, or keeps it for a person; otherwise code's way applies (§6.7): muted, refused or rejected comments are recorded.
        foreach (var r in read)
        {
            // The executor wrote through its own context: what it set counts. The decision is saved now, so its id is known.
            await db.Entry(r).ReloadAsync(ct);
            if (r.State != AwayCommentState.Pending)
            {
                r.DecisionId ??= log.Id;
                continue;
            }
            r.Importance = "high";
            r.Confidence = answer.Confidence;
            r.Reason = reason;
            r.JudgedBy = "cookie";
            r.JudgedAt = now;
            r.DecisionId = log.Id;
            if (log.Outcome != "proposed")
            {
                r.State = AwayCommentState.Recorded;
                r.Detail = CaseBook.Cut($"The notice was {log.Outcome}: {log.Detail}", 500);
            }
            else
            {
                r.Detail = "Waits for a person in the decisions inbox.";
            }
        }
        await db.SaveChangesAsync(ct);
        return (read.Count, calls, cost);
    }
}
