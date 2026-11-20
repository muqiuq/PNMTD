namespace PNMTD.Outbound;

/// <summary>The two outbound logs (docs/redmine-queue-outbound-plan.md §6, §9): how many records each keeps, and how much of a body.</summary>
public sealed class OutboundOptions
{
    public const string Section = "Outbound";

    /// <summary>The notification log: the newest e-mails, Pushover messages and HTTP notifications kept.</summary>
    public int KeepNotifications { get; set; } = 10_000;

    /// <summary>The Redmine log: the newest Redmine calls kept.</summary>
    public int KeepRedmine { get; set; } = 1_000;

    /// <summary>Per body, before compression; what is longer is cut and marked.</summary>
    public int MaxBodyKiB { get; set; } = 256;

    public int MaxBodyBytes => Math.Clamp(MaxBodyKiB, 1, 64 * 1024) * 1024;
}
