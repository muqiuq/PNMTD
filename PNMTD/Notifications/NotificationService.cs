using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Outbound;

namespace PNMTD.Notifications
{
    /// <summary>
    /// Picks the provider by the recipient's form and sends (docs/ai-operations-plan.md §8.3). It logs the
    /// provider and the result, never the recipient: a Pushover key or a webhook URL can carry secrets. Every send goes
    /// into the notification log (docs/redmine-queue-outbound-plan.md §6): the providers record what they send, under the
    /// scope opened here; a simulated send, and one that failed before anything went out, are recorded here.
    /// </summary>
    public sealed class NotificationService(IEnumerable<INotificationProvider> providers, OutboundLog outbound, ILogger<NotificationService> logger)
    {
        public const string HttpClientName = "notifications";

        public INotificationProvider? ProviderFor(string recipient) => providers.FirstOrDefault(p => p.IsMatch(recipient));

        /// <summary>
        /// Null when no provider takes the recipient. <paramref name="simulate"/>: logged, not sent (Development).
        /// <paramref name="context"/> is what it is for, <paramref name="askedBy"/> who asked (a person's name, Cookie; PNMT by default).
        /// </summary>
        public async Task<DeliveryResult?> SendAsync(NotificationMessage message, string context, bool simulate, CancellationToken ct, string? askedBy = null)
        {
            if (ProviderFor(message.Recipient) is not { } provider)
            {
                logger.LogWarning("No notification provider for the recipient of {Context}", context);
                return null;
            }
            var channel = provider.Name switch
            {
                "email" => OutboundChannel.Email,
                "pushover" => OutboundChannel.Pushover,
                _ => OutboundChannel.Http,
            };
            var recipient = channel == OutboundChannel.Pushover ? OutboundRedactor.PushoverRecipient(message.Recipient) : message.Recipient;
            var scope = new OutboundContext(context, askedBy ?? "PNMT", channel, recipient, message.Subject);
            using var _ = OutboundScope.Begin(scope);
            var started = Clock.UtcNow;
            if (simulate)
            {
                logger.LogInformation("Simulated {Provider} notification for {Context}: {Subject}", provider.Name, context, message.Subject);
                var simulated = new DeliveryResult(true, "Simulated (Development:DoNotSendNotifications).", TimeSpan.Zero);
                await RecordAsync(provider, message, scope, started, simulated, OutboundOutcome.Simulated, ct);
                return simulated;
            }
            var result = await provider.SendAsync(message, ct);
            if (!scope.Recorded)
            {
                // Nothing went out (no key, no server, not an address): the log says why.
                await RecordAsync(provider, message, scope, started, result, result.Ok ? OutboundOutcome.Ok : OutboundOutcome.Failed, ct);
            }
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

        private async Task RecordAsync(INotificationProvider provider, NotificationMessage message, OutboundContext scope, DateTime started,
            DeliveryResult result, OutboundOutcome outcome, CancellationToken ct)
        {
            OutboundDescription? described = null;
            try
            {
                described = await provider.DescribeAsync(message, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The record says what failed; the description is a courtesy.
            }
            outbound.Record(new OutboundNotificationEntity
            {
                StartedAt = started, DurationMs = (int)result.Duration.TotalMilliseconds, Channel = scope.Channel, Method = described?.Method ?? "",
                Target = described?.Target ?? "", Recipient = scope.Recipient, Summary = scope.Summary, Purpose = scope.Purpose, AskedBy = scope.AskedBy,
                Outcome = outcome, Error = result.Ok ? null : result.Detail,
                RequestHeaders = OutboundRedactor.Json(described?.Headers.ToList() ?? []),
            }, described?.Body, outcome == OutboundOutcome.Simulated ? null : result.Detail);
        }
    }

    public static class NotificationSetup
    {
        public static IServiceCollection AddNotifications(this IServiceCollection services)
        {
            // Every Pushover message and HTTP notification into the notification log (docs/redmine-queue-outbound-plan.md §6.3).
            services.AddTransient<OutboundHttpHandler>();
            services.AddHttpClient(NotificationService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
                .AddHttpMessageHandler<OutboundHttpHandler>();
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
