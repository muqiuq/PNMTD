using PNMTD.Lib.Agent;

namespace PNMTD.Api.V1.Contracts;

// What stays Windows' own after L1 (docs/linux-sensor-plan.md §3.3, §10): a Windows server's typed sections and its WMI, under
// /api/v1/windows/servers/{id}/. The shared contracts are in Agents.cs.

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

/// <summary>
/// A server's WMI (§16): PNMT's settings for it, and what the agent last reported (its built-in queries as they ran, the own
/// queries' rows, what is switched off on the server). <see cref="CollectedAt"/> is null until it reported.
/// </summary>
public sealed record WindowsWmiView(WmiConfig Settings, DateTime? CollectedAt, WmiSection? Section, bool Connected);
