namespace PNMTD.Windows;

/// <summary>The <c>Windows</c> settings (docs/windows-sensor-plan.md §8.5): Windows__Enabled, … in the environment.</summary>
public sealed class WindowsOptions
{
    public const string Section = "Windows";

    /// <summary>Off: the agent endpoints answer 404, and connected agents are closed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Agents may join over plain HTTP. Development only, as Mikrotik:AllowPasswordsOverHttp.</summary>
    public bool AllowHttp { get; set; }

    /// <summary>Sent to the agents in their configuration; also a new server's Interval.</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    public int FullUpdateHours { get; set; } = 6;

    /// <summary>How long a join token is valid by default; at most 168.</summary>
    public int TokenHours { get; set; } = 24;

    /// <summary>A heartbeat whose clock is further off writes 451 (§3.7).</summary>
    public int MaxClockSkewSeconds { get; set; } = 120;

    /// <summary>A message on the wire; decompressed 4 times as much (§3.5).</summary>
    public int MaxMessageBytes { get; set; } = Lib.Agent.AgentProtocol.DefaultMaxMessageBytes;

    /// <summary>Joins per minute and address. 50 failures in an hour close the join for 15 minutes (§8.7).</summary>
    public int JoinRateLimitPerMinute { get; set; } = 10;

    /// <summary>Messages per minute and agent besides sections, logins and results (§8.7).</summary>
    public int MessagesPerMinute { get; set; } = 60;

    /// <summary>Sections, login batches and diagnostics results per minute and agent.</summary>
    public int ReportsPerMinute { get; set; } = 300;

    /// <summary>
    /// The origin an agent signs must be PNMT's: PublicBaseUrl's host, or the host the request came to. Off only for
    /// proxies that rewrite the Host header and no PublicBaseUrl.
    /// </summary>
    public bool CheckOrigin { get; set; } = true;

    /// <summary>A socket that has not sent its hello within this time is closed (4408, §3.4).</summary>
    public int HelloTimeoutSeconds { get; set; } = 10;

    /// <summary>For the Download tab: <c>{base}/v{version}/pnmt-sensor-x64.exe</c> (§11).</summary>
    public string AgentDownloadBaseUrl { get; set; } = "https://github.com/muqiuq/PNMTD/releases/download";

    /// <summary>A burst of failed logins (454) is counted over this many minutes (§4.7).</summary>
    public int FailedLoginWindowMinutes { get; set; } = 10;

    /// <summary>Logins, changes (180) and the agent log (365) are kept this many days.</summary>
    public int KeepLoginsDays { get; set; } = 180;

    public int KeepChangesDays { get; set; } = 180;

    public int KeepAgentLogDays { get; set; } = 365;
}
