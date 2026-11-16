namespace PNMTD.Notifications
{
    /// <summary>
    /// Picks the provider by the recipient's form and sends (docs/ai-operations-plan.md §8.3). It logs the
    /// provider and the result, never the recipient: a Pushover key or a webhook URL can carry secrets.
    /// </summary>
    public sealed class NotificationService(IEnumerable<INotificationProvider> providers, ILogger<NotificationService> logger)
    {
        public const string HttpClientName = "notifications";

        public INotificationProvider? ProviderFor(string recipient) => providers.FirstOrDefault(p => p.IsMatch(recipient));

        /// <summary>Null when no provider takes the recipient. <paramref name="simulate"/>: logged, not sent (Development).</summary>
        public async Task<DeliveryResult?> SendAsync(NotificationMessage message, string context, bool simulate, CancellationToken ct)
        {
            if (ProviderFor(message.Recipient) is not { } provider)
            {
                logger.LogWarning("No notification provider for the recipient of {Context}", context);
                return null;
            }
            if (simulate)
            {
                logger.LogInformation("Simulated {Provider} notification for {Context}: {Subject}", provider.Name, context, message.Subject);
                return new DeliveryResult(true, "Simulated (Development:DoNotSendNotifications).", TimeSpan.Zero);
            }
            var result = await provider.SendAsync(message, ct);
            if (result.Ok)
            {
                logger.LogInformation("{Provider} notification for {Context}: {Detail}", provider.Name, context, result.Detail);
            }
            else
            {
                logger.LogWarning("{Provider} notification for {Context} failed: {Detail}", provider.Name, context, result.Detail);
            }
            return result;
        }
    }

    public static class NotificationSetup
    {
        public static IServiceCollection AddNotifications(this IServiceCollection services)
        {
            services.AddHttpClient(NotificationService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
            services.AddSingleton<PushoverPriorities>();
            services.AddSingleton<PushoverNotification>();
            services.AddSingleton<EMailNotification>();
            services.AddSingleton<HttpNotification>();
            services.AddSingleton<INotificationProvider>(sp => sp.GetRequiredService<PushoverNotification>());
            services.AddSingleton<INotificationProvider>(sp => sp.GetRequiredService<HttpNotification>());
            services.AddSingleton<INotificationProvider>(sp => sp.GetRequiredService<EMailNotification>());
            services.AddSingleton<NotificationService>();
            // Whom a rule reaches (docs/notification-recipients-plan.md §4): reads the availability plan, so it is a singleton like it.
            services.AddSingleton<NotificationRecipients>();
            return services;
        }
    }
}
