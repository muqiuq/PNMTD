namespace PNMTD.Udp;

/// <summary>The <c>Udp</c> settings (docs/udp-iot-plan.md §4.8): Udp__Enabled, Udp__Port, … in the environment.</summary>
public sealed class UdpOptions
{
    public const string Section = "Udp";

    /// <summary>On by default (review decision 2): it answers only packets from nodes defined in PNMT.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>5028 by default; 0 binds a free port (the tests).</summary>
    public int Port { get; set; } = 5028;

    /// <summary>"host:port" as the UI shows it; default: PublicBaseUrl's host and <see cref="Port"/>.</summary>
    public string? PublicEndpoint { get; set; }

    public int Workers { get; set; } = 4;

    public int QueueLength { get; set; } = 10_000;

    public int RetransmissionWindowSeconds { get; set; } = 300;

    /// <summary>At most one JOIN per node within this many seconds (§3.8).</summary>
    public int JoinIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// A new node without a device id gets a random free one that fits in this many header bytes
    /// (1–4): 2 gives 1–65,535. A taken id is followed by the next ones, at most 100 in all.
    /// </summary>
    public int DeviceIdBytes { get; set; } = 2;

    public RateLimitOptions RateLimit { get; set; } = new();

    public DiagnosticsOptions Diagnostics { get; set; } = new();

    public CommandOptions Commands { get; set; } = new();

    public OtaOptions Ota { get; set; } = new();

    public sealed class RateLimitOptions
    {
        public int PerNodePerMinute { get; set; } = 30;

        public int PerNodeBurst { get; set; } = 10;

        public int PerAddressPerSecond { get; set; } = 50;
    }

    public sealed class DiagnosticsOptions
    {
        public int RecentRejectionsPerNode { get; set; } = 20;

        public int UnknownSenders { get; set; } = 200;

        /// <summary>The "rejected packets" warning (§4.10): this many bad tags within <see cref="BadTagWindowMinutes"/>.</summary>
        public int BadTagWarningThreshold { get; set; } = 20;

        public int BadTagWindowMinutes { get; set; } = 10;
    }

    public sealed class CommandOptions
    {
        public int MaxOpenPerNode { get; set; } = 32;

        public int DefaultExpiryHours { get; set; } = 24;

        public int DefaultMaxAttempts { get; set; } = 3;

        public int MaxBytes { get; set; } = 480;
    }

    public sealed class OtaOptions
    {
        public int MaxImageBytes { get; set; } = 16 * 1024 * 1024;

        public int DefaultChunkBytes { get; set; } = 448;

        public int DefaultOfferExpiryDays { get; set; } = 7;

        public int DefaultMaxOfferAttempts { get; set; } = 20;

        /// <summary>The raised rate limit for OTA GETs while an update is open.</summary>
        public int GetsPerSecondPerNode { get; set; } = 20;

        public long CacheBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>A download without a GET for this long is marked failed ("stalled").</summary>
        public int StallHours { get; set; } = 24;
    }
}
