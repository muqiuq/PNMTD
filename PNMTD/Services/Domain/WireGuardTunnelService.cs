using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.WireGuard;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.WireGuard;

namespace PNMTD.Services.Domain;

/// <summary>
/// Custom tunnels (docs/mikrotik-plan.md §3.5): created by an Admin, optionally for a PNMT user who may then
/// download the configuration, reaching the management ports of the routers they may reach. Every change
/// and every download is audit-logged, and the gateway resyncs at once.
/// </summary>
public sealed class WireGuardTunnelService(PnmtdDbContext db, WireGuardNetwork network, MikrotikKeyStore keys, IHttpContextAccessor accessor)
{
    public const int MaxName = 60;

    public async Task<IReadOnlyList<WireGuardTunnelItem>> ListAsync(Guid? ownerUserId, CancellationToken ct)
    {
        var n = await network.EnsureAsync(db, ct);
        var peers = await db.WgPeers.AsNoTracking()
            .Where(p => p.Kind == WgPeerKind.Custom && (ownerUserId == null || p.OwnerUserId == ownerUserId))
            .OrderBy(p => p.Name).ToListAsync(ct);
        var ids = peers.Select(p => p.Id).ToList();
        var status = await db.WgPeerStatus.AsNoTracking().Where(s => ids.Contains(s.PeerId)).ToDictionaryAsync(s => s.PeerId, ct);
        var access = (await db.WgPeerAccess.AsNoTracking().Where(a => ids.Contains(a.PeerId)).ToListAsync(ct)).ToLookup(a => a.PeerId, a => a.RouterSensorId);
        var sensorIds = access.SelectMany(g => g).Distinct().ToList();
        var names = await db.Sensors.AsNoTracking().Where(s => sensorIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var owners = peers.Select(p => p.OwnerUserId).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, ct);
        var reach = await TunnelReach.TunnelsAsync(db, ct);
        var now = Clock.UtcNow;
        var stale = TimeSpan.FromSeconds(n.StaleHandshakeSeconds);
        return peers.Select(p =>
        {
            status.TryGetValue(p.Id, out var s);
            return new WireGuardTunnelItem(p.Id, p.Name, p.Notes,
                p.OwnerUserId is { } o && users.TryGetValue(o, out var u) ? new Ref(o, u) : null,
                p.Address, p.PublicKey, p.Enabled, Expired(p, now), UdpViews.NullableUtc(p.ExpiresAt), p.AccessAllRouters,
                access[p.Id].Where(names.ContainsKey).Select(id => new Ref(id, names[id])).ToList(),
                (reach.TryGetValue(p.Id, out var r) ? r : []).Select(x => new WireGuardTunnelTarget(x.SensorId, x.Name, x.Address)).ToList(),
                s?.LatestHandshake is { } h && now - h <= stale, UdpViews.NullableUtc(s?.LatestHandshake), s?.Endpoint, s?.RxBytes ?? 0, s?.TxBytes ?? 0,
                Instants.Utc(p.KeysCreatedAt), Instants.Utc(p.CreatedAt));
        }).ToList();
    }

    private static bool Expired(WgPeerEntity p, DateTime now) => p.ExpiresAt is { } e && e <= now;

