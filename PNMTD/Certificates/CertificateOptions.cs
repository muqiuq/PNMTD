namespace PNMTD.Certificates;

/// <summary>SSL certificate sensors (docs/ssl-certificate-plan.md §5.5), from .env as <c>Certificates__*</c>; every one optional.</summary>
public sealed class CertificateOptions
{
    public const string Section = "Certificates";

    /// <summary>Off: no checks and no evaluations; the sensors keep their states.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The connection and the handshake together.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>The next check after a failed one.</summary>
    public int RetryMinutes { get; set; } = 15;

    /// <summary>Failed checks in a row before 489.</summary>
    public int FailuresBeforeUnreachable { get; set; } = 2;

    /// <summary>The check's rhythm while the verdict fails (485 to 488).</summary>
    public int FailingCheckMinutes { get; set; } = 60;

    /// <summary>Checks at once.</summary>
    public int MaxConcurrent { get; set; } = 8;

    /// <summary>The warning's default for new sensors.</summary>
    public int WarnDays { get; set; } = 14;

    /// <summary>Versions kept per sensor; the current, accepted and announced ones always.</summary>
    public int KeepVersions { get; set; } = 50;

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 2, 120));

    public TimeSpan Retry => TimeSpan.FromMinutes(Math.Clamp(RetryMinutes, 1, 24 * 60));

    public TimeSpan FailingCheck => TimeSpan.FromMinutes(Math.Clamp(FailingCheckMinutes, 5, 24 * 60));

    public int Failures => Math.Clamp(FailuresBeforeUnreachable, 1, 20);

    public int DefaultWarnDays => Math.Clamp(WarnDays, 1, 365);
}
