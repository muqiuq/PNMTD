using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Decisions;
using PNMTD.Lib.Helper;
using PNMTD.Mails;
using PNMTD.Models.Db;
using PNMTD.Tasks.Helpers;

namespace PNMTD.Tasks
{
    /// <summary>
    /// Matches unprocessed mails against the enabled rules every 60 s (docs/ai-operations-plan.md §6). Event rules write
    /// their events and AI rules queue the AI, all that match, as before; only when none of them matched do Ignore and
    /// Delete rules apply. Every expression runs with a 250 ms timeout. With AI on, a mail no rule handled for
    /// <c>Ai:Mail:WaitMinutes</c> is analysed, unless one like it already waits for a decision; and a rule set to ask when
    /// unsure has the AI propose better tests.
    /// </summary>
    public class MailProcessTask(ILogger<MailProcessTask> logger, IServiceProvider services, IOptionsMonitor<AiOptions> ai, AiWorkQueue queue,
        DecisionBook decisions) : IHostedService, IDisposable
    {
        private Timer? timer;
        private readonly RunGuard runGuard = new();

        public void Dispose() => timer?.Dispose();

        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Starting MailProcessTask");
            timer = new Timer(_ => TryRun(), null, TimeSpan.Zero, TimeSpan.FromSeconds(60));
            return Task.CompletedTask;
        }

        private void TryRun()
        {
            if (!runGuard.TryEnter()) return;
            try
            {
                RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MailProcessTask");
            }
            finally
            {
                runGuard.Exit();
            }
        }

