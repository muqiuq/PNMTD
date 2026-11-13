using PNMTD.Lib.Models.Enum;

namespace PNMTD.Notifications
{
    /// <summary>
    /// One notification, as a provider sends it. <see cref="Priority"/> and <see cref="Device"/> are the
    /// debug page's (docs/ai-operations-plan.md §8.2); otherwise Pushover's priority follows the importance (§11.5).
    /// </summary>
    public sealed record NotificationMessage(
        string Recipient,
        string Subject,
        string Short,
        string Long,
        Importance Importance = Importance.Normal,
        int? Priority = null,
        string? Device = null);

    /// <summary>What the provider's server answered (§8.3): shown on the debug page, logged by the notification task.</summary>
    public sealed record DeliveryResult(bool Ok, string Detail, TimeSpan Duration)
    {
        public static DeliveryResult Failed(string detail, TimeSpan duration) => new(false, detail, duration);
    }

    public interface INotificationProvider
    {
        /// <summary>pushover, email or http, as <c>NotificationProviders.Match</c> names them.</summary>
        string Name { get; }

        bool IsMatch(string recipient);

        /// <summary>Never throws for a delivery problem: it is the result.</summary>
        Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct);
    }
}
