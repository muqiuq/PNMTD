using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.WireGuard;
using PNMTD.Models.Db;
using PNMTD.WireGuard;

namespace PNMTD.Services.Domain;

/// <summary>
/// The tunnel network on the admin page (docs/mikrotik-plan.md §3.3, §10, §11.2): the gateway's state, the
/// settings with their sources, a change of them, and every peer with its tunnel.
/// </summary>
public sealed class WireGuardService(PnmtdDbContext db, WireGuardNetwork network, IHttpContextAccessor accessor)
{
    public async Task<WireGuardStatus> StatusAsync(CancellationToken ct)
    {
        var n = await network.EnsureAsync(db, ct);
        var s = network.Settings(n);
        var now = Clock.UtcNow;
        var up = s.GatewayUp(now, network.Options.GatewayDownSeconds);
        var stale = TimeSpan.FromSeconds(s.StaleHandshakeSeconds);
        var peers = await db.WgPeers.CountAsync(ct);
        var handshakes = await db.WgPeerStatus.Select(x => x.LatestHandshake).ToListAsync(ct);
        var warnings = new List<string>();
        if (!network.Enabled)
        {
            warnings.Add("WireGuard is off: set WireGuard__Enabled=true and COMPOSE_PROFILES=wireguard in .env (deploy/README.md).");
        }
        else if (s.GatewaySeenAt is null)
        {
            warnings.Add("The gateway has never reported: is the service pnmtd-wg running (docker compose ps)? It needs NET_ADMIN and the WireGuard kernel module.");
        }
        else if (!up)
        {
            warnings.Add($"The gateway has not reported since {Clock.ToLocal(s.GatewaySeenAt.Value):yyyy-MM-dd HH:mm:ss}: tunnel routers write no events until it is back.");
        }
        if (s.ExternalHost is null)
        {
            warnings.Add("No external address is known: set PNMTD_WG_EXTERNAL_HOST (or PublicBaseUrl), or the external host below. Join scripts need it.");
        }
        return new WireGuardStatus(network.Enabled, up, UdpViews.NullableUtc(s.GatewaySeenAt), s.GatewayVersion, s.GatewayError, s.Endpoint, s.ExternalHost,
            s.ExternalHostSource, s.ExternalPort, s.ExternalPortSource, s.PublishedPort, s.Plan.ToString(), s.Plan.ServerAddress.ToString(),
            s.Plan.AccessAddress.ToString(), s.GatewayPublicKey, peers, handshakes.Count(h => h is { } x && now - x <= stale), warnings);
    }

    public async Task<WireGuardNetworkView> NetworkAsync(CancellationToken ct) => View(await network.EnsureAsync(db, ct));

    private WireGuardNetworkView View(WgNetworkEntity n)
    {
        var s = network.Settings(n);
        return new WireGuardNetworkView(n.Prefix, n.ExternalHost, s.ExternalHost, s.ExternalHostSource, s.ExternalHostSource == WgSettingSource.Environment,
            n.ExternalPort, s.ExternalPort, s.ExternalPortSource, s.ExternalPortSource == WgSettingSource.Environment, s.PublishedPort, s.ListenPort,
            n.KeepaliveSeconds, n.Mtu, n.StaleHandshakeSeconds, n.ManagementPorts, Instants.Utc(n.ChangedAt));
    }

