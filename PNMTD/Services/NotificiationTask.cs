using Castle.Core.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PNMTD.Data;
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

namespace PNMTD.Services
{
    public class NotificiationService : IHostedService, IDisposable
    {
        private readonly ILogger<NotificiationService> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;
        private int executionCount;

        public NotificiationService(ILogger<NotificiationService> _logger, IServiceProvider services, IConfiguration configuration)
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
            logger.LogInformation("Starting NotificationTask");

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(5));

            return Task.CompletedTask;
        }


        private void tryDoWork(object? state)
        {
            try
            {
                doWork(state);
            }
            catch (Exception ex)
            {
                logger.LogError("NotificiationService DoWork Exception", ex);
            }
        }

        private void doWork(object? state)
        {
            using (var dbContext = new PnmtdDbContext())
            {
                var count = Interlocked.Increment(ref executionCount);

                IList<PendingNotification> allPendingNotifications;

                allPendingNotifications = dbContext.GetAllPendingNotificationsForLastMinutes();

                if (allPendingNotifications.Count == 0) return;

                logger.LogInformation(
                    $"Found {allPendingNotifications.Count} pending notifications");

                if (count % 5 == 0)
                {
                    var result = dbContext.CleanNotificationRuleEventEntities();
                    logger.LogDebug($"Cleanup. Removed: {result}");
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
                        pnm.NoAction = false;
                        NotificationService.SendNotification(
                        pnm.NotitificationRule.Recipient,
                        "Alert",
                        $"{pnm.EventEntity.Sensor.Name} is now State {pnm.EventEntity.Code}\n\n--- DATA ---\n{JsonSerializer.Serialize(eventEntityPoco)}",
                        configuration["Development:DoNotSendNotifications"] == null ? false : bool.Parse(configuration["Development:DoNotSendNotifications"])
                        );
                    }
                    else
                    {
                        pnm.NoAction = true;
                    }

                    dbContext.CreateNotificationRuleEventEntitiesOfPendingNotifications(pnm);
                    dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_NOTIFICATION_TASK_RUN);
                    dbContext.SaveChanges();
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("NotificiationTask is stopping.");

            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }
    }
}
