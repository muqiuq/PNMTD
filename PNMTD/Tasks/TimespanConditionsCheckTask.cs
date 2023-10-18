using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using System.Text.RegularExpressions;
using PNMTD.Lib.Logic.IntervalDescriptions;
using Microsoft.EntityFrameworkCore;

namespace PNMTD.Tasks
{
    public class TimespanConditionsCheckTask : IHostedService, IDisposable
    {
        private readonly ILogger<TimespanConditionsCheckTask> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;


        public TimespanConditionsCheckTask(ILogger<TimespanConditionsCheckTask> _logger, IServiceProvider services, IConfiguration configuration)
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
            logger.LogInformation("Starting TimespanConditionsCheckTask");

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(15));

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
                logger.LogError("TimespanConditionsCheckTask", ex);
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
            using(var dbContext = new PnmtdDbContext())
            {
                var relevantSensors = dbContext.Sensors.Where(s =>
                (s.Type == SensorType.ONE_WITHIN_TIMESPAN)
                && s.Enabled).ToList();

                int counterCreatedEvents = 0;

                foreach (var sensor in relevantSensors)
                {
                    if (sensor.Type == SensorType.ONE_WITHIN_TIMESPAN)
                    {
                        ProcessSensor(logger, dbContext, sensor, PNMTStatusCodes.ONE_WITHIN_TIMESPAN_OK, PNMTStatusCodes.ONE_WITHIN_TIMESPAN_FAILED);
                    }
                    else if (sensor.Type == SensorType.ALL_WITHIN_TIMESPAN)
                    {
                        ProcessSensor(logger, dbContext, sensor, PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK, PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED);
                    }
                }

                dbContext.SaveChanges();
                dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_TIMESPAN_CONDITIONS_CHECK_TASK_RUN);

                if (counterCreatedEvents > 0)
                {
                    logger.LogInformation($"TimespanConditionsCheckTask created {counterCreatedEvents} HEARTBEAT_MISSING events");
                }
            }
        }

        public static void ProcessSensor(ILogger<TimespanConditionsCheckTask> logger, PnmtdDbContext dbContext, SensorEntity sensor,
            int withinSuccessCode, int withinFailedCode, DateTime now = new DateTime())
        {
            // Testing purposes
            if(now == new DateTime())
            {
                now = DateTime.Now;
            }

            if (!IntervalDescriptionHelper.TryParse(sensor.Parameters, out var intervalDescription))
            {
                sensor.Status = "invalid interval description";
                logger?.LogError($"Invalid interval description {sensor.Parameters} for {sensor.Name} ({sensor.Id})");
                return;
            }

            if (sensor.Status != "") sensor.Status = "";

            if (!intervalDescription.IsNow(now))
            {
                return;
            }

            var relEvent = dbContext.Events.Where(e => e.SensorId == sensor.Id &&
            (e.Code == withinSuccessCode ||
            e.Code == withinFailedCode)).OrderByDescending(e => e.Created).FirstOrDefault();

            if (relEvent != null && !intervalDescription.TimespanDifBiggerThenInterval(now, relEvent.Created))
            {
                return;
            }

            var analyzedEventsQuery = dbContext.Events.Where(e => e.SensorId == sensor.Id);
            if(relEvent != null)
            {
                analyzedEventsQuery = analyzedEventsQuery.Where(e => e.Created > relEvent.Created);
            }
            var analyzedEvents = analyzedEventsQuery.OrderByDescending(e => e.Created).ToList();

            int numOfSuccessEvents = analyzedEvents.Where(e => e.IsSuccess).Count();
            int numOfEvents = analyzedEvents.Count();

            bool success = false;

            if(sensor.Type == SensorType.ONE_WITHIN_TIMESPAN && numOfSuccessEvents != 0)
            {
                success = true;
            }
            if (sensor.Type == SensorType.ALL_WITHIN_TIMESPAN && numOfEvents == numOfSuccessEvents && numOfEvents > 0)
            {
                success = true;
            }

            var newEvents = new EventEntity()
            {
                Id = Guid.NewGuid(),
                Code = success ? withinSuccessCode : withinFailedCode,
                Created = now,
                Message = "",
                SensorId = sensor.Id,
                Source = "TimespanConditionCheckTask"
            };
            dbContext.Events.Add(newEvents);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("TimespanConditionsCheckTask is stopping.");

            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }
    }
}
