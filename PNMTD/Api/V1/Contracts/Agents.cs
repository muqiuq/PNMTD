using System.Text.Json.Serialization;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Servers with an agent, Windows and Linux (docs/windows-sensor-plan.md §9.2; docs/linux-sensor-plan.md §10): under
// /api/v1/agents/ since L1. A Windows server's typed sections and WMI are in Windows.cs.

/// <summary>A server in the fleet list (§10.1), of either platform.</summary>
public sealed record AgentServerItem(
    Guid SensorId,
    Ref Host,
    string Name,
    AgentPlatform Platform,
    SensorState State,
    int? LastCode,
    string? LastMessage,
    string Hostname,
    string? Domain,
    string? OsCaption,
    string? OsBuild,
    bool OsSupported,
    string? Architecture,
    string? AgentVersion,
    bool Outdated,
    bool Connected,
    DateTime? LastSeenAt,
    DateTime? BootTime,
    int? ClockSkewSeconds,
    bool Revoked,
    bool Uninstalled,
    DateTime? LastProblemAt,
    string? RemoteAccess,
    int AuditGaps,
    int DiagnosticsFailing);

public sealed class AgentServerQuery : PageQuery
{
    /// <summary>Only the servers of one platform (docs/linux-sensor-plan.md §10).</summary>
    public AgentPlatform? Platform { get; set; }

    public SensorState? State { get; set; }

    /// <summary>Only servers on an OS version the agent does not support.</summary>
    public bool? Unsupported { get; set; }

    /// <summary>Only servers whose agent is older than PNMTD.</summary>
    public bool? Outdated { get; set; }

    public bool? Connected { get; set; }

    /// <summary>Only servers with remote access open (RDP, WinRM, SMB, …).</summary>
    public bool? RemoteAccess { get; set; }

    /// <summary>Only servers whose audit policy misses something.</summary>
    public bool? AuditGaps { get; set; }

    /// <summary>Only servers with a failing diagnostics assignment.</summary>
    public bool? DiagnosticsFailing { get; set; }

    public Guid? HostId { get; set; }
}

/// <summary>When a section was collected, and its collector's errors.</summary>
public sealed record AgentSectionInfo(string Name, DateTime CollectedAt, DateTime ReceivedAt, IReadOnlyList<string> Errors);

/// <summary>A server's page (§10.2): the agent, its connection, the last heartbeat, its switches and recent problems.</summary>
public sealed record AgentServerDetail(
    AgentServerItem Server,
    Guid AgentId,
    string KeyFingerprint,
    string PnmtdFingerprint,
    DateTime JoinedAt,
    DateTime? ConnectedAt,
    DateTime? DisconnectedAt,
    string? RemoteAddress,
    string? Fqdn,
    string? OsEdition,
    string? OsDisplayVersion,
    string? MachineId,
    IReadOnlyList<string> Capabilities,
    int ConfigVersion,
    int CurrentConfigVersion,
    Heartbeat? Heartbeat,
    AgentServerSettings Settings,
    IReadOnlyList<AgentLogItem> RecentProblems,
    IReadOnlyList<Ref> SameMachine,
    IReadOnlyList<AgentSectionInfo> Sections,
    string? RevokedReason,
    DateTime? RevokedAt,
    DateTime? UninstalledAt,
    string PnmtVersion);

/// <summary>A server's event switches (§4.7, §4.8), with its platform's codes (254, 454, 453; 264, 464, 463).</summary>
public sealed record AgentServerSettings(bool AdminLoginEvents, bool FailedLoginEvents, int FailedLoginThreshold, bool CriticalEvents);

public sealed record AgentLogItem(Guid Id, Guid? SensorId, string AgentName, DateTime At, AgentLogKind Kind, string Message,
    string? Details, string? Address);

public sealed class AgentLogQuery : PageQuery
{
    public AgentLogKind? Kind { get; set; }

    public Guid? SensorId { get; set; }
}

/// <summary>A join token in the list: never the token itself, which is shown once.</summary>
public sealed record AgentJoinTokenItem(
    Guid Id,
    AgentPlatform Platform,
    string Prefix,
    Ref Host,
    string? Name,
    Ref? Rejoin,
    DateTime CreatedAt,
    string? CreatedByName,
    DateTime ExpiresAt,
    DateTime? UsedAt,
    string? UsedFromAddress,
    Ref? Result,
    DateTime? RevokedAt,
    AgentJoinTokenState State);

[JsonConverter(typeof(JsonStringEnumConverter<AgentJoinTokenState>))]
public enum AgentJoinTokenState { Pending, Used, Expired, Revoked }

public sealed class AgentJoinTokenQuery : PageQuery
{
    public AgentJoinTokenState? State { get; set; }
}

/// <summary>
/// Add server (§3.3): the host it joins, an optional name, how long the token is valid (1 to 168 hours), and the platform
/// it is for (docs/linux-sensor-plan.md §10; Windows when missing): an agent of the other platform is refused.
/// </summary>
public sealed record AgentJoinTokenRequest(Guid HostId, string? Name, int? ValidHours, AgentPlatform Platform = AgentPlatform.Windows);

/// <summary>A re-join token (§3.3): bound to the server, whose key the join replaces.</summary>
public sealed record AgentRejoinTokenRequest(int? ValidHours);

/// <summary>
/// The new token, shown once, with what the server's administrator needs. <see cref="CommandLine"/> and
/// <see cref="Downloads"/> are Windows' for now: the Linux agent's install line comes with its release (L3).
/// </summary>
public sealed record AgentJoinTokenCreated(
    AgentJoinTokenItem Item,
    string Token,
    string Url,
    string PnmtdFingerprint,
    string CommandLine,
    AgentDownloadLinks Downloads);

/// <summary>PNMTD's version and the agent's release links (§10.1), with the SHA-256 when GitHub could be asked. Windows' for now.</summary>
public sealed record AgentDownloadLinks(string Version, IReadOnlyList<AgentDownloadLink> Files, string? ReleaseUrl, string? Note);

public sealed record AgentDownloadLink(string Architecture, string FileName, string Url, string? Sha256);

public sealed record AgentRevokeRequest(string? Reason);

/// <summary>Collect these sections now (empty: all).</summary>
public sealed record AgentCollectRequest(IReadOnlyList<string>? Sections);

/// <summary>What the agent answered to a request.</summary>
public sealed record AgentRequestResult(bool Ok, string? Error);

/// <summary>The last lines of the agent's log file.</summary>
public sealed record AgentLogFile(DateTime At, IReadOnlyList<string> Lines);

/// <summary>A login or connection (§4.7): an aggregate when <see cref="Count"/> is above 1.</summary>
public sealed record AgentLoginItem(
    Guid Id,
    DateTime At,
    DateTime? LastAt,
    int Count,
    LogonOutcome Outcome,
    string? FailureReason,
    string User,
    string? Domain,
    bool IsAdmin,
    LogonKind Kind,
    string? AuthPackage,
    string? SourceAddress,
    int? SourcePort,
    string? Workstation,
    bool? Elevated,
    DateTime? LoggedOffAt,
    string Channel);

public sealed class AgentLoginQuery : PageQuery
{
    public LogonOutcome? Outcome { get; set; }

    public bool? Admin { get; set; }

    public LogonKind? Kind { get; set; }

    public string? User { get; set; }

    public string? Source { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

public sealed record AgentChangeItem(Guid Id, string Section, DateTime At, string Summary, bool Notable, string? Details);

public sealed class AgentChangeQuery : PageQuery
{
    public string? Section { get; set; }

    public bool? Notable { get; set; }
}
