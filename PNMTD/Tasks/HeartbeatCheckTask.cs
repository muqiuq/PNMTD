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


        private void tryDoWork(object? state)
        {
            try
            {
                doWork(state);
            }
            catch (Exception ex)
            {
                logger.LogError("HeartbeatCheckTask DoWork Exception", ex);
            }
        }

        private TaskRunDecisionMaker decisionMaker = new TaskRunDecisionMaker();

        private void doWork(object? state)
        {
            using (var dbContext = new PnmtdDbContext())
            {
                if (!decisionMaker.DetermineIfTaskShouldRun(dbContext)) return;

                var relevantSensors = dbContext.Sensors.Where(s =>
                (s.Type == SensorType.HEARTBEAT || s.Type == SensorType.HEARTBEAT_VALUECHECK)
                && s.Enabled).ToList();

                int counterCreatedEvents = 0;

                foreach (var sensor in relevantSensors)
                {
                    var lastCheck = DateTime.Today.AddDays(-365);
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

                    if (DateTime.Now - lastCheck > TimeSpan.FromSeconds(sensor.Interval) + TimeSpan.FromSeconds(sensor.GracePeriod))
                    {
                        var newEvent = new EventEntity()
                        {
                            Created = DateTime.Now,
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
