namespace PNMTD.Mikrotik;

/// <summary>The <c>Mikrotik</c> settings (docs/mikrotik-plan.md §9.4): Mikrotik__Enabled, … in the environment.</summary>
public sealed class MikrotikOptions
{
    public const string Section = "Mikrotik";

    /// <summary>The poller, the version check and the jobs. Off: routers are neither polled nor changed.</summary>
    public bool Enabled { get; set; } = true;

    public SshOptions Ssh { get; set; } = new();

    public VersionCheckOptions VersionCheck { get; set; } = new();

    /// <summary>Admin passwords only over HTTPS (§7.2), unless this is set: development only.</summary>
    public bool AllowPasswordsOverHttp { get; set; }

    public sealed class SshOptions
    {
        public int ConnectTimeoutSeconds { get; set; } = 15;

        public int CommandTimeoutSeconds { get; set; } = 60;

        public int ExportTimeoutSeconds { get; set; } = 120;

        /// <summary>Sessions open at once, over all routers (§4.5). One per router at a time in any case.</summary>
        public int MaxConcurrentSessions { get; set; } = 8;

        /// <summary>The output of a poll or a command is cut here (§4.5).</summary>
        public int MaxOutputBytes { get; set; } = 1024 * 1024;
    }

    public sealed class VersionCheckOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>MikroTik's update server: <c>NEWESTa7.&lt;channel&gt;</c> and <c>&lt;version&gt;/CHANGELOG</c> below it (§5.4).</summary>
        public string BaseUrl { get; set; } = "https://upgrade.mikrotik.com/routeros/";

        public int IntervalHours { get; set; } = 6;

        public string[] Channels { get; set; } = ["stable", "long-term", "testing", "development"];
    }
}
