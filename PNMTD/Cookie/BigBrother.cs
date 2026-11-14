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
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>The big brother's solution (docs/cookie-plan.md §15.4), in a fixed schema.</summary>
public sealed record Solution(
    [property: Description("The most likely cause, at most 400 characters")] string Cause,
    [property: Description("How sure, 0 to 1")] double Confidence,
    [property: Description("The evidence for it: what you saw, with ids")] IReadOnlyList<string> Evidence,
    [property: Description("Causes you couldn't rule out")] IReadOnlyList<string> Alternatives,
    [property: Description("The fix as steps for a person, in order; commands written out; a step that interrupts service or can't be undone says so")] IReadOnlyList<string> Steps,
    [property: Description("How to check that it worked")] string Verify,
    [property: Description("What to watch afterwards")] string Watch);

/// <summary>
/// The big brother (docs/cookie-plan.md §15, §21.1): two strong models; for each call one of them at random, the other for a
/// second call on the same case and when the first fails. It reads with Cookie's group's rights and the read tools, up to
/// 60 tool calls, and returns a solution. It advises only: the solution goes into the case, and then as a proposal (a
/// private note on the case's ticket, or a ticket) through the executor and Seraph. Its own budget and limits; a limit
/// reached sends the case to a person instead.
/// </summary>
public sealed class BigBrother(IDbContextFactory<PnmtdDbContext> dbs, CookieModel model, CookieBrain brain, CookieSettings settings, CookieExecutor executor,
    Instructions instructions, IOptionsMonitor<CookieOptions> options, IServiceProvider services, ILogger<BigBrother> logger)
{
    public const int MaxToolCalls = 60;

    public const string Kind = "big-brother";

    /// <summary>
    /// Whether code offers the case (§15.2, conditions 2 and 3): it matters (importance High, a code status of Warning or Alert
    /// that names it, or a ticket's missed deadline), and nobody has taken care of it for a working day.
    /// </summary>
    public static bool Offered(CookieCaseEntity c, CookieRuntimeEntity runtime, bool missedDeadline, WorkingCalendar calendar, DateTime now)
    {
        if (!c.Open || c.StoppedAt is not null || c.Kind == CookieCaseKind.Request)
        {
            return false;
        }
        var matters = c.Importance == Importance.High || missedDeadline
            || runtime.CodeStatus >= OverallState.Warning && runtime.CodeCases.Contains(c.Id);
        var oneDay = calendar.AlwaysOpen ? TimeSpan.FromHours(24) : TimeSpan.FromHours(8);
        return matters && calendar.WorkingTimeBetween(c.ProgressAt, now) >= oneDay;
    }

    /// <summary>Why the big brother can't be called now, or null: the switch, the limits of §15.5, mute.</summary>
    public async Task<string?> BlockedAsync(CookieCaseEntity c, CancellationToken ct)
    {
        var actions = await settings.GetAsync<ActionSettings>(ct);
        if (!actions.BigBrother)
        {
            return "The big brother is off on Admin → Cookie → Actions.";
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var today = Clock.ToUtc(Clock.LocalNow.Date);
        var calls = db.CookieDecisions.AsNoTracking().Where(d => d.Kind == Kind && d.At >= today && d.Outcome != "refused");
        if (await calls.CountAsync(ct) >= actions.Limits.BigBrotherPerDay)
        {
            return $"The big brother: {actions.Limits.BigBrotherPerDay} calls a day.";
        }
        if (await calls.CountAsync(d => d.CaseId == c.Id, ct) >= actions.Limits.BigBrotherPerCaseDay)
        {
            return $"The big brother: {actions.Limits.BigBrotherPerCaseDay} calls a day per case.";
        }
        var members = JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? [];
        if (Instructions.Blocks(await instructions.ActiveAsync(ct), CookieActionKind.HandToPerson, c, members, bigBrother: true) is { } muted)
        {
            return $"Muted: {muted.Kind} {muted.Scope} {muted.Label ?? muted.Value}, by {muted.CreatedByName}.";
        }
        return null;
    }

    /// <summary>
    /// The call (§15.3, §15.4). Afterwards the solution is in the case, and its proposal went through the executor; or, when
    /// it couldn't be called or both models failed, the case went to a person.
    /// </summary>
    public async Task<CookieDecisionEntity> SolveAsync(CookieCaseEntity c, string question, long? roundId, UserRights rights, Pseudonyms p, string? askedBy,
        CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var log = new CookieDecisionEntity
        {
            At = Clock.UtcNow, CaseId = c.Id, RoundId = roundId, Kind = Kind, Title = CaseBook.Cut($"Big brother: {c.Title}", 300)!, Target = $"case:{c.Id}",
            Proposal = JsonSerializer.Serialize(new { question = p.Mask(question), askedBy }, AiJson.Options), Areas = [.. c.Areas],
        };
        db.CookieDecisions.Add(log);
        var blocked = await BlockedAsync(c, ct);
        if (blocked is not null)
        {
            log.Outcome = "refused";
            log.Detail = blocked;
            await db.SaveChangesAsync(ct);
            await HandToPersonAsync(c, $"Would need the big brother: {p.Mask(question)}", blocked, roundId, rights, p, ct);
            return log;
        }
        var o = options.CurrentValue.BigBrother;
        var models = await settings.GetAsync<ModelSettings>(ct);
        var primary = new ModelChoice(string.IsNullOrWhiteSpace(models.BigBrotherPrimary) ? o.Primary.Model : models.BigBrotherPrimary.Trim(), o.Primary.Providers,
            null, AiRole.Main);
        var backup = new ModelChoice(string.IsNullOrWhiteSpace(models.BigBrotherBackup) ? o.Backup.Model : models.BigBrotherBackup.Trim(), o.Backup.Providers,
            null, AiRole.Main);
        // A second call on the same case goes to the other model (§15.3); the first is a coin's.
        var last = await db.CookieDecisions.AsNoTracking().Where(d => d.CaseId == c.Id && d.Kind == Kind && d.Model != null).OrderByDescending(d => d.Id)
            .Select(d => d.Model).FirstOrDefaultAsync(ct);
        var first = last == primary.Model ? backup : last == backup.Model ? primary : Random.Shared.Next(2) == 0 ? primary : backup;
        var order = first == primary ? new[] { primary, backup } : [backup, primary];
        Solution? solution = null;
        string? used = null;
        var errors = new List<string>();
        foreach (var choice in order.Where(x => !string.IsNullOrWhiteSpace(x.Model)))
        {
            try
            {
                solution = await CallAsync(c, question, choice, rights, p, ct);
                used = choice.Model;
                break;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("The big brother ({Model}) failed: {Error}", choice.Model, e.Message);
                errors.Add($"{choice.Model}: {e.Message}");
            }
        }
        log.Model = used;
        if (solution is null)
        {
            log.Outcome = "failed";
            log.Detail = CaseBook.Cut($"The big brother couldn't be reached: {string.Join("; ", errors)}", 1000);
            await db.SaveChangesAsync(ct);
            await HandToPersonAsync(c, $"Would need the big brother: {p.Mask(question)}", "The big brother couldn't be reached.", roundId, rights, p, ct);
            return log;
        }
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var text = Format(solution, identity.Name, used!);
        log.Outcome = "done";
        log.Confidence = solution.Confidence;
        log.Detail = CaseBook.Cut(p.Unmask(solution.Cause), 1000);
        var tracked = await db.CookieCases.SingleAsync(x => x.Id == c.Id, ct);
        tracked.Solution = p.Unmask(text);
        tracked.SolutionAt = Clock.UtcNow;
        tracked.SolutionModel = used;
        tracked.BigBrotherAskedAt = null;
        tracked.BigBrotherAskedBy = null;
        CaseBook.Entry(db, c.Id, Clock.UtcNow, "solution", CaseBook.Cut($"The big brother ({used}): {p.Unmask(solution.Cause)}", 2000)!, "cookie");
        await db.SaveChangesAsync(ct);

        // Where it goes (§15.4): a private note on the case's ticket, or a ticket with it; both proposals, after Seraph.
        var issue = c.IssueId ?? c.OwnIssueId;
        var action = issue is { } i
            ? new ProposedAction(CookieActionKind.PrivateNote, null, i, null, $"A solution for {c.Title}", text, "The big brother's solution for the person working on it.",
                [c.Id.ToString()], solution.Confidence, Urgency.Important)
            : new ProposedAction(CookieActionKind.Ticket, null, 0, null, CaseBook.Cut($"{c.Title}: a proposed fix", 120)!, text,
                "The big brother's solution, for whoever takes it on.", [c.Id.ToString()], solution.Confidence, Urgency.Important);
        await executor.HandleAsync(new ActionRequest(new CookieProposal(action), tracked, roundId, rights, p, c.Areas), ct);
        return log;
    }

    private async Task<Solution> CallAsync(CookieCaseEntity c, string question, ModelChoice choice, UserRights rights, Pseudonyms p, CancellationToken ct)
    {
        var values = await brain.ValuesAsync(ct);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var entries = await db.CookieCaseEntries.AsNoTracking().Where(e => e.CaseId == c.Id).OrderByDescending(e => e.At).Take(30).ToListAsync(ct);
        var members = JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? [];
        values["question"] = p.Mask(question);
        values["case"] = CookieBrain.Json(new
        {
            c.Id, c.Kind, Title = p.Mask(c.Title), c.Importance, c.OpenedAt, c.ProgressAt, c.HostId, c.LocationId, c.IssueId,
            Members = members.Select(m => new { m.Type, m.Id, m.Name, m.Since, m.Code, m.Severity, Message = m.Message is null ? null : $"<untrusted>{p.Mask(InputSanitizer.Clean(m.Message, 300).Text)}</untrusted>" }),
            Timeline = entries.AsEnumerable().Reverse().Select(e => $"{e.At:u} {e.Kind}: {p.Mask(InputSanitizer.Clean(e.Text, 300).Text)}"),
            CookiesNote = p.Mask(c.CookieNote),
        });
        var context = new ToolContext(services, rights, p);
        var tools = CookieTools.Definitions(CookieTools.For(rights));
        var work = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = CookiePrompts.Fill(await brain.PromptAsync("bigBrother", ct), values) },
            new JsonObject { ["role"] = "user", ["content"] = "Look at what you need with the tools, then say what you found, without calling more tools." });
        var toolCalls = 0;
        var findings = "";
        string? provider = null;
        for (var step = 0; step < 30; step++)
        {
            var allow = toolCalls < MaxToolCalls;
            var turn = await model.CompleteAsync("bigbrother", work, allow ? tools : null, null, "high", provider, 8000, ct, choice);
            provider ??= turn.Provider;
            if (turn.ToolCalls.Count == 0)
            {
                findings = turn.Content ?? "";
                break;
            }
            work.Add(turn.AssistantMessage);
            foreach (var call in turn.ToolCalls)
            {
                toolCalls++;
                work.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = await CookieTools.RunAsync(call.Name, call.Arguments, context, ct) });
            }
            if (toolCalls >= MaxToolCalls)
            {
                work.Add(new JsonObject { ["role"] = "user", ["content"] = "No more tools: say what you found." });
            }
        }
        var final = new JsonArray(work[0]!.DeepClone(), new JsonObject { ["role"] = "assistant", ["content"] = findings },
            new JsonObject { ["role"] = "user", ["content"] = "Now give your solution." });
        var answer = await model.CompleteAsync("bigbrother.solution", final, null, (nameof(Solution), AiJson.SchemaFor<Solution>()), "high", provider, 4000, ct, choice);
        return JsonSerializer.Deserialize<Solution>(answer.Content ?? "", AiJson.Options) ?? throw new AiRequestException(AiOutcome.InvalidOutput, "The solution is empty.");
    }

    /// <summary>The solution as text, signed with Cookie's name and the model's (§15.4).</summary>
    public static string Format(Solution s, string name, string model)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Most likely cause ({s.Confidence:0%} sure): {s.Cause}");
        if (s.Evidence.Count > 0)
        {
            text.AppendLine("Evidence: " + string.Join("; ", s.Evidence));
        }
        if (s.Alternatives.Count > 0)
        {
            text.AppendLine("Not ruled out: " + string.Join("; ", s.Alternatives));
        }
        text.AppendLine();
        text.AppendLine("The fix:");
        foreach (var (step, i) in s.Steps.Select((x, i) => (x, i + 1)))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{i}. {step}");
        }
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Check: {s.Verify}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Watch: {s.Watch}");
        text.AppendLine();
        text.Append(CultureInfo.InvariantCulture, $"Proposed by {name}'s big brother ({model}); nothing was run.");
        return CaseBook.Cut(text.ToString(), 1500)!;
    }

    private async Task HandToPersonAsync(CookieCaseEntity c, string text, string why, long? roundId, UserRights rights, Pseudonyms p, CancellationToken ct) =>
        await executor.HandleAsync(new ActionRequest(new CookieProposal(new ProposedAction(CookieActionKind.HandToPerson, null, 0, null, c.Title, text, why,
            [c.Id.ToString()], 1, Urgency.Important)), c, roundId, rights, p, c.Areas), ct);
}
