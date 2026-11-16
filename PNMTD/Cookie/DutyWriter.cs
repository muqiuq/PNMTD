using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>Cookie's text for a reminder or an escalation (§21.1): the same four parts as the template's.</summary>
public sealed record DutyTextAnswer(
    [property: Description("The subject of the e-mail, at most 120 characters")] string Subject,
    [property: Description("The Pushover message, at most 200 characters")] string Short,
    [property: Description("The e-mail to the people, at most 1200 characters")] string Message,
    [property: Description("The private note on the ticket, to staff, at most 600 characters")] string Note);

/// <summary>
/// Cookie writes the reminders' and escalations' texts (docs/cookie-plan.md §3.4 C, §21.1) when <i>Actions → Cookie writes the
/// duties' texts</i> is on and Cookie is running. Code still decides when, to whom and through which limits; Seraph reviews
/// the text; <b>the template is used whenever Cookie is stopped, paused, slow or rejected.</b>
/// </summary>
public sealed class DutyWriter(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, CookieModel model, CookieBrain brain, PrivacyFilter privacy,
    Seraph seraph, IOptionsMonitor<CookieOptions> options, ILogger<DutyWriter> logger)
{
    public static readonly TimeSpan MaxTime = TimeSpan.FromSeconds(90);

    public async Task<DutyTexts.Text> WriteAsync(CookieCaseEntity c, CookieDutyEntity duty, DutyTexts.Text template, CancellationToken ct)
    {
        if (!(await settings.GetAsync<ActionSettings>(ct)).CookieWritesDuties)
        {
            return template;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        if (runtime is not { Running: true } || runtime.State == CookieState.Paused)
        {
            return template;
        }
        var log = new CookieDecisionEntity
        {
            At = Clock.UtcNow, CaseId = c.Id, Kind = "duty-text", Title = CaseBook.Cut($"Text for {duty.Kind}: {c.Title}", 300)!, Target = $"duty:{duty.Id}",
            Areas = [.. c.Areas],
        };
        db.CookieDecisions.Add(log);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MaxTime);
        try
        {
            var p = await privacy.OpenAsync(timeout.Token);
            try
            {
                var values = await brain.ValuesAsync(timeout.Token);
                var facts = new
                {
                    Duty = duty.Kind.ToString(), DueAt = duty.DueAt, Ticket = c.IssueId, Title = p.Mask(c.Title), Assignee = p.Mask(c.AssigneeName), c.StatusName,
                    c.TicketCreatedAt, c.FirstAnswerAt, c.LastStaffUpdateAt, YourNote = p.Mask(c.CookieNote),
                    Template = new { Subject = p.Mask(template.Subject), Short = p.Mask(template.Short), Message = p.Mask(template.Message), Note = p.Mask(template.Note) },
                };
                var messages = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = await brain.BaseAsync(values, timeout.Token) },
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "Write the texts for this reminder or escalation. Code decided that it goes out now and to whom; you only write. " +
                            "Keep every fact of the template (the deadline, the ticket, who it is assigned to, the links); say it plainly and briefly, " +
                            $"and sign as {values["name"]}.\n\n{CookieBrain.Json(facts)}",
                    });
                var turn = await model.CompleteAsync("duty", messages, null, (nameof(DutyTextAnswer), AiJson.SchemaFor<DutyTextAnswer>()), "low", null,
                    options.CurrentValue.Tokens.Duty, timeout.Token);
                var answer = JsonSerializer.Deserialize<DutyTextAnswer>(turn.Content ?? "", AiJson.Options)
                    ?? throw new AiRequestException(AiOutcome.InvalidOutput, "The answer is empty.");
                var proposal = new CookieProposal(new ProposedAction(CookieActionKind.Email, null, c.IssueId ?? 0, null, answer.Subject,
                    $"{answer.Message}\n\nPushover: {answer.Short}\n\nNote on the ticket: {answer.Note}", $"The {duty.Kind} that code sends now.", [c.Id.ToString()], 1,
                    CaseRules.UrgencyOf(duty.Kind)));
                log.Proposal = JsonSerializer.Serialize(proposal, AiJson.Options);
                var (verdict, seraphModel, error) = await seraph.ReviewAsync(proposal, CookieBrain.Json(facts), Instructions.PromptText([]), timeout.Token);
                log.Model = seraphModel;
                log.Verdict = verdict?.Verdict.ToString().ToLowerInvariant();
                log.VerdictReason = verdict?.Reason ?? error;
                if (verdict?.Verdict != SeraphVerdictKind.Approve)
                {
                    log.Outcome = "rejected";
                    log.Detail = "The template went out instead.";
                    return template;
                }
                log.Outcome = "done";
                log.Detail = "Cookie's text went out.";
                // The links and the window stay as code wrote them: they are in the template's message.
                return new DutyTexts.Text(CaseBook.Cut(p.Unmask(answer.Subject), 120)!, CaseBook.Cut(p.Unmask(answer.Short), 200)!,
                    CaseBook.Cut(p.Unmask(answer.Message), 1200)!, CaseBook.Cut(p.Unmask(answer.Note), 600)!);
            }
            finally
            {
                await p.SaveAsync(CancellationToken.None);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Cookie couldn't write the duty's text: {Error}", e.Message);
            log.Outcome = "failed";
            log.Detail = CaseBook.Cut($"The template went out instead: {e.Message}", 1000);
            return template;
        }
        finally
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