    /// <summary>
    /// Saves the settings (§3.3). What the join scripts carry (endpoint, prefix, keepalive, MTU, management
    /// ports) applies to new scripts; the answer names the routers and tunnels that need theirs again.
    /// </summary>
    public async Task<WireGuardNetworkSaved> UpdateNetworkAsync(WireGuardNetworkRequest request, CancellationToken ct)
    {
        var n = await network.EnsureAsync(db, ct);
        var before = network.Settings(n);
        var v = new ApiValidator();
        AddressPlan? plan = null;
        if (!AddressPlan.TryParse(request.Prefix, out var parsed, out var prefixError))
        {
            v.Check(false, "prefix", prefixError!);
        }
        else
        {
            plan = parsed;
        }
        var prefixChanged = plan is not null && plan.ToString() != before.Plan.ToString();
        if (prefixChanged)
        {
            v.Check(string.Equals(request.ConfirmPrefix?.Trim(), plan!.ToString(), StringComparison.Ordinal), "confirmPrefix",
                $"A new prefix re-addresses every router and tunnel: type {plan} to confirm.");
        }
        var host = Text.Trimmed(request.ExternalHost);
        if (before.ExternalHostSource == WgSettingSource.Environment)
        {
            v.Check(host is null || host == Text.Trimmed(n.ExternalHost), "externalHost", "Set by PNMTD_WG_EXTERNAL_HOST in .env: change it there.");
        }
        else
        {
            v.Check(host is null || (host.Length <= 253 && MikrotikRouterService.IsHost(host)), "externalHost",
                "A host name (recommended) or an address that routers can reach from outside.");
        }
        if (before.ExternalPortSource == WgSettingSource.Environment)
        {
            v.Check(request.ExternalPort is null || request.ExternalPort == n.ExternalPort, "externalPort", "Set by PNMTD_WG_EXTERNAL_PORT in .env: change it there.");
        }
        else
        {
            v.Check(request.ExternalPort is null or (>= 1 and <= 65535), "externalPort", "Between 1 and 65535, or empty for the published port.");
        }
        v.Check(request.KeepaliveSeconds is >= 10 and <= 120, "keepaliveSeconds", "Between 10 and 120 seconds.");
        v.Check(request.Mtu is >= 1280 and <= 1500, "mtu", "Between 1280 and 1500.");
        v.Check(request.StaleHandshakeSeconds >= request.KeepaliveSeconds * 3 && request.StaleHandshakeSeconds <= 3600, "staleHandshakeSeconds",
            "At least three keepalives, at most 3600 seconds.");
        var ports = request.ManagementPorts.Distinct().Order().ToList();
        v.Check(ports.Count <= 16 && ports.All(p => p is >= 1 and <= 65535), "managementPorts", "At most 16 ports from 1 to 65535.");

        List<WgPeerEntity> peers = [];
        if (prefixChanged && plan is not null)
        {
            peers = await db.WgPeers.ToListAsync(ct);
            var unplaced = peers.Where(p => !IPAddress.TryParse(p.Address, out var a) || before.Plan.Move(a, plan) is null).Select(p => p.Name).ToList();
            v.Check(unplaced.Count == 0, "prefix", $"{plan} is too small for {unplaced.Count} of the {peers.Count} tunnels: {string.Join(", ", unplaced.Take(5))}.");
        }
        v.ThrowIfInvalid();

        if (prefixChanged)
        {
            // The same place in the new prefix, so every router keeps its offset (§3.3).
            foreach (var peer in peers)
            {
                peer.Address = before.Plan.Move(IPAddress.Parse(peer.Address), plan!)!.ToString();
            }
            n.Prefix = plan!.ToString();
        }
        if (before.ExternalHostSource != WgSettingSource.Environment)
        {
            n.ExternalHost = host;
        }
        if (before.ExternalPortSource != WgSettingSource.Environment)
        {
            n.ExternalPort = request.ExternalPort;
        }
        n.KeepaliveSeconds = request.KeepaliveSeconds;
        n.Mtu = request.Mtu;
        n.StaleHandshakeSeconds = request.StaleHandshakeSeconds;
        n.ManagementPorts = [.. ports];
        var after = network.Settings(n);
        var changes = new List<string>();
        if (prefixChanged) changes.Add($"prefix {before.Plan} → {after.Plan}");
        if (before.Endpoint != after.Endpoint) changes.Add($"endpoint {before.Endpoint ?? "none"} → {after.Endpoint ?? "none"}");
        if (before.KeepaliveSeconds != after.KeepaliveSeconds) changes.Add($"keepalive {before.KeepaliveSeconds} → {after.KeepaliveSeconds} s");
        if (before.Mtu != after.Mtu) changes.Add($"MTU {before.Mtu} → {after.Mtu}");
        if (!before.ManagementPorts.SequenceEqual(after.ManagementPorts)) changes.Add($"management ports {string.Join(",", after.ManagementPorts)}");
        if (before.StaleHandshakeSeconds != after.StaleHandshakeSeconds) changes.Add($"stale handshake {after.StaleHandshakeSeconds} s");
        var scriptsChange = prefixChanged || before.Endpoint != after.Endpoint || before.KeepaliveSeconds != after.KeepaliveSeconds
            || before.Mtu != after.Mtu || !before.ManagementPorts.SequenceEqual(after.ManagementPorts);
        var now = Clock.UtcNow;
        if (scriptsChange)
        {
            n.ScriptSettingsChangedAt = now;
        }
        n.ChangedAt = now;
        n.ChangedBy = UdpViews.User(accessor).Id;
        if (changes.Count > 0)
        {
            UdpViews.Audit(db, accessor, AuditEvents.WireGuardNetworkChanged, string.Join("; ", changes));
        }
        await db.SaveChangesAsync(ct);
        await WireGuardNetwork.NotifyAsync(db, ct);
        var affected = scriptsChange ? await db.WgPeers.OrderBy(p => p.Name).Select(p => p.Name).ToListAsync(ct) : [];
        return new WireGuardNetworkSaved(View(n), affected);
    }

    public async Task<IReadOnlyList<WireGuardPeerItem>> PeersAsync(CancellationToken ct)
    {
        var n = await network.EnsureAsync(db, ct);
        var stale = TimeSpan.FromSeconds(n.StaleHandshakeSeconds);
        var now = Clock.UtcNow;
        var peers = await db.WgPeers.AsNoTracking().OrderBy(p => p.Address).ToListAsync(ct);
        var status = await db.WgPeerStatus.AsNoTracking().ToDictionaryAsync(s => s.PeerId, ct);
        return peers.Select(p =>
        {
            status.TryGetValue(p.Id, out var s);
            return new WireGuardPeerItem(p.Id, p.Kind, p.Name, p.SensorId, p.Address, p.PublicKey, p.Enabled,
                s?.LatestHandshake is { } h && now - h <= stale, UdpViews.NullableUtc(s?.LatestHandshake), s?.Endpoint, s?.RxBytes ?? 0, s?.TxBytes ?? 0,
                s?.EndpointChanges24h ?? 0, UdpViews.NullableUtc(p.ExpiresAt));
        }).ToList();
    }
}
