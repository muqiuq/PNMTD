using PNMTD.Tasks.Helpers;
using PNMTD.Lib.Helper;
using MailKit.Net.Pop3;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Mails;
using PNMTD.Models.Db;
using System.Text.RegularExpressions;

namespace PNMTD.Tasks
{
    public class MailProcessTask : IHostedService, IDisposable
    {
        private readonly ILogger<MailProcessTask> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;
        private int executionCount;
        private string? username;
        private string? host;
        private string? password;

        public MailProcessTask(ILogger<MailProcessTask> _logger, IServiceProvider services, IConfiguration configuration)
        {
            logger = _logger;
            this.services = services;
            this.configuration = configuration;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Starting MailProcessTask");

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(60));

            return Task.CompletedTask;
        }


        private readonly RunGuard runGuard = new();

        private void tryDoWork(object? state)
        {
            if (!runGuard.TryEnter()) return;
            try
            {
                doWork(state);
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

        private static bool IsMatch(string content, string parameters)
        {
            Regex rx = new Regex(parameters, RegexOptions.Compiled);

            MatchCollection matches = rx.Matches(content);

            return matches.Count > 0;
        }

        private void doWork(object? state)
        {
            using (var dbContext = services.CreateDbContext())
            {
                var count = Interlocked.Increment(ref executionCount);

                var maillogs = dbContext.MailLogs.Where(ml => ml.Processed == false).ToList();

                var mailinputrules = dbContext.MailInputs.Where(ml => ml.Enabled).ToList();

                foreach (var maillog in maillogs)
                {
                    ProcessSingleEntry(maillog, mailinputrules, dbContext);
                }

                dbContext.MailLogs.CleanUpMailLog();
                dbContext.SaveChanges();

                dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_MAIL_PROCESSING);
            }
        }

        private void ProcessSingleEntry(MailLogEntity maillog, List<MailInputRuleEntity> mailinputrules, PnmtdDbContext dbContext)
        {
            foreach (var rule in mailinputrules)
            {
                if (IsMatch(maillog.From, rule.FromTest) &&
                    (string.IsNullOrEmpty(rule.SubjectTest) || IsMatch(maillog.Subject, rule.SubjectTest)) &&
                    (string.IsNullOrEmpty(rule.BodyTest) || IsMatch(maillog.Content, rule.BodyTest)))
                {
                    maillog.ProcessLog += $"\nmatched {rule.Name}({rule.Id})";
                    maillog.ProcessedBy = rule;
                    maillog.ProcessedById = rule.Id;
                    maillog.Processed = true;
                    if (rule.SensorOutputId.HasValue)
                    {
                        var message = "";
                        int code = rule.DefaultCode;
                        if (!string.IsNullOrEmpty(rule.OkTest) && rule.OkCode.HasValue && IsMatch(maillog.Content, rule.OkTest))
                        {
                            code = rule.OkCode.Value;
                        }
                        if (!string.IsNullOrEmpty(rule.FailTest) && rule.FailCode.HasValue && IsMatch(maillog.Content, rule.FailTest))
                        {
                            code = rule.FailCode.Value;
                        }
                        if (!string.IsNullOrEmpty(rule.ExtractMessageRegex))
                        {
                            Regex pattern = new Regex(rule.ExtractMessageRegex);

                            Match match = pattern.Match(maillog.Content);

                            if (match.Success && match.Groups["msg"].Success)
                            {
                                message = ": " + match.Groups["msg"].Value;
                            }
                        }
                        var eventEntity = new EventEntity()
                        {
                            Id = Guid.NewGuid(),
                            Code = code,
                            Created = Clock.UtcNow,
                            Message = $"MailInput {rule.Name}({rule.Id}){message}".Trim(),
                            SensorId = rule.SensorOutputId.Value,
                            Source = maillog.From
                        };
                        dbContext.Events.Add(eventEntity);
                    }

                    dbContext.SaveChanges();
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("MailProcessTask is stopping.");

            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }
    }
}
