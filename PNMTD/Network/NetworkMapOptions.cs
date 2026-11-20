namespace PNMTD.Network;

/// <summary>
/// <c>NetworkMap:*</c> (docs/network-map-plan.md §9.4). The schedule, its hour and the retention are in the database, set on
/// the Scans tab; these are the limits of a scan.
/// </summary>
public sealed class NetworkMapOptions
{
    public const string Section = "NetworkMap";

    /// <summary>Off: no scans, and the menu entry is hidden.</summary>
    public bool Enabled { get; set; } = true;

    public ScanOptions Scan { get; set; } = new();

    public AnalysisOptions Analysis { get; set; } = new();

    public sealed class ScanOptions
    {
        /// <summary>Per table: each is one exec of its own.</summary>
        public int TableTimeoutSeconds { get; set; } = 120;

        /// <summary>Per table. A core switch's MAC table of 3,000 entries is about 450 KB.</summary>
        public int MaxOutputBytes { get; set; } = 8 * 1024 * 1024;

        /// <summary>How long a scan waits for the router's lock (a poll, a job) before it skips the router as busy.</summary>
        public int LockWaitSeconds { get; set; } = 120;

        /// <summary>Runs and their routers are kept this long.</summary>
        public int HistoryDays { get; set; } = 365;

        /// <summary>The worker's own loop. Off, routers are scanned only by <see cref="NetworkScanner.RunHereAsync"/> (the tests).</summary>
        public bool Background { get; set; } = true;
    }

    public sealed class AnalysisOptions
    {
        /// <summary>During a long run, the analysis runs at most this often, so suggestions come while it goes (§5.1).</summary>
        public int IntervalMinutes { get; set; } = 5;
    }
}
