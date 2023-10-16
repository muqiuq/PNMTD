using PNMTD.Data;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Tasks.Helpers;
using System;
using System.Net.NetworkInformation;
using System.Threading;

namespace PNMTD.Tasks
{
    public class PingCheckTask : IHostedService, IDisposable
    {
        private readonly ILogger<PingCheckTask> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;
        private int executionCount;

        public const int NUMBER_OF_PINGS = 4;

        public PingCheckTask(ILogger<PingCheckTask> _logger,
            IServiceProvider services,
            IConfiguration configuration
            )
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
            logger.LogInformation("Starting PingWorker");

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(10));

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("PingWorker is stopping.");

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
                logger.LogError("PingWorker DoWork Exception", ex);
            }
        }

        private int PingHost(string host, int timeout)
        {
            int successCounter = 0;

            for(int a = 0; a < NUMBER_OF_PINGS; a++)
            {
                Ping pingSender = new Ping();
                PingOptions options = new PingOptions();
                try
                {
                    PingReply reply = pingSender.Send(host, timeout);
                    if (reply.Status == IPStatus.Success) successCounter++;
                }
                catch (Exception ex) when (ex is PingException || ex is ArgumentOutOfRangeException || ex is ArgumentNullException)
                {
                    logger.LogWarning("PingWorker PingException", ex);
                }
            }

            return successCounter;
        }

        Dictionary<Guid, DateTime> SensorIdLastCheck = new Dictionary<Guid, DateTime>();

        private TaskRunDecisionMaker decisionMaker = new TaskRunDecisionMaker();

        private void doWork(object? state)
        {
            using(var dbContext = new PnmtdDbContext())
            {
                if (!decisionMaker.DetermineIfTaskShouldRun(dbContext)) return;

                var relevantSensors = dbContext.Sensors
                .Where(s => s.Type == SensorType.PING && s.Enabled && s.Parameters != null)
                .ToList();

                int counterCreatedEvents = 0;
                int successfullPings = 0;
                int failedPings = 0;

                foreach (var sensor in relevantSensors)
                {
                    var lastEventSuccess = false;
                    var lastCheck = DateTime.Today.AddDays(-365);
                    var firstTime = false;

                    var lastEvent = dbContext.Events.Where(e => e.SensorId == sensor.Id).OrderByDescending(e => e.Created).FirstOrDefault();
                    if (lastEvent != null)
                    {
                        lastEventSuccess = lastEvent.Code == PNMTStatusCodes.PING_SUCCESSFULL;
                        lastCheck = lastEvent.Created;
                    }
                    else
                    {
                        firstTime = true;
                    }

                    if (DateTime.Now - lastCheck > TimeSpan.FromSeconds(sensor.Interval) &&
                        (!SensorIdLastCheck.ContainsKey(sensor.Id) || DateTime.Now - SensorIdLastCheck[sensor.Id] > TimeSpan.FromSeconds(sensor.Interval)))
                    {

                        bool pingSuccessfull = PingHost(sensor.Parameters, 120) == NUMBER_OF_PINGS;

                        if (pingSuccessfull) successfullPings++;
                        else failedPings++;

                        SensorIdLastCheck[sensor.Id] = DateTime.Now;

                        logger.LogDebug($"Ping {sensor.Parameters} was {pingSuccessfull} ({sensor.Id})");

                        if ((pingSuccessfull && !lastEventSuccess) ||
                                (!pingSuccessfull && lastEventSuccess) ||
                                firstTime)
                        {
                            var newEvent = new EventEntity()
                            {
                                Created = DateTime.Now,
                                Code = pingSuccessfull ? PNMTStatusCodes.PING_SUCCESSFULL : PNMTStatusCodes.PING_FAILED,
                                Id = Guid.NewGuid(),
                                Message = "Ping " + (pingSuccessfull ? "successfull" : "failed"),
                                Sensor = sensor,
                                SensorId = sensor.Id,
                                Source = "PingCheckTask"
                            };
                            dbContext.Events.Add(newEvent);
                            counterCreatedEvents++;
                        }
                    }
                }

                dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_PING_TASK_RUN);
                dbContext.SetKeyValueEntryByEnum(Models.Enums.KeyValueKeyEnums.NUM_OF_SUCCESSFULL_PINGS, successfullPings);
                dbContext.SetKeyValueEntryByEnum(Models.Enums.KeyValueKeyEnums.NUM_OF_FAILED_PINGS, failedPings);

                if (counterCreatedEvents > 0)
                {
                    dbContext.SaveChanges();
                    logger.LogInformation($"PingWorker created {counterCreatedEvents} PINGFAIL events");
                }
            }
        }
    }
}
