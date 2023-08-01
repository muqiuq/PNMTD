﻿using Castle.Core.Logging;
using Microsoft.Extensions.Logging;
using PNMTD.Data;
using PNMTD.Models.Helper;
using PNMTD.Models.Poco;
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
            }catch (Exception ex)
            {
                logger.LogError("NotificiationService DoWork Exception", ex);
            }
        }

        private void doWork(object? state)
        {
            var count = Interlocked.Increment(ref executionCount);

            IList<PendingNotification> allPendingNotifications;

            var dbContext = new PnmtdDbContext();

            allPendingNotifications = dbContext.GetAllPendingNotifications();

            if (allPendingNotifications.Count == 0) return;

            logger.LogInformation(
                $"Found {allPendingNotifications.Count} pending notifications");

            foreach (var pnm in allPendingNotifications)
            {
                var eventEntityPoco = new EventEntityPoco(pnm.EventEntity);

                NotificationService.SendNotification(
                    pnm.NotitificationRule.Recipient,
                    "Alert",
                    $"{pnm.EventEntity.Sensor.Name} is now State {pnm.EventEntity.Code}\n\n--- DATA ---\n{JsonSerializer.Serialize(eventEntityPoco)}",
                    configuration["Development:DoNotSendNotifications"] == null ? false : bool.Parse(configuration["Development:DoNotSendNotifications"])
                    ) ;

                dbContext.CreateNotificationRuleEventEntitiesOfPendingNotifications(pnm);

                dbContext.SaveChanges();
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