    public async Task<WireGuardTunnelItem> CreateAsync(WireGuardTunnelRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, null, ct);
        var peer = await network.NewPeerAsync(db, WgPeerKind.Custom, request.Name!.Trim(), UdpViews.User(accessor).Id, ct);
        Apply(peer, request);
        SetAccess(peer.Id, request);
        UdpViews.Audit(db, accessor, AuthEvents.WireGuardTunnelCreated, $"tunnel {peer.Name} ({peer.Address}): {Describe(request)}");
        await db.SaveChangesAsync(ct);
        await WireGuardNetwork.NotifyAsync(db, ct);
        return await ItemAsync(peer.Id, ct);
    }

    public async Task<WireGuardTunnelItem> UpdateAsync(Guid id, WireGuardTunnelRequest request, CancellationToken ct)
    {
        var peer = await TunnelAsync(id, ct);
        await ValidateAsync(request, peer, ct);
        peer.Name = request.Name!.Trim();
        Apply(peer, request);
        await db.WgPeerAccess.Where(a => a.PeerId == id).ExecuteDeleteAsync(ct);
        SetAccess(id, request);
        UdpViews.Audit(db, accessor, AuthEvents.WireGuardTunnelChanged, $"tunnel {peer.Name} ({peer.Address}): {Describe(request)}");
        await db.SaveChangesAsync(ct);
        await WireGuardNetwork.NotifyAsync(db, ct);
        return await ItemAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var peer = await TunnelAsync(id, ct);
        UdpViews.Audit(db, accessor, AuthEvents.WireGuardTunnelDeleted, $"tunnel {peer.Name} ({peer.Address}) deleted");
        db.WgPeers.Remove(peer);
        await db.SaveChangesAsync(ct);
        await WireGuardNetwork.NotifyAsync(db, ct);
    }

    /// <summary>New keys: every downloaded file stops working (§3.2).</summary>
    public async Task<WireGuardTunnelItem> RegenerateAsync(Guid id, CancellationToken ct)
    {
        var peer = await TunnelAsync(id, ct);
        network.Regenerate(peer, UdpViews.User(accessor).Id);
        UdpViews.Audit(db, accessor, AuthEvents.WireGuardKeysRegenerated, $"tunnel {peer.Name}: keys regenerated");
        await db.SaveChangesAsync(ct);
        await WireGuardNetwork.NotifyAsync(db, ct);
        return await ItemAsync(id, ct);
    }

    /// <summary>
    /// The wg-quick file (§3.5). An Admin may download any tunnel; with <paramref name="ownerUserId"/> only that
    /// user's own, enabled and unexpired tunnels. Audit-logged: it holds a private key.
    /// </summary>
    public async Task<WireGuardTunnelConfig> ConfigAsync(Guid id, Guid? ownerUserId, CancellationToken ct)
    {
        var peer = await db.WgPeers.SingleOrDefaultAsync(p => p.Id == id && p.Kind == WgPeerKind.Custom
            && (ownerUserId == null || p.OwnerUserId == ownerUserId), ct) ?? throw ApiException.NotFound("Tunnel");
        if (ownerUserId is not null && (!peer.Enabled || Expired(peer, Clock.UtcNow)))
        {
            throw ApiException.Conflict("tunnel_inactive", "This tunnel is disabled or expired: ask an Admin.");
        }
        var settings = await network.SettingsAsync(db, ct);
        if (settings.Endpoint is null)
        {
            throw ApiException.Conflict("no_endpoint", "No external address is known for the WireGuard endpoint: an Admin sets it on the admin page.");
        }
        var privateKey = network.Unprotect(peer.PrivateKeyProtected) ?? throw ApiException.Conflict("key_lost",
            "The tunnel's key cannot be decrypted (the key ring is gone): regenerate its keys.");
        var installation = await keys.GetAsync(ct);
        UdpViews.Audit(db, accessor, AuthEvents.WireGuardTunnelDownloaded, $"tunnel {peer.Name} ({peer.Address}) downloaded");
        await db.SaveChangesAsync(ct);
        return new WireGuardTunnelConfig(WgQuickConfig.FileName(peer.Name),
            WgQuickConfig.Build(peer.Name, installation.Instance, privateKey, peer.Address, settings.Mtu, settings.GatewayPublicKey,
                settings.Endpoint, settings.Plan.ToString(), settings.KeepaliveSeconds),
            settings.ManagementPorts);
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private async Task<WgPeerEntity> TunnelAsync(Guid id, CancellationToken ct) =>
        await db.WgPeers.SingleOrDefaultAsync(p => p.Id == id && p.Kind == WgPeerKind.Custom, ct) ?? throw ApiException.NotFound("Tunnel");

    private async Task<WireGuardTunnelItem> ItemAsync(Guid id, CancellationToken ct) => (await ListAsync(null, ct)).Single(t => t.Id == id);

    private async Task ValidateAsync(WireGuardTunnelRequest request, WgPeerEntity? existing, CancellationToken ct)
    {
        var v = new ApiValidator();
        var name = request.Name?.Trim();
        v.Check(name is { Length: >= 1 and <= MaxName }, "name", $"The name must have 1 to {MaxName} characters.");
        if (name is not null)
        {
            v.Check(!await db.WgPeers.AnyAsync(p => p.Kind == WgPeerKind.Custom && p.Name == name && p.Id != (existing == null ? Guid.Empty : existing.Id), ct),
                "name", "Another tunnel has this name.");
        }
        v.Check(request.Notes is null || request.Notes.Length <= UdpNodeService.MaxNotes, "notes", $"At most {UdpNodeService.MaxNotes} characters.");
        if (request.OwnerUserId is { } owner)
        {
            v.Check(await db.Users.AnyAsync(u => u.Id == owner, ct), "ownerUserId", "No such user.");
        }
        if (!request.AccessAllRouters && request.RouterIds.Count > 0)
        {
            var ids = request.RouterIds.Distinct().ToList();
            var known = await db.Sensors.CountAsync(s => ids.Contains(s.Id) && s.Type == SensorType.MIKROTIK, ct);
            v.Check(known == ids.Count, "routerIds", "Only MikroTik routers.");
        }
        v.Check(request.ExpiresAt is null || existing is not null || request.ExpiresAt > Clock.UtcNow, "expiresAt", "In the future.");
        v.Check(network.Enabled, "name", "Custom tunnels need the WireGuard gateway (WireGuard__Enabled).");
        v.ThrowIfInvalid();
    }

    private static void Apply(WgPeerEntity peer, WireGuardTunnelRequest request)
    {
        peer.Notes = Text.Trimmed(request.Notes) is null ? null : request.Notes;
        peer.OwnerUserId = request.OwnerUserId;
        peer.AccessAllRouters = request.AccessAllRouters;
        peer.ExpiresAt = request.ExpiresAt is { } e ? Clock.AsUtc(e) : null;
        peer.Enabled = request.Enabled;
    }

    private void SetAccess(Guid peerId, WireGuardTunnelRequest request)
    {
        if (request.AccessAllRouters)
        {
            return;
        }
        foreach (var routerId in request.RouterIds.Distinct())
        {
            db.WgPeerAccess.Add(new WgPeerAccessEntity { PeerId = peerId, RouterSensorId = routerId });
        }
    }

    private static string Describe(WireGuardTunnelRequest r) =>
        $"{(r.Enabled ? "enabled" : "disabled")}, {(r.AccessAllRouters ? "all routers" : $"{r.RouterIds.Count} router(s)")}" +
        $"{(r.OwnerUserId is null ? "" : ", with an owner")}{(r.ExpiresAt is { } e ? $", expires {Clock.AsUtc(e):yyyy-MM-dd HH:mm} UTC" : "")}";
}
