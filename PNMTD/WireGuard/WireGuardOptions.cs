namespace PNMTD.WireGuard;

/// <summary>The <c>WireGuard</c> settings (docs/mikrotik-plan.md §9.4): WireGuard__Enabled, … in the environment.</summary>
public sealed class WireGuardOptions
{
    public const string Section = "WireGuard";

    /// <summary>True with the compose profile wireguard (§9.5). Direct SSH works without.</summary>
    public bool Enabled { get; set; }

    /// <summary>PNMTD_WG_EXTERNAL_HOST: what routers and tunnels dial. Set, it wins over the admin page (§3.3).</summary>
    public string? ExternalHost { get; set; }

    /// <summary>PNMTD_WG_EXTERNAL_PORT: the port an upstream router forwards. Set, it wins; default: <see cref="PublishedPort"/>.</summary>
    public int? ExternalPort { get; set; }

    /// <summary>PNMTD_WG_PORT: the host port mapped to the gateway's <see cref="ListenPort"/>.</summary>
    public int PublishedPort { get; set; } = 51820;

    /// <summary>Inside the gateway's container.</summary>
    public int ListenPort { get; set; } = 51820;

    /// <summary>The gateway's SOCKS5 endpoint, for PNMTD's SSH connections into the tunnel (§3.4).</summary>
    public string Gateway { get; set; } = "pnmtd-wg:1080";

    /// <summary>No heartbeat for this long: the gateway counts as down, and tunnel routers write no events.</summary>
    public int GatewayDownSeconds { get; set; } = 60;

    /// <summary>The gateway's interface.</summary>
    public string Interface { get; set; } = "wg0";

    /// <summary>The gateway's SOCKS5 port (inside its container).</summary>
    public int SocksPort { get; set; } = 1080;
}
