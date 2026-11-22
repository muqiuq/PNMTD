using PNMTD.Tasks.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Logic;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Helper;
using PNMTD.Models.Poco;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Notifications;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD.Tasks
{
    /// <summary>
    /// The alerts of the notification rules, every 10 seconds (<see cref="RunOnce"/>); and on a second timer of its own, with a guard of
    /// its own, the notifications to people about their own work (docs/knowledge-base-extensions-plan.md §10.5): the two never wait for
    /// each other, so a slow mail server for a person's e-mail never delays an alert.
    /// </summary>
    public class NotificationTask : IHostedService, IDisposable
    {
        private readonly ILogger<NotificationTask> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;
        private Timer? userTimer;
        private int executionCount;
        private readonly NotificationService notificationService;
        private readonly NotificationRecipients recipients;
        private readonly UserNotifications userNotifications;

        public NotificationTask(ILogger<NotificationTask> _logger,
            IServiceProvider services,
            IConfiguration configuration,
            NotificationService service,
            NotificationRecipients recipients,
            UserNotifications userNotifications)
        {
            logger = _logger;
            this.services = services;
            this.configuration = configuration;
            this.notificationService = service;
            this.recipients = recipients;
            this.userNotifications = userNotifications;
        }

        public void Dispose()
        {
            _timer?.Dispose();
            userTimer?.Dispose();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Starting NotificationTask");

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(10));

            var userInterval = Math.Max(5, configuration.GetValue($"{UserNotificationOptions.Section}:IntervalSeconds", 30));
            userTimer = new Timer(tryUserWork, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(userInterval));

            return Task.CompletedTask;
        }

        private readonly RunGuard userGuard = new();

        private void tryUserWork(object? state)
        {
            if (!userGuard.TryEnter()) return;
            try
            {
                RunUserNotificationsOnceAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NotificationTask: the notifications to people");
            }
            finally
            {
                userGuard.Exit();
            }
        }

        /// <summary>One run of the second timer: the notifications to people that are due. The tests call it directly.</summary>
        internal Task<int> RunUserNotificationsOnceAsync(CancellationToken ct = default) => userNotifications.RunOnceAsync(ct);


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
                logger.LogError(ex, "NotificiationService DoWork Exception");
            }
            finally
            {
                runGuard.Exit();
            }
        }

        private void doWork(object? state) => RunOnce();

        /// <summary>One run: the pending notifications of the last 30 minutes, evaluated, sent and recorded. The tests call it directly.</summary>
        internal void RunOnce()
        {
            using (var dbContext = services.CreateDbContext())
            {
                var count = Interlocked.Increment(ref executionCount);

                IList<PendingNotification> allPendingNotifications;

                allPendingNotifications = dbContext.GetAllPendingNotificationsForLastMinutes();

                // Whom each rule reaches (docs/notification-recipients-plan.md §5): once per rule per run; the responsible doesn't
                // change inside 30 minutes of events.
                var resolvedByRule = new Dictionary<Guid, Resolved>();

                // Every run, so that /api/v1/system/tasks can tell a stalled task from a quiet one.
                dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_NOTIFICATION_TASK_RUN);

                if (allPendingNotifications.Count == 0) return;

                logger.LogInformation(
                    $"Found {allPendingNotifications.Count} pending notifications");

                if (count % 5 == 0)
                {
                    var result = dbContext.CleanNotificationRuleEventEntities();
                    logger.LogDebug($"Cleanup. Removed: {result}");
                    var updateResult = dbContext.UpdateLastCleanupEvents();
                    logger.LogDebug($"Updated NotificationEntities {updateResult}");
                }

                foreach (var pnm in allPendingNotifications)
                {
                    var eventEntityPoco = pnm.EventEntity.ToPoco();

                    pnm.NoAction = false;

                    if ((pnm.EventEntity.Sensor.Type == SensorType.ONE_WITHIN_TIMESPAN
                        && pnm.EventEntity.Code != PNMTStatusCodes.ONE_WITHIN_TIMESPAN_OK
                        && pnm.EventEntity.Code != PNMTStatusCodes.ONE_WITHIN_TIMESPAN_FAILED) ||
                        (pnm.EventEntity.Sensor.Type == SensorType.ALL_WITHIN_TIMESPAN
                        && pnm.EventEntity.Code != PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK
                        && pnm.EventEntity.Code != PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED))
                    {
                        pnm.NoAction = true;
                        logger.LogDebug($"Ignoring event {pnm.EventEntity.Id} with code {pnm.EventEntity.Code} because of invalid within timespan code");
                    }

                    EventEntity? lastEventForSensor;

                    if (pnm.EventEntity.Sensor.Type == SensorType.ONE_WITHIN_TIMESPAN
                        || pnm.EventEntity.Sensor.Type == SensorType.ALL_WITHIN_TIMESPAN)
                    {
                        lastEventForSensor = dbContext.Events
                        .Where(e => e.SensorId == eventEntityPoco.SensorId
                            && e.Id != pnm.EventEntity.Id
                            && (e.Code == PNMTStatusCodes.ONE_WITHIN_TIMESPAN_OK
                                || e.Code == PNMTStatusCodes.ONE_WITHIN_TIMESPAN_FAILED
                                || e.Code == PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK
                                || e.Code == PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED
                            ))
                        .OrderByDescending(e => e.Created)
                        .FirstOrDefault();
                    }
                    else
                    {
                        lastEventForSensor = dbContext.Events
                        .Where(e => e.SensorId == eventEntityPoco.SensorId && e.Id != pnm.EventEntity.Id)
                        .OrderByDescending(e => e.Created)
                        .FirstOrDefault();
                    }

                    int oldStatusCode = -1;

                    if (lastEventForSensor != null)
                    {
                        oldStatusCode = lastEventForSensor.Code;
                    }

                    if (pnm.NoAction == false && NotificationRuleTriggerLogic.Eval(pnm.NotitificationRule.Type, oldStatusCode, pnm.EventEntity.Code)
                        && pnm.NotitificationRule.Enabled &&
                        pnm.EventEntity.Sensor.Enabled &&
                        !pnm.EventEntity.Sensor.Ignore &&
                        pnm.EventEntity.Sensor.Parent.Enabled)
                    {
                        var rule = pnm.NotitificationRule;
                        if (!resolvedByRule.TryGetValue(rule.Id, out var resolved))
                        {
                            resolved = recipients.ResolveAsync(dbContext, rule, Clock.UtcNow, CancellationToken.None).GetAwaiter().GetResult();
                            resolvedByRule[rule.Id] = resolved;
                            if (resolved.Deliveries.Count == 0)
                            {
                                logger.LogWarning("Rule {Rule} reached nobody: {Why}", rule.Name, resolved.Why);
                            }
                        }
                        if (resolved.Deliveries.Count == 0)
                        {
                            // Fired, but nobody to tell: recorded as no action; the page and the coverage say why (§5).
                            pnm.NoAction = true;
                        }
                        else
                        {
                            var emailSubject = pnm.EventEntity.IsSuccess ? "Resolved" : "Alert";
                            pnm.NoAction = false;
                            var messageShort = $"Host {pnm.EventEntity.Sensor.Parent.Name}, Sensor {pnm.EventEntity.Sensor.Name} is now State {pnm.EventEntity.Code}";
                            var messageLong = $"{messageShort}\n\n--- DATA ---\n{JsonSerializer.Serialize(eventEntityPoco)}";
                            if (resolved.Because is { } because)
                            {
                                // Why this person got it (R4): "— Rule Storage on duty: to you as the responsible today".
                                messageLong += $"\n\n— Rule {rule.Name}: {because}";
                            }
                            var simulate = configuration.GetValue("Development:DoNotSendNotifications", false);
                            var sent = false;
                            foreach (var delivery in resolved.Deliveries)
                            {
                                var message = new NotificationMessage(delivery.Recipient, $"{emailSubject} ({pnm.EventEntity.Sensor.Name})", messageShort,
                                    messageLong, pnm.Importance);
                                var result = notificationService.SendAsync(message,
                                    $"rule {rule.Name} → {(delivery.Person.Length > 0 ? delivery.Person : delivery.Channel)}", simulate, CancellationToken.None)
                                    .GetAwaiter().GetResult();
                                sent |= result is { Ok: true };
                            }
                            if (sent)
                            {
                                rule.LastSentAt = Clock.UtcNow;
                                rule.LastSentTo = resolved.Who.Length <= 500 ? resolved.Who : resolved.Who[..499] + "…";
                            }
                        }
                    }
                    else
                    {
                        pnm.NoAction = true;
                    }

                    dbContext.CreateNotificationRuleEventEntitiesOfPendingNotifications(pnm);
                    dbContext.SaveChanges();
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("NotificiationTask is stopping.");

            _timer?.Change(Timeout.Infinite, 0);
            userTimer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }
    }
}
