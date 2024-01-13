using PNMTD.Helper;
using PNMTD.Tasks;
using System.Diagnostics;

namespace PNMTD.Notifications
{
    public class NotificationService
    {
        private readonly ILogger _logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;

        public NotificationService(ILogger<NotificationTask> _logger, IServiceProvider services, IConfiguration configuration)
        {
            this._logger = _logger;
            this.services = services;
            this.configuration = configuration;
        }

        public void SendNotification(string recipient, string subject, string messageShort, string messageLong, bool onlySimulate = false)
        {
            var type = typeof(INotificationProvider);

            var types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .Where(p => type.IsAssignableFrom(p));

            var notificationTypes = types.Where(t => t.IsClass && t.Namespace == "PNMTD.Notifications" && t.GetInterfaces().Contains(typeof(INotificationProvider))).ToList();

            foreach(var notificationType in notificationTypes)
            {
                var notificationProvider = (INotificationProvider) Activator.CreateInstance(notificationType);

                notificationProvider.Configure(configuration);

                if(notificationProvider.IsMatch(recipient))
                {
                    _logger.LogInformation($"Sending notification ({notificationProvider.GetType().Name}) to {recipient} subject '{subject}' content '{messageShort}' ");
                    if (!onlySimulate)
                    {
                        notificationProvider.SendNotification(recipient, subject, messageShort, messageLong);
                    }
                }
            }
        }
    }
}
