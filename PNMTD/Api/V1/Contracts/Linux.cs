using PNMTD.Lib.Agent;

namespace PNMTD.Api.V1.Contracts;

// What is Linux's own (docs/linux-sensor-plan.md §10): a Linux server's typed sections, under /api/v1/linux/servers/{id}/. The
// shared contracts are in Agents.cs.

/// <summary>
/// A Linux server's section (§5), typed by Linux's records: the one asked for is filled in, the others are null. hardware and
/// network are the shared records. <c>access</c>, <c>journal</c> and <c>ssh-keys</c> need CanWrite (§9.7): they hold user names,
/// addresses and key comments. A section PNMT can't read names the reason first in <see cref="Errors"/>.
/// </summary>
public sealed record LinuxSectionView(
    string Name,
    DateTime CollectedAt,
    DateTime ReceivedAt,
    DateTime ChangedAt,
    IReadOnlyList<string> Errors,
    LinuxSystemSection? System = null,
    HardwareSection? Hardware = null,
    NetworkSection? Network = null,
    LinuxServicesSection? Services = null,
    LinuxRemoteAccessSection? RemoteAccess = null,
    LinuxAccessSection? Access = null,
    JournalSection? Journal = null,
    SshKeysSection? SshKeys = null);
