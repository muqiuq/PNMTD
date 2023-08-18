using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using System.Text.RegularExpressions;

namespace PNMTD.Services
{
    public class TimespanConditionsCheckTask : IHostedService, IDisposable
    {
        private readonly ILogger<NotificiationService> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;
        private int executionCount;
        private string? username;
        private string? host;
        private string? password;

        public TimespanConditionsCheckTask(ILogger<NotificiationService> _logger, IServiceProvider services, IConfiguration configuration)
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
            TimeSpan.FromSeconds(10));

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
            var dbContext = new PnmtdDbContext();

            var relevantSensors = dbContext.Sensors.Where(s =>
                (s.Type == SensorType.ONE_WITHIN_TIMESPAN)
                && s.Enabled).ToList();

            int counterCreatedEvents = 0;

            foreach (var sensor in relevantSensors)
            {
                
            }

            dbContext.SaveChanges();
            dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_HEARTBEAT_TASK_RUN);

            if (counterCreatedEvents > 0)
            {
                logger.LogInformation($"HeartbeatCheckTask created {counterCreatedEvents} HEARTBEAT_MISSING events");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("TimespanConditionsCheckTask is stopping.");

            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }
    }
}
