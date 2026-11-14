using PNMTD.Lib.Helper;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Tasks.Helpers;

namespace PNMTD.Tasks
{
    public class HeartbeatCheckTask : IHostedService, IDisposable
    {
        private readonly ILogger<HeartbeatCheckTask> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;
        private int executionCount;

        public HeartbeatCheckTask(ILogger<HeartbeatCheckTask> _logger, IServiceProvider services, IConfiguration configuration)
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
            logger.LogInformation("Starting HeartbeatCheckTask");

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(GlobalConfiguration.MINIMUM_TIME_DIFFERENCE_BETWEEN_EVENTS_IN_SECONDS * 2));

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("HeartbeatCheckTask is stopping.");

            _timer?.Change(Timeout.Infinite, 0);

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
                logger.LogError(ex, "HeartbeatCheckTask DoWork Exception");
            }
            finally
            {
                runGuard.Exit();
            }
        }

        private TaskRunDecisionMaker decisionMaker = new TaskRunDecisionMaker();

        private readonly DateTime startedAt = Clock.UtcNow;

        /// <summary>
        /// No 460s for a while after a start (Heartbeat:StartupGraceMinutes, default 5): a sensor
        /// whose report fell into the downtime of an update gets time to report again before it is
        /// called missing (docs/webui-migration-plan.md §6.5).
        /// </summary>
        private TimeSpan StartupGrace => TimeSpan.FromMinutes(configuration.GetValue("Heartbeat:StartupGraceMinutes", 5));

        private void doWork(object? state)
        {
            if (Clock.UtcNow - startedAt < StartupGrace) return;

            using (var dbContext = services.CreateDbContext())
            {
                if (!decisionMaker.DetermineIfTaskShouldRun(dbContext)) return;

                // Servers with an agent too, Windows and Linux (docs/windows-sensor-plan.md §8.1; docs/linux-sensor-plan.md §9.1),
                // except those revoked or uninstalled on purpose.
                var relevantSensors = dbContext.Sensors.Where(s =>
                ((s.Type == SensorType.HEARTBEAT || s.Type == SensorType.HEARTBEAT_VALUECHECK || s.Type == SensorType.UDP_NODE)
                 || ((s.Type == SensorType.WINDOWS || s.Type == SensorType.LINUX)
                     && !dbContext.Agents.Any(a => a.SensorId == s.Id && (a.RevokedAt != null || a.UninstalledAt != null)))
                 // Scheduled diagnostics: their interval is the schedule's (docs/windows-sensor-plan.md §5.4); manual ones have none.
                 || (s.Type == SensorType.DIAGNOSTICS && s.Interval > 0))
                && s.Enabled).ToList();

                int counterCreatedEvents = 0;

                foreach (var sensor in relevantSensors)
                {
                    var lastCheck = Clock.UtcNow.AddDays(-365);
                    var lastCode = -1;
                    var listOfEventsQuery = dbContext.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created);
                    if (listOfEventsQuery.Any())
                    {
                        var lastEvent = listOfEventsQuery.FirstOrDefault();
                        lastCode = lastEvent.Code;
                        lastCheck = lastEvent.Created;
                    }
                    else
                    {
                        // Will only start if any events are available
                        continue;
                    }

                    if (lastCode == PNMTStatusCodes.HEARTBEAT_MISSING) continue;

                    if (Clock.UtcNow - lastCheck > TimeSpan.FromSeconds(sensor.Interval) + TimeSpan.FromSeconds(sensor.GracePeriod))
                    {
                        var newEvent = new EventEntity()
                        {
                            Created = Clock.UtcNow,
                            Code = PNMTStatusCodes.HEARTBEAT_MISSING,
                            Id = Guid.NewGuid(),
                            Message = "Heartbeat missing",
                            Sensor = sensor,
                            SensorId = sensor.Id,
                            Source = "HeartbeatCheckTask"
                        };
                        dbContext.Events.Add(newEvent);
                        counterCreatedEvents++;
                    }
                }

                dbContext.SaveChanges();
                dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_HEARTBEAT_TASK_RUN);

                if (counterCreatedEvents > 0)
                {
                    logger.LogInformation($"HeartbeatCheckTask created {counterCreatedEvents} HEARTBEAT_MISSING events");
                }
            }
        }
    }
}
