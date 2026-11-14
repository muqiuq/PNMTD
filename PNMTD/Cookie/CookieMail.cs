using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Decisions;
using PNMTD.Lib.Helper;
using PNMTD.Mails;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>Cookie's verdict on an <i>AI every time</i> mail (docs/cookie-prompts.md §7.8).</summary>
public sealed record MailAnswer(
    [property: Description("ok when the mail reports success, fail when it reports a failure or a problem, unknown when you can't tell")] MailVerdict Outcome,
    [property: Description("At most 200 characters, quoting the mail where you can")] string Message,
    [property: Description("How sure you are, 0 to 1")] double Confidence);

/// <summary>
/// The mails of rules set to <i>AI every time</i>, in Cookie's round (docs/cookie-plan.md §12, §21.1): Jev screens each one,
/// Cookie decides ok, fail or unknown, and the event is the action <i>mail-event</i> through the executor, with Seraph in
/// place of package one's verification model. <i>Unknown</i>, a Jev hit, a rejection or a refusal becomes the inbox's
/// <i>The AI could not decide</i>; a proposal at <i>Propose</i> waits in the inbox as Cookie's. While Cookie is stopped or
/// paused, the mails wait.
/// </summary>
public sealed class CookieMail(IDbContextFactory<PnmtdDbContext> dbs, CookieModel model, CookieBrain brain, CookieExecutor executor, DecisionBook inbox,
    DecisionClient guard, IOptionsMonitor<AiOptions> ai)
{
    public const int PerRound = 5;

    public sealed record Result(int Done, int Calls, decimal Cost, bool More);

    public async Task<Result> RunAsync(long roundId, UserRights rights, Pseudonyms p, string effort, Action<string, string> trace, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var mails = await db.MailLogs.Where(m => m.AiState == MailAiState.Cookie && !m.Processed).OrderBy(m => m.Created).Take(PerRound + 1).ToListAsync(ct);
        int done = 0, calls = 0;
        decimal cost = 0;
        foreach (var mail in mails.Take(PerRound))
        {
            var rule = mail.ProcessedById is { } ruleId
                ? await db.MailInputs.AsNoTracking().Include(r => r.SensorOutput).ThenInclude(s => s!.Parent).SingleOrDefaultAsync(r => r.Id == ruleId, ct)
                : null;
            if (rule is not { Enabled: true, Action: MailRuleAction.AskAi, SensorOutput: { } sensor })
            {
                mail.AiState = MailAiState.Failed;
                mail.ProcessLog += "\nCookie: the rule is gone, disabled, or no longer asks the AI.";
                await db.SaveChangesAsync(ct);
                continue;
            }
            var o = ai.CurrentValue;
            var subject = InputSanitizer.Clean(mail.Subject, 500).Text;
            var body = InputSanitizer.Clean(mail.Content, o.MaxInputChars).Text;
            var sender = RuleTemplate.SenderOf(mail.From);
            var scan = InjectionScanner.Check($"{subject}\n{body}");
            double? injection = null;
            if (o.Guard.Configured)
            {
                var decided = await guard.DecideAsync(new AiCall(CookieModel.PurposePrefix + "mail.screen"), [p.Mask($"From: {sender}\nSubject: {subject}\n\n{body}")],
                    new Dictionary<string, DecisionQuestion> { ["injection"] = DecisionQuestion.Noul(AiPipeline.InjectionQuestion) }, ct);
                calls++;
                injection = decided.Answers["injection"].Probability;
            }
            var evidence = MailAi.Evidence(mail, body);
            if (scan.Score >= InjectionScanner.BlockScore || injection >= o.Guard.Threshold)
            {
                mail.DecisionId = await inbox.RaiseAsync(new DecisionDraft(MailDecisionKinds.Suspicious, $"Suspicious mail from {sender}",
                    $"For the rule {rule.Name}, the guard or the scan stopped it. Cookie didn't read it.",
                    new MailDecisionPayload(null, evidence, new AiEvidence(injection, injection >= o.Guard.Threshold ? "guard" : "scan", scan.Flags, null, [], []), rule.Id),
                    MailAi.SuspiciousKey(mail.From), mail.Id, "mail", mail.Id), ct);
                mail.AiState = MailAiState.Blocked;
                await db.SaveChangesAsync(ct);
                trace("Mail withheld", $"{sender}: {subject} (the guard or the scan)");
                continue;
            }

            var values = await brain.ValuesAsync(ct);
            values["rule_name"] = rule.Name;
            values["rule_purpose"] = $"{sensor.Parent.Name} / {sensor.Name}";
            var messages = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = $"{await brain.BaseAsync(values, ct)}\n\n{CookiePrompts.Fill(await brain.CaseInstructionAsync("aiEveryTime", ct), values)}" },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = $"<untrusted source=\"mail\">{p.Mask($"From: {sender}\nSubject: {subject}\n\n{body}")}</untrusted>",
                });
            var turn = await model.CompleteAsync("mail", messages, null, (nameof(MailAnswer), AiJson.SchemaFor<MailAnswer>()), effort, null, 600, ct);
            calls++;
            cost += turn.Cost;
            var answer = JsonSerializer.Deserialize<MailAnswer>(turn.Content ?? "", AiJson.Options)
                ?? throw new AiRequestException(AiOutcome.InvalidOutput, "The answer is empty.");
            var message = CaseBook.Cut(p.Unmask(answer.Message.Trim()), 200)!;
            var cookieEvidence = new AiEvidence(injection, null, scan.Flags, null, [], [turn.RequestId]);
            if (answer.Outcome == MailVerdict.Unknown)
            {
                await UndecidedAsync(db, mail, rule, evidence, cookieEvidence, $"Cookie can't tell: {message}", ct);
                trace("Mail: unknown", $"{rule.Name}: {message}");
                continue;
            }
            var ok = answer.Outcome == MailVerdict.Ok;
            var code = ok ? rule.OkCode ?? 200 : rule.FailCode ?? 500;
            var proposal = new CookieProposal(
                new ProposedAction(CookieActionKind.MailEvent, null, 0, sensor.Id.ToString(), subject, answer.Message,
                    $"The mail reports {(ok ? "success" : "a failure")}.", [mail.Id.ToString()], answer.Confidence, Urgency.Info),
                new MailEventPayload(mail.Id, rule.Id, sensor.Id, code, message));
            var log = await executor.HandleAsync(new ActionRequest(proposal, null, roundId, rights, p, ["Mail"]), ct);
            trace($"Mail: {(ok ? "ok" : "fail")}, {log.Outcome}", $"{rule.Name}: {message}{(log.Detail is null ? "" : $"\n{log.Detail}")}");
            await db.Entry(mail).ReloadAsync(ct);
            if (log.Outcome == "done")
            {
                done++;
            }
            else if (log.Outcome == "proposed")
            {
                mail.AiState = MailAiState.Analysed;
                mail.DecisionId = log.InboxDecisionId;
                mail.ProcessLog += $"\nCookie: {(ok ? "ok" : "fail")}, waits for a person: {log.Detail}";
                await db.SaveChangesAsync(ct);
            }
            else
            {
                await UndecidedAsync(db, mail, rule, evidence, cookieEvidence, $"Cookie said {(ok ? "ok" : "fail")} ({message}); {log.Outcome}: {log.Detail}", ct);
            }
        }
        return new Result(done, calls, cost, mails.Count > PerRound);
    }

    /// <summary>A person decides, as package one's <i>The AI could not decide</i> (§12).</summary>
    private async Task UndecidedAsync(PnmtdDbContext db, MailLogEntity mail, MailInputRuleEntity rule, MailEvidence evidence, AiEvidence cookie, string why,
        CancellationToken ct)
    {
        var subject = mail.Subject.Length <= 80 ? mail.Subject : mail.Subject[..79] + "…";
        mail.DecisionId = await inbox.RaiseAsync(new DecisionDraft(MailDecisionKinds.Outcome, $"The AI could not decide: {subject}", $"For the rule {rule.Name}: {why}",
            new MailDecisionPayload(null, evidence, cookie, rule.Id), $"{MailDecisionKinds.Outcome}:{mail.Id}", mail.Id, "mail", mail.Id), ct);
        mail.AiState = MailAiState.Analysed;
        mail.ProcessLog += $"\nCookie: {why}";
        await db.SaveChangesAsync(ct);
    }
}
