namespace PNMTD.OsUpdates;

/// <summary>
/// The <c>OsUpdates</c> settings (docs/linux-os-updates-plan.md §8.1): <c>OsUpdates__Enabled</c>, … in the environment. Read through
/// <c>IOptionsMonitor</c>, so a change applies without a restart (the window reaches the agents with their next configuration).
/// </summary>
public sealed class OsUpdatesOptions
{
    public const string Section = "OsUpdates";

    /// <summary>Off: no daily update anywhere and no buttons; the tab shows the pending packages only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The daily <c>apt-get update</c> (§4.2).</summary>
    public bool DailyUpdate { get; set; } = true;

    /// <summary>The window's start in each server's local time, HH:mm; each server gets a fixed slot inside it.</summary>
    public string WindowStart { get; set; } = "04:00";

    public int WindowMinutes { get; set; } = 120;

    /// <summary>The newest this many update runs per server, with their logs (R6).</summary>
    public int KeepUpdateLogs { get; set; } = 50;

    /// <summary>The newest this many upgrade runs per server (PNMT's and others' from history.log), with their logs (R6).</summary>
    public int KeepUpgradeLogs { get; set; } = 200;

    /// <summary>One log at most: a longer one keeps its first MiB and its end.</summary>
    public long MaxLogBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>An upgrade running longer is marked "taking long"; it is never stopped (§4.3).</summary>
    public int UpgradeWarnMinutes { get; set; } = 60;

    /// <summary>Lists older than this many days are 467 (§5.4).</summary>
    public int ListsStaleDays { get; set; } = 3;

    /// <summary>Resolved findings are kept this many days.</summary>
    public int KeepFindingsDays { get; set; } = 365;

    public AdvisoryOptions Advisories { get; set; } = new();

    public int KeepUpdates => Math.Clamp(KeepUpdateLogs, 1, 10_000);

    public int KeepUpgrades => Math.Clamp(KeepUpgradeLogs, 1, 10_000);

    public long MaxLog => Math.Clamp(MaxLogBytes, 1024 * 1024, 64L * 1024 * 1024);

    public int WindowLength => Math.Clamp(WindowMinutes, 1, 24 * 60);

    /// <summary>The window's start when it is a valid HH:mm, else 04:00.</summary>
    public string Start => TimeOnly.TryParseExact(WindowStart, "HH:mm", out var t) ? t.ToString("HH:mm") : "04:00";

    /// <summary>
    /// The high-risk check (§6, §7): off by default. Its sources are in the database (Admin → Servers → Advisories); these are its
    /// thresholds and limits.
    /// </summary>
    public sealed class AdvisoryOptions
    {
        public bool Enabled { get; set; }

        /// <summary>EPSS percentile for high risk (D8): 0.99 is the top 1 % of all CVEs.</summary>
        public double HighRiskEpssPercentile { get; set; } = 0.99;

        /// <summary>EPSS percentile for important (D8).</summary>
        public double ImportantEpssPercentile { get; set; } = 0.90;

        /// <summary>The longest a high-risk finding may be acknowledged, in days (D7).</summary>
        public int AcknowledgeMaxDays { get; set; } = 30;

        /// <summary>A source not fetched successfully for this long is 434 (§5.4).</summary>
        public int StaleHours { get; set; } = 36;

        /// <summary>One download at most (the Debian tracker is about 92 MB, 15 MB with gzip, O0).</summary>
        public long MaxDownloadBytes { get; set; } = 256L * 1024 * 1024;

        /// <summary>A source's URL may name a private or loopback address (a local mirror).</summary>
        public bool AllowPrivateAddresses { get; set; }

        /// <summary>AI for the RSS sources' texts (§7): also needs <c>Ai__Enabled</c> and a key.</summary>
        public bool Ai { get; set; }

        public int MaxAcknowledgeDays => Math.Clamp(AcknowledgeMaxDays, 1, 365);
    }
}
