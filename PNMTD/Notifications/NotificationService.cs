﻿using PNMTD.Helper;
using System.Diagnostics;

namespace PNMTD.Notifications
{
    public class NotificationService
    {
        private static readonly ILogger _logger = LogManager.CreateLogger<NotificationService>();

        public static void SendNotification(string recipient, string subject, string messageContent)
        {
            var type = typeof(INotificationProvider);

            var types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .Where(p => type.IsAssignableFrom(p));

            var notificationTypes = types.Where(t => t.IsClass && t.Namespace == "PNMTD.Notifications" && t.GetInterfaces().Contains(typeof(INotificationProvider))).ToList();

            foreach(var notificationType in notificationTypes)
            {
                var notificationProvider = (INotificationProvider) Activator.CreateInstance(notificationType);

                if(notificationProvider.IsMatch(recipient))
                {
                    if (Global.IsDevelopment)
                    {
                        _logger.LogInformation($"Sending notification ({notificationProvider.GetType().Name}) to {recipient} subject '{subject}' content '{messageContent}' ");
                        // notificationProvider.SendNotification(recipient, subject, messageContent);
                    }
                    else
                    {
                        notificationProvider.SendNotification(recipient, subject, messageContent);
                    }
                }
            }
        }
    }
}
