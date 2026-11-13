namespace PNMTD.Mikrotik;

/// <summary>The <c>Mikrotik</c> settings (docs/mikrotik-plan.md §9.4): Mikrotik__Enabled, … in the environment.</summary>
public sealed class MikrotikOptions
{
    public const string Section = "Mikrotik";

    /// <summary>The poller, the version check and the jobs. Off: routers are neither polled nor changed.</summary>
    public bool Enabled { get; set; } = true;

    public SshOptions Ssh { get; set; } = new();

    public VersionCheckOptions VersionCheck { get; set; } = new();

    public BackupOptions Backups { get; set; } = new();

    public UpdateOptions Updates { get; set; } = new();

    public CommandOptions Commands { get; set; } = new();

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

    public sealed class BackupOptions
    {
        /// <summary>Every version younger than this is kept (§6.3)…</summary>
        public int KeepDays { get; set; } = 365;

        /// <summary>…and at least this many per router, whatever their age (at least 1).</summary>
        public int KeepVersions { get; set; } = 50;

        public int MaxExportBytes { get; set; } = 16 * 1024 * 1024;

        /// <summary>After a failed export, the next try waits this long (the poll in between goes on).</summary>
        public int RetryMinutes { get; set; } = 30;
    }

    public sealed class UpdateOptions
    {
        /// <summary>A router that does not answer again this long after its reboot fails the job (§7.5).</summary>
        public int RebootTimeoutMinutes { get; set; } = 15;

        public int DownloadTimeoutMinutes { get; set; } = 10;

        /// <summary>How long the admin password stays in memory for a batch (§7.2).</summary>
        public int CredentialLifetimeMinutes { get; set; } = 60;

        /// <summary>How long a step waits between its checks of the router (the download, the return after a reboot).</summary>
        public int PollSeconds { get; set; } = 10;

        /// <summary>Less free storage refuses the update at its check (a package is 10–20 MB).</summary>
        public long MinimumFreeStorageBytes { get; set; } = 24L * 1024 * 1024;

        public long MaxBinaryBackupBytes { get; set; } = 64L * 1024 * 1024;
    }

    public sealed class CommandOptions
    {
        /// <summary>The text of a remote command, in UTF-8 (§8.2). M6 ran 64 KB in one exec on 7.25.1.</summary>
        public int MaxCommandBytes { get; set; } = 16 * 1024;

        public int DefaultTimeoutSeconds { get; set; } = 60;

        public int MaxTimeoutSeconds { get; set; } = 600;

        /// <summary>The output is read up to this (§8.3)…</summary>
        public int MaxOutputBytes { get; set; } = 1024 * 1024;

        /// <summary>…and kept up to this, marked as cut beyond it.</summary>
        public int MaxStoredOutputBytes { get; set; } = 256 * 1024;

        /// <summary>Jobs that run at once, over all batches; a batch run one at a time takes one of them.</summary>
        public int Parallelism { get; set; } = 4;
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
