using PNMTD.Models.Enums;
using PNMTD.WireGuard;

namespace PNMTD.Api.V1.Contracts;

/// <summary>The admin page's overview (docs/mikrotik-plan.md §11.2): the gateway, the endpoint in effect, the peers.</summary>
public sealed record WireGuardStatus(
    bool Enabled,
    bool GatewayUp,
    DateTime? GatewaySeenAt,
    string? GatewayVersion,
    string? GatewayError,
    string? Endpoint,
    string? ExternalHost,
    WgSettingSource ExternalHostSource,
    int ExternalPort,
    WgSettingSource ExternalPortSource,
    int PublishedPort,
    string Network,
    string ServerAddress,
    string AccessAddress,
    string GatewayPublicKey,
    int Peers,
    int PeersUp,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The network's settings (§3.3), each with where its value comes from. A setting the environment sets is
/// locked: the UI shows it with the variable's name and cannot change it.
/// </summary>
public sealed record WireGuardNetworkView(
    string Prefix,
    string? ExternalHost,
    string? ExternalHostInEffect,
    WgSettingSource ExternalHostSource,
    bool ExternalHostLocked,
    int? ExternalPort,
    int ExternalPortInEffect,
    WgSettingSource ExternalPortSource,
    bool ExternalPortLocked,
    int PublishedPort,
    int ListenPort,
    int KeepaliveSeconds,
    int Mtu,
    int StaleHandshakeSeconds,
    IReadOnlyList<int> ManagementPorts,
    DateTime ChangedAt);

/// <summary>
/// <c>PUT /wireguard/network</c>. A new prefix re-addresses every peer and needs <see cref="ConfirmPrefix"/>
/// equal to it. The external host and port are refused when the environment sets them.
/// </summary>
public sealed class WireGuardNetworkRequest
{
    public string Prefix { get; init; } = "";

    public string? ConfirmPrefix { get; init; }

    public string? ExternalHost { get; init; }

    public int? ExternalPort { get; init; }

    public int KeepaliveSeconds { get; init; } = 25;

    public int Mtu { get; init; } = 1420;

    public int StaleHandshakeSeconds { get; init; } = 180;

    public IReadOnlyList<int> ManagementPorts { get; init; } = [22, 8291, 80, 443];
}

/// <summary>The saved settings, and the routers and tunnels that need their new script or file.</summary>
public sealed record WireGuardNetworkSaved(WireGuardNetworkView Network, IReadOnlyList<string> NeedNewScripts);

/// <summary>A peer, router or custom, with its tunnel as the gateway sees it (§11.2).</summary>
public sealed record WireGuardPeerItem(
    Guid Id,
    WgPeerKind Kind,
    string Name,
    Guid? SensorId,
    string Address,
    string PublicKey,
    bool Enabled,
    bool Up,
    DateTime? LatestHandshake,
    string? Endpoint,
    long RxBytes,
    long TxBytes,
    int EndpointChanges24h,
    DateTime? ExpiresAt);

/// <summary>
/// <c>POST</c> and <c>PUT /wireguard/tunnels</c> (§3.5): a custom tunnel, optionally for a PNMT user who may then
/// download it, reaching all routers or the selected ones (each only with its management access on).
/// </summary>
public sealed class WireGuardTunnelRequest
{
    public string? Name { get; init; }

    public string? Notes { get; init; }

    public Guid? OwnerUserId { get; init; }

    public bool AccessAllRouters { get; init; }

    public IReadOnlyList<Guid> RouterIds { get; init; } = [];

    public DateTime? ExpiresAt { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>A custom tunnel with its tunnel state and the routers it actually reaches.</summary>
public sealed record WireGuardTunnelItem(
    Guid Id,
    string Name,
    string? Notes,
    Ref? Owner,
    string Address,
    string PublicKey,
    bool Enabled,
    bool Expired,
    DateTime? ExpiresAt,
    bool AccessAllRouters,
    IReadOnlyList<Ref> Routers,
    IReadOnlyList<WireGuardTunnelTarget> Reaches,
    bool Up,
    DateTime? LatestHandshake,
    string? Endpoint,
    long RxBytes,
    long TxBytes,
    DateTime KeysCreatedAt,
    DateTime CreatedAt);

/// <summary>A router a tunnel reaches: on its tunnel address, on the management ports.</summary>
public sealed record WireGuardTunnelTarget(Guid SensorId, string Name, string Address);

/// <summary>A tunnel's wg-quick file: for the WireGuard apps, or as a QR code for a phone.</summary>
public sealed record WireGuardTunnelConfig(string FileName, string Content, IReadOnlyList<int> ManagementPorts);
