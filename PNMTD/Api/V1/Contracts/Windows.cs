using System.Text.Json.Serialization;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Windows servers and their agents (docs/windows-sensor-plan.md §9.2).

/// <summary>A server in the fleet list (§10.1).</summary>
public sealed record WindowsServerItem(
    Guid SensorId,
    Ref Host,
    string Name,
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

public sealed class WindowsServerQuery : PageQuery
{
    public SensorState? State { get; set; }

    /// <summary>Only servers on a Windows version the agent does not support.</summary>
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
public sealed record WindowsSectionInfo(string Name, DateTime CollectedAt, DateTime ReceivedAt, IReadOnlyList<string> Errors);

/// <summary>A server's page (§10.2): the agent, its connection, the last heartbeat, its switches and recent problems.</summary>
public sealed record WindowsServerDetail(
    WindowsServerItem Server,
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
    string? MachineGuid,
    IReadOnlyList<string> Capabilities,
    int ConfigVersion,
    int CurrentConfigVersion,
    Heartbeat? Heartbeat,
    WindowsServerSettings Settings,
    IReadOnlyList<WindowsAgentLogItem> RecentProblems,
    IReadOnlyList<Ref> SameMachine,
    IReadOnlyList<WindowsSectionInfo> Sections,
    string? RevokedReason,
    DateTime? RevokedAt,
    DateTime? UninstalledAt,
    string PnmtVersion);

/// <summary>A server's event switches (§4.7, §4.8).</summary>
public sealed record WindowsServerSettings(bool AdminLoginEvents, bool FailedLoginEvents, int FailedLoginThreshold, bool CriticalEvents);

public sealed record WindowsAgentLogItem(Guid Id, Guid? SensorId, string AgentName, DateTime At, WindowsAgentLogKind Kind, string Message,
    string? Details, string? Address);

public sealed class WindowsAgentLogQuery : PageQuery
{
    public WindowsAgentLogKind? Kind { get; set; }

    public Guid? SensorId { get; set; }
}

/// <summary>A join token in the list: never the token itself, which is shown once.</summary>
public sealed record WindowsJoinTokenItem(
    Guid Id,
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
    WindowsJoinTokenState State);

[JsonConverter(typeof(JsonStringEnumConverter<WindowsJoinTokenState>))]
public enum WindowsJoinTokenState { Pending, Used, Expired, Revoked }

public sealed class WindowsJoinTokenQuery : PageQuery
{
    public WindowsJoinTokenState? State { get; set; }
}

/// <summary>Add server (§3.3): the host it joins, an optional name, how long the token is valid (1 to 168 hours).</summary>
public sealed record WindowsJoinTokenRequest(Guid HostId, string? Name, int? ValidHours);

/// <summary>A re-join token (§3.3): bound to the server, whose key the join replaces.</summary>
public sealed record WindowsRejoinTokenRequest(int? ValidHours);

/// <summary>The new token, shown once, with what the server's administrator needs.</summary>
public sealed record WindowsJoinTokenCreated(
    WindowsJoinTokenItem Item,
    string Token,
    string Url,
    string PnmtdFingerprint,
    string CommandLine,
    WindowsAgentDownloads Downloads);

/// <summary>PNMTD's version and the agent's release links (§10.1), with the SHA-256 when GitHub could be asked.</summary>
public sealed record WindowsAgentDownloads(string Version, IReadOnlyList<WindowsAgentDownload> Files, string? ReleaseUrl, string? Note);

public sealed record WindowsAgentDownload(string Architecture, string FileName, string Url, string? Sha256);

public sealed record WindowsRevokeRequest(string? Reason);

/// <summary>Collect these sections now (empty: all).</summary>
public sealed record WindowsCollectRequest(IReadOnlyList<string>? Sections);

/// <summary>What the agent answered to a request.</summary>
public sealed record WindowsRequestResult(bool Ok, string? Error);

/// <summary>The last lines of the agent's log file.</summary>
public sealed record WindowsAgentLogFile(DateTime At, IReadOnlyList<string> Lines);

/// <summary>
/// A server's section (§4), typed: the one asked for is filled in, the others are null. <c>access</c> and <c>event-log</c>
/// need CanWrite (§6): they hold user names and addresses.
/// </summary>
public sealed record WindowsSectionView(
    string Name,
    DateTime CollectedAt,
    DateTime ReceivedAt,
    DateTime ChangedAt,
    IReadOnlyList<string> Errors,
    SystemSection? System = null,
    HardwareSection? Hardware = null,
    NetworkSection? Network = null,
    ServicesSection? Services = null,
    RemoteAccessSection? RemoteAccess = null,
    AccessSection? Access = null,
    EventLogSection? EventLog = null);

/// <summary>A login or connection (§4.7): an aggregate when <see cref="Count"/> is above 1.</summary>
public sealed record WindowsLoginItem(
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

public sealed class WindowsLoginQuery : PageQuery
{
    public LogonOutcome? Outcome { get; set; }

    public bool? Admin { get; set; }

    public LogonKind? Kind { get; set; }

    public string? User { get; set; }

    public string? Source { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

public sealed record WindowsChangeItem(Guid Id, string Section, DateTime At, string Summary, bool Notable, string? Details);

public sealed class WindowsChangeQuery : PageQuery
{
    public string? Section { get; set; }

    public bool? Notable { get; set; }
}
