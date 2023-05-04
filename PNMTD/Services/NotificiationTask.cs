using Castle.Core.Logging;
using Microsoft.Extensions.Logging;
using PNMTD.Data;
using PNMTD.Models.Helper;
using PNMTD.Notifications;

namespace PNMTD.Services
{
    public class NotificiationService : IHostedService, IDisposable
    {
        private readonly ILogger<NotificiationService> logger;
        private readonly IServiceProvider services;
        private Timer _timer;
        private int executionCount;

        public NotificiationService(ILogger<NotificiationService> _logger, IServiceProvider services)
        {
            logger = _logger;
            this.services = services;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Starting NotificationTask");

            _timer = new Timer(DoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(5));

            return Task.CompletedTask;
        }


        private void DoWork(object? state)
        {
            var count = Interlocked.Increment(ref executionCount);

            IList<PendingNotification> allPendingNotifications;

            using (var scope = services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();

                allPendingNotifications = dbContext.GetAllPendingNotifications();
            }

            logger.LogInformation(
                $"Found {allPendingNotifications.Count} pending notifications");

            foreach (var pnm in allPendingNotifications)
            {
                NotificationService.SendNotification(
                    pnm.NotitificationRule.Recipient,
                    "Alert",
                    $"{pnm.EventEntity.Sensor.Name} is now State {pnm.EventEntity.Code}"
                    );

                using (var scope = services.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();

                    dbContext.CreateNotificationRuleEventEntitiesOfPendingNotifications(pnm);

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
