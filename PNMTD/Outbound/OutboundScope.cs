using PNMTD.Models.Db;

namespace PNMTD.Outbound;

/// <summary>
/// What a send is for and who asked for it (docs/redmine-queue-outbound-plan.md §6.3), for the notification log.
/// <see cref="Recorded"/>: a record was written for it, so <c>NotificationService</c> doesn't write one of its own.
/// </summary>
public sealed class OutboundContext(string purpose, string askedBy, OutboundChannel channel, string? recipient, string summary)
{
    public string Purpose { get; } = purpose;

    public string AskedBy { get; } = askedBy;

    public OutboundChannel Channel { get; } = channel;

    public string? Recipient { get; } = recipient;

    public string Summary { get; } = summary;

    public bool Recorded { get; set; }
}

/// <summary>
/// The current send's <see cref="OutboundContext"/>, flowing with the async call: <c>NotificationService</c> opens it, the
/// handler on the <c>notifications</c> client reads it, so no provider's signature changes.
/// </summary>
public static class OutboundScope
{
    private static readonly AsyncLocal<OutboundContext?> current = new();

    public static OutboundContext? Current => current.Value;

    public static IDisposable Begin(OutboundContext context)
    {
        var previous = current.Value;
        current.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore(OutboundContext? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}