        /// <summary>One round. Public for the tests.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            await using var db = services.CreateDbContext();
            var mails = await db.MailLogs.Where(ml => !ml.Processed).ToListAsync(ct);
            var rules = await db.MailInputs.Where(r => r.Enabled).ToListAsync(ct);
            foreach (var mail in mails)
            {
                await ProcessAsync(mail, rules, db, ct);
            }
            db.MailLogs.CleanUpMailLog();
            await db.SaveChangesAsync(ct);
            db.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_MAIL_PROCESSING);
        }

        private bool Matches(MailLogEntity mail, MailInputRuleEntity rule) =>
            Match(mail, mail.From, rule.FromTest ?? "", rule)
            && (string.IsNullOrEmpty(rule.SubjectTest) || Match(mail, mail.Subject, rule.SubjectTest, rule))
            && (string.IsNullOrEmpty(rule.BodyTest) || Match(mail, mail.Content, rule.BodyTest, rule));

        /// <summary>A match with the timeout; a timeout counts as no match and goes to the process log.</summary>
        private static bool Match(MailLogEntity mail, string input, string pattern, MailInputRuleEntity rule)
        {
            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(input, pattern, System.Text.RegularExpressions.RegexOptions.None, RuleTemplate.Timeout);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                mail.ProcessLog += $"\n{rule.Name}({rule.Id}): an expression took longer than 250 ms; counted as no match";
                return false;
            }
        }

        private async Task ProcessAsync(MailLogEntity mail, List<MailInputRuleEntity> rules, PnmtdDbContext db, CancellationToken ct)
        {
            var o = ai.CurrentValue;
            var aiMail = o.Usable && o.Mail.Enabled;
            var handled = false;
            foreach (var rule in rules.Where(r => r.Action is MailRuleAction.Event or MailRuleAction.AskAi))
            {
                if (!Matches(mail, rule))
                {
                    continue;
                }
                handled = true;
                mail.ProcessLog += $"\nmatched {rule.Name}({rule.Id})";
                mail.ProcessedBy = rule;
                mail.ProcessedById = rule.Id;
                if (rule.Action == MailRuleAction.AskAi)
                {
                    // The AI decides; the mail stays unprocessed until it has (or a person has).
                    if (aiMail && mail.AiState == MailAiState.None)
                    {
                        mail.AiState = MailAiState.Queued;
                        await db.SaveChangesAsync(ct);
                        await queue.EnqueueAsync("mail.evaluate", "mail", mail.Id, null, ct);
                    }
                    continue;
                }
                mail.Processed = true;
                mail.Disposition = MailDisposition.Event;
                if (rule.SensorOutputId.HasValue)
                {
                    var message = "";
                    var code = rule.DefaultCode;
                    var tested = false;
                    if (!string.IsNullOrEmpty(rule.OkTest) && rule.OkCode.HasValue)
                    {
                        tested = true;
                        if (Match(mail, mail.Content, rule.OkTest, rule)) code = rule.OkCode.Value;
                    }
                    if (!string.IsNullOrEmpty(rule.FailTest) && rule.FailCode.HasValue)
                    {
                        tested = true;
                        if (Match(mail, mail.Content, rule.FailTest, rule)) code = rule.FailCode.Value;
                    }
                    if (!string.IsNullOrEmpty(rule.ExtractMessageRegex))
                    {
                        try
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(mail.Content, rule.ExtractMessageRegex,
                                System.Text.RegularExpressions.RegexOptions.None, RuleTemplate.Timeout);
                            if (match.Success && match.Groups["msg"].Success) message = ": " + match.Groups["msg"].Value;
                        }
                        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                        {
                            mail.ProcessLog += $"\n{rule.Name}({rule.Id}): the message expression took longer than 250 ms";
                        }
                    }
                    db.Events.Add(new EventEntity
                    {
                        Id = Guid.NewGuid(), Code = code, Created = Clock.UtcNow, Message = $"MailInput {rule.Name}({rule.Id}){message}".Trim(),
                        SensorId = rule.SensorOutputId.Value, Source = mail.From,
                    });
                    // Trigger B (§6.2): neither test matched, and the rule asks the AI when unsure.
                    if (tested && code == rule.DefaultCode && rule.AskAiWhenUnsure && aiMail)
                    {
                        await db.SaveChangesAsync(ct);
                        await queue.EnqueueAsync("mail.improve", "mail", mail.Id, null, ct);
                    }
                }
                await db.SaveChangesAsync(ct);
            }
            if (handled)
            {
                return;
            }
            var quiet = rules.FirstOrDefault(r => r.Action is MailRuleAction.Ignore or MailRuleAction.Delete && Matches(mail, r));
            if (quiet is not null)
            {
                mail.Processed = true;
                mail.ProcessedBy = quiet;
                mail.ProcessedById = quiet.Id;
                mail.Disposition = quiet.Action == MailRuleAction.Delete ? MailDisposition.Deleted : MailDisposition.Ignored;
                mail.ProcessLog += $"\n{(quiet.Action == MailRuleAction.Delete ? "deleted" : "ignored")} by {quiet.Name}({quiet.Id})";
                await db.SaveChangesAsync(ct);
                return;
            }
            // Trigger A (§6.2): no rule for WaitMinutes, within the last 7 days; one like it waiting for a decision takes it.
            if (aiMail && mail.AiState == MailAiState.None && mail.Created <= Clock.UtcNow.AddMinutes(-Math.Max(0, o.Mail.WaitMinutes))
                && mail.Created >= Clock.UtcNow.AddDays(-7))
            {
                await AnalyseAsync(mail, db, ct);
            }
        }

        private async Task AnalyseAsync(MailLogEntity mail, PnmtdDbContext db, CancellationToken ct)
        {
            if (await decisions.AttachAsync(MailAi.RuleKey(mail.From, mail.Subject), mail.Id, ct) is { } decision)
            {
                mail.AiState = MailAiState.Analysed;
                mail.DecisionId = decision;
                mail.ProcessLog += "\nlike a mail that waits for a decision; attached to it";
                await db.SaveChangesAsync(ct);
                return;
            }
            mail.AiState = MailAiState.Queued;
            await db.SaveChangesAsync(ct);
            await queue.EnqueueAsync("mail.analyse", "mail", mail.Id, null, ct);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("MailProcessTask is stopping.");
            timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }
    }
}
