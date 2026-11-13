using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Models.Enums;

namespace PNMTD.Models.Db;

/// <summary>
/// The tunnel network, one row (docs/mikrotik-plan.md §3.3, §9.2): the prefix, the gateway's keys, the
/// settings from the admin page, and the gateway's heartbeat. The environment's external host and port
/// win over the ones stored here.
/// </summary>
[Table("wg_network")]
public class WgNetworkEntity
{
    public const int SingletonId = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Id { get; set; } = SingletonId;

    /// <summary>"10.253.0.0/16".</summary>
    public string Prefix { get; set; } = "";

    public string PublicKey { get; set; } = "";

    /// <summary>The gateway's private key, under Data Protection (purpose PNMTD.WireGuard.Key.v1).</summary>
    public string PrivateKeyProtected { get; set; } = "";

    public int KeepaliveSeconds { get; set; } = 25;

    public int Mtu { get; set; } = 1420;

    public int StaleHandshakeSeconds { get; set; } = 180;

    /// <summary>Set on the admin page; the environment (WireGuard__ExternalHost) wins.</summary>
    public string? ExternalHost { get; set; }

    public int? ExternalPort { get; set; }

    /// <summary>What custom tunnels may reach on routers (§3.5).</summary>
    public int[] ManagementPorts { get; set; } = [22, 8291, 80, 443];

    /// <summary>The SOCKS5 password between the app and the gateway (§3.4), under Data Protection.</summary>
    public string SocksSecretProtected { get; set; } = "";

    /// <summary>When a setting that the join scripts carry last changed: scripts generated before are outdated.</summary>
    public DateTime ScriptSettingsChangedAt { get; set; }

    public DateTime? GatewaySeenAt { get; set; }

    /// <summary>When the gateway last started: its tunnels need a handshake again, which takes up to a few minutes.</summary>
    public DateTime? GatewayStartedAt { get; set; }

    public string? GatewayVersion { get; set; }

    public string? GatewayError { get; set; }

    public DateTime ChangedAt { get; set; }

    public Guid? ChangedBy { get; set; }
}

/// <summary>A tunnel: a router's, or a custom one (§3.5). PNMTD generated its keys.</summary>
[Table("wg_peers")]
public class WgPeerEntity
{
    [Key]
    public Guid Id { get; set; }

    public WgPeerKind Kind { get; set; }

    public string Name { get; set; } = "";

    public string? Notes { get; set; }

    /// <summary>Inside the prefix, after the server block.</summary>
    public string Address { get; set; } = "";

    public string PublicKey { get; set; } = "";

    /// <summary>Under Data Protection: the join script and the tunnel file show it again (§3.2).</summary>
    public string PrivateKeyProtected { get; set; } = "";

    public DateTime KeysCreatedAt { get; set; }

    public Guid? KeysCreatedBy { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>A router's peer: deleted with the sensor.</summary>
    public Guid? SensorId { get; set; }

    /// <summary>A custom tunnel's owner, who may download it.</summary>
    public Guid? OwnerUserId { get; set; }

    public bool AccessAllRouters { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}

/// <summary>A peer's tunnel as the gateway sees it, every 15 seconds (§3.2). Written by the gateway only.</summary>
[Table("wg_peer_status")]
public class WgPeerStatusEntity
{
    [Key]
    public Guid PeerId { get; set; }

    public string? Endpoint { get; set; }

    public DateTime? LatestHandshake { get; set; }

    public long RxBytes { get; set; }

    public long TxBytes { get; set; }

    /// <summary>How often the endpoint changed in the last 24 hours: two devices with one key take turns (§4.4).</summary>
    public int EndpointChanges24h { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>The routers a custom tunnel may reach, when not all of them (§3.5); each only with its management access on.</summary>
[Table("wg_peer_access")]
public class WgPeerAccessEntity
{
    public Guid PeerId { get; set; }

    public Guid RouterSensorId { get; set; }
}
