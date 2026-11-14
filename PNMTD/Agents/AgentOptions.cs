namespace PNMTD.Agents;

/// <summary>
/// The <c>Agents</c> settings (docs/linux-sensor-plan.md §9.5, which were W§8.5's <c>Windows__*</c> before L1):
/// Agents__Enabled, … in the environment. The same meanings and defaults for the agents of every platform.
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agents";

    /// <summary>Off: the agent endpoints answer 404, and connected agents are closed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sent to the agents in their configuration; also a new server's Interval.</summary>
    public int HeartbeatSeconds { get; set; } = 60;

    public int FullUpdateHours { get; set; } = 6;

    /// <summary>How long a join token is valid by default; at most 168.</summary>
    public int TokenHours { get; set; } = 24;

    /// <summary>A heartbeat whose clock is further off writes the platform's agent problem (451, 461; §3.7).</summary>
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

    /// <summary>
    /// The agents' GitHub release: <c>{base}/sensor-{version}/pnmt-sensor-x64.exe</c> (§11), for the links and for fetching into
    /// <see cref="DownloadFolder"/> (docs/linux-sensor-plan.md §4.4).
    /// </summary>
    public string AgentDownloadBaseUrl { get; set; } = "https://github.com/muqiuq/PNMTD/releases/download";

    /// <summary>
    /// <c>GET /agent/v1/download</c> and <c>/agent/v1/download/{asset}</c> (docs/linux-sensor-plan.md §4.4, §9.5): off, both answer
    /// 404, and the dialogs offer GitHub only.
    /// </summary>
    public bool ServeDownloads { get; set; } = true;

    /// <summary>
    /// Where PNMTD keeps the agents' files of its own version: filled from GitHub when empty, or a folder an admin filled (a PNMT
    /// without internet), or <c>dist</c> in development (relative to the working directory). Only files that match their
    /// <c>.sha256</c> are served.
    /// </summary>
    public string DownloadFolder { get; set; } = "/var/lib/pnmtd/downloads";

    /// <summary>Downloads of the agents' files per hour and address (§9.7).</summary>
    public int DownloadsPerHour { get; set; } = 20;

    /// <summary>A burst of failed logins (454, 464) is counted over this many minutes (§4.7).</summary>
    public int FailedLoginWindowMinutes { get; set; } = 10;

    /// <summary>Logins, changes (180) and the agent log (365) are kept this many days.</summary>
    public int KeepLoginsDays { get; set; } = 180;

    public int KeepChangesDays { get; set; } = 180;

    public int KeepAgentLogDays { get; set; } = 365;
}
