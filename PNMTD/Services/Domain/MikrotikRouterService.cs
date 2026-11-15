using PNMTD.Auth.Rights;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.WireGuard;

namespace PNMTD.Services.Domain;

/// <summary>
/// MikroTik routers (docs/mikrotik-plan.md §9, §10): their settings as part of the sensor, the join
/// and removal scripts, the host key, "poll now", and the admin page's fleet and status.
/// </summary>
public sealed class MikrotikRouterService(
    PnmtdDbContext db,
    MikrotikKeyStore keys,
    RouterPollService polls,
    MonitoringReader reader,
    IOptionsMonitor<MikrotikOptions> options,
    WireGuardNetwork wireGuard,
    IHttpContextAccessor accessor,
    AdvisoryFeed advisories,
    Cookie.CookieSettings cookieSettings)
{
    /// <summary>Set when a change concerns the gateway's peers: <see cref="NotifyGatewayAsync"/> after saving.</summary>
    private bool peersChanged;
    public const int MaxBackupIntervalHours = 8760;

    private static readonly string[] SortFields = ["name", "host", "state", "version", "lastsuccess"];

    // --- as part of the sensor --------------------------------------------------------------------

    /// <summary>Validates <c>SensorRequest.mikrotik</c> into <paramref name="v"/>; <paramref name="existing"/> is the router on update.</summary>
    public void Validate(MikrotikRouterRequest? request, MikrotikRouterEntity? existing, ApiValidator v)
    {
        if (request is null)
        {
            v.Check(existing is not null, "mikrotik", "A MikroTik router needs its settings: the mode, and for Direct SSH the address.");
            return;
        }
        v.Check(Enum.IsDefined(request.Mode), "mikrotik.mode", "WireGuardSsh, WireGuardOnly or DirectSsh.");
        if (request.Mode.UsesWireGuard())
        {
            v.Check(wireGuard.Enabled, "mikrotik.mode",
                "The WireGuard modes need the WireGuard gateway (the compose profile wireguard, WireGuard__Enabled=true). Use Direct SSH without it.");
        }
        if (request.Mode == MikrotikMode.DirectSsh)
        {
            var address = request.Address?.Trim();
            v.Check(address is { Length: > 0 and <= 253 } && IsHost(address), "mikrotik.address",
                "A host name or an IPv4 or IPv6 address that PNMTD can reach.");
            v.Check(request.SourceAddress is null || string.IsNullOrWhiteSpace(request.SourceAddress) || IsAddressOrNetwork(request.SourceAddress.Trim()),
                "mikrotik.sourceAddress", "PNMTD's address as the router sees it (192.0.2.10), or a network (192.0.2.0/24); empty for any.");
            v.Check(!request.ManagementAccess, "mikrotik.managementAccess", "Management access needs a WireGuard mode: custom tunnels reach routers through the tunnel.");
        }
        v.Check(request.SshPort is >= 1 and <= 65535, "mikrotik.sshPort", "Between 1 and 65535.");
        v.Check(request.BackupIntervalHours is null or (>= 1 and <= MaxBackupIntervalHours), "mikrotik.backupIntervalHours",
            $"Off, or between 1 and {MaxBackupIntervalHours} hours (a year).");

        // The settings that widen what PNMT may do on the router are an Admin's (§3.5, §6.6, §8.4).
        var before = (existing?.ManagementAccess ?? false, existing?.IncludeSecrets ?? false, existing?.Diagnostics ?? false);
        if ((request.ManagementAccess, request.IncludeSecrets, request.Diagnostics) != before && !IsAdmin())
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.ForbiddenRole,
                "Only an Admin can change management access, secrets in exports or diagnostics.");
        }
        if (request.EncryptBackups is { } encrypt && encrypt != (existing?.EncryptBackups ?? false) && !IsAdmin())
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.ForbiddenRole, "Only an Admin can change whether backups are encrypted.");
        }
    }

    /// <summary>Creates the router of a new MIKROTIK sensor, or updates it (a missing request keeps an existing router as it is).</summary>
    public async Task ApplyAsync(SensorEntity sensor, MikrotikRouterRequest? request, CancellationToken ct)
    {
        var router = await db.MikrotikRouters.SingleOrDefaultAsync(r => r.SensorId == sensor.Id, ct);
        if (router is not null && request is null)
        {
            return;
        }
        var defaults = await db.MikrotikSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == MikrotikSettingsEntity.SingletonId, ct);
        if (request is null)
        {
            // A router created without settings starts with the installation's backup schedule (docs/mikrotik-backup-plan.md §2.2).
            request = new MikrotikRouterRequest { BackupIntervalHours = defaults is null ? 24 : defaults.DefaultBackupIntervalHours };
        }
        // Encryption (docs/mikrotik-backup-plan.md §3.3): left out, as it is, or for a new router the installation's default.
        var hasKey = defaults?.BackupKeyId is not null;
        var encrypt = request.EncryptBackups ?? (router is null ? hasKey && defaults!.DefaultEncryptBackups : router.EncryptBackups);
        if (encrypt && !hasKey)
        {
            throw ApiException.Invalid("mikrotik.encryptBackups", "Set up the backup key first (Admin → MikroTik & WireGuard → Backups).");
        }
        var now = Clock.UtcNow;
        if (router is null)
        {
            router = new MikrotikRouterEntity { SensorId = sensor.Id, Created = now, ScriptChangedAt = now, SetupState = MikrotikSetupState.AwaitingRouter };
            // The access watch (docs/cookie-access-plan.md §4.2): a new router takes the Access tab's "Watch new routers".
            router.WatchLogins = (await cookieSettings.GetAsync<Cookie.Access.AccessSettings>(ct)).WatchNewRouters;
            db.MikrotikRouters.Add(router);
        }
        else
        {
            if (router.ManagementAccess != request.ManagementAccess)
            {
                UdpViews.Audit(db, accessor, AuditEvents.MikrotikManagementAccessChanged, $"router {sensor.Name}: management access {(request.ManagementAccess ? "on" : "off")}");
            }
            if (router.IncludeSecrets != request.IncludeSecrets)
            {
                UdpViews.Audit(db, accessor, AuditEvents.MikrotikSecretsChanged, $"router {sensor.Name}: secrets in exports {(request.IncludeSecrets ? "on" : "off")}");
            }
            if (router.Diagnostics != request.Diagnostics)
            {
                UdpViews.Audit(db, accessor, AuditEvents.MikrotikDiagnosticsChanged, $"router {sensor.Name}: diagnostics {(request.Diagnostics ? "on" : "off")}");
            }
            if (router.EncryptBackups != encrypt)
            {
                UdpViews.Audit(db, accessor, AuditEvents.MikrotikBackupEncryptionChanged, $"router {sensor.Name}: backups encrypted {(encrypt ? "on" : "off")}");
            }
        }
        var address = Text.Trimmed(request.Address);
        var source = Text.Trimmed(request.SourceAddress);
        var scriptChanged = router.Mode != request.Mode || router.PnmtSourceAddress != source || router.IncludeSecrets != request.IncludeSecrets
            || router.Diagnostics != request.Diagnostics || router.ManagementAccess != request.ManagementAccess;
        if (router.Mode != request.Mode || router.Address != address || router.SshPort != request.SshPort)
        {
            // Another router may answer at the new place: its host key is pinned anew at the next login (§4.4).
            router.HostKeyType = null;
            router.HostKeyFingerprint = null;
            router.HostKeyPinnedAt = null;
            router.PendingHostKeyType = null;
            router.PendingHostKeyFingerprint = null;
            router.PendingHostKeySeenAt = null;
            router.LastPollAt = null;
            if (router.SetupState != MikrotikSetupState.AwaitingRouter)
            {
                router.SetupState = MikrotikSetupState.AwaitingRouter;
            }
        }
        router.Mode = request.Mode;
        await PeerAsync(sensor, router, ct);
        router.Address = request.Mode == MikrotikMode.DirectSsh ? address : null;
        router.SshPort = request.SshPort;
        router.PnmtSourceAddress = request.Mode == MikrotikMode.DirectSsh ? source : null;
        router.BackupIntervalHours = request.BackupIntervalHours;
        router.EncryptBackups = encrypt;
        router.ManagementAccess = request.ManagementAccess;
        router.IncludeSecrets = request.IncludeSecrets;
        router.Diagnostics = request.Diagnostics;
        if (scriptChanged)
        {
            router.ScriptChangedAt = now;
        }
    }

    /// <summary>
    /// The router's tunnel follows its mode (§3.2): a WireGuard mode gets a peer, with the next free address
    /// and new keys; Direct SSH has none. The peer carries the sensor's name.
    /// </summary>
    private async Task PeerAsync(SensorEntity sensor, MikrotikRouterEntity router, CancellationToken ct)
    {
        peersChanged = true;
        var peer = router.WgPeerId is { } id ? await db.WgPeers.SingleOrDefaultAsync(p => p.Id == id, ct) : null;
        if (router.Mode.UsesWireGuard())
        {
            if (peer is null)
            {
                peer = await wireGuard.NewPeerAsync(db, WgPeerKind.Router, sensor.Name, UdpViews.User(accessor).Id, ct);
                peer.SensorId = sensor.Id;
                router.WgPeerId = peer.Id;
            }
            peer.Name = sensor.Name;
        }
        else if (peer is not null)
        {
            db.WgPeers.Remove(peer);
            router.WgPeerId = null;
        }
    }

    /// <summary>The type changed away from MIKROTIK: the router row and its tunnel go.</summary>
    public async Task RemoveAsync(Guid sensorId, CancellationToken ct)
    {
        await db.MikrotikRouters.Where(r => r.SensorId == sensorId).ExecuteDeleteAsync(ct);
        await db.WgPeers.Where(p => p.SensorId == sensorId).ExecuteDeleteAsync(ct);
        peersChanged = true;
    }

    /// <summary>After a router was saved or deleted: the gateway resyncs at once (§3.4).</summary>
    public async Task NotifyGatewayAsync(CancellationToken ct, bool always = false)
    {
        if (wireGuard.Enabled && (peersChanged || always))
        {
            await WireGuardNetwork.NotifyAsync(db, ct);
        }
        peersChanged = false;
    }

    public async Task<MikrotikRouterDetail?> DetailAsync(Guid sensorId, CancellationToken ct)
    {
        var router = await db.MikrotikRouters.AsNoTracking().SingleOrDefaultAsync(r => r.SensorId == sensorId, ct);
        if (router is null)
        {
            return null;
        }
        var installation = await keys.GetAsync(ct);
        var latest = await LatestAsync(ct);
        var wg = await WireGuardAsync(router, ct);
        var backups = db.MikrotikBackups.AsNoTracking().Where(b => b.SensorId == sensorId);
        var count = await backups.CountAsync(ct);
        var newest = await backups.OrderByDescending(b => b.Number).Select(b => new MikrotikBackupEntity
        {
            Id = b.Id, Number = b.Number, Kind = b.Kind, Trigger = b.Trigger, CreatedAt = b.CreatedAt, CreatedByName = b.CreatedByName,
            RouterOsVersion = b.RouterOsVersion, Size = b.Size, LineCount = b.LineCount, LinesAdded = b.LinesAdded, LinesRemoved = b.LinesRemoved,
            IncludesSecrets = b.IncludesSecrets, Encrypted = b.Encrypted, EncryptedWith = b.EncryptedWith,
        }).FirstOrDefaultAsync(ct);
        return Detail(router, installation, latest, wg) with
        {
            LastExportAt = UdpViews.NullableUtc(router.LastExportAt),
            BackupFailures = router.BackupFailures,
            LastBackupError = router.LastBackupError,
            BackupCount = count,
            LatestBackup = newest is null ? null : MikrotikBackupService.Item(newest),
            EncryptBackups = router.EncryptBackups,
            UpdateMethod = router.UpdateMethod,
            UpdateCheckStorage = router.UpdateCheckStorage,
            LowStorage = LowStorage(Reading(router)),
            SecurityFix = (await SecurityFixesAsync(ct)).GetValueOrDefault(sensorId),
            Tunnel = wg is { Peer: { } peer } ? new MikrotikTunnel(peer.Address, peer.PublicKey, Instants.Utc(peer.KeysCreatedAt),
                wg.Status?.LatestHandshake is { } h && Clock.UtcNow - h <= TimeSpan.FromSeconds(wg.Settings.StaleHandshakeSeconds),
                UdpViews.NullableUtc(wg.Status?.LatestHandshake), wg.Status?.Endpoint, wg.Status?.RxBytes ?? 0, wg.Status?.TxBytes ?? 0,
                wg.Status?.EndpointChanges24h ?? 0, wg.Settings.GatewayUp(Clock.UtcNow, wireGuard.Options.GatewayDownSeconds)) : null,
        };
    }

    /// <summary>What a router in a WireGuard mode is compared with: the network in effect, its peer and the peer's status.</summary>
    internal sealed record WgContext(WgNetworkSettings Settings, WgPeerEntity? Peer, WgPeerStatusEntity? Status);

    private async Task<WgContext?> WireGuardAsync(MikrotikRouterEntity router, CancellationToken ct)
    {
        if (!router.Mode.UsesWireGuard() || !wireGuard.Enabled)
        {
            return null;
        }
        var settings = await wireGuard.SettingsAsync(db, ct);
        var peer = router.WgPeerId is { } id ? await db.WgPeers.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) : null;
        var status = peer is null ? null : await db.WgPeerStatus.AsNoTracking().SingleOrDefaultAsync(s => s.PeerId == peer.Id, ct);
        return new WgContext(settings, peer, status);
    }

    private MikrotikRouterDetail Detail(MikrotikRouterEntity r, MikrotikInstallation installation, IReadOnlyDictionary<string, LatestVersion> latest,
        WgContext? wg = null)
    {
        var reading = Reading(r);
        var latestVersion = r.Channel is { } ch && latest.TryGetValue(ch, out var l) ? l.Version : null;
        var differences = Differences(r, reading, installation, wg);
        var warnings = new List<string>();
        if (r.Mode == MikrotikMode.DirectSsh && r.PnmtSourceAddress is null)
        {
            warnings.Add("The user pnmt may log in from any address: enter PNMTD's address as the router sees it.");
        }
        if (r.PendingHostKeyFingerprint is not null)
        {
            warnings.Add("The router presented another host key. If the router was replaced or reinstalled, an Admin can accept the new one.");
        }
        if (wg?.Status is { EndpointChanges24h: >= 6 } flapping)
        {
            warnings.Add($"The tunnel's endpoint changed {flapping.EndpointChanges24h} times in 24 hours: two devices may share this router's key " +
                "(a copied join script). If so, regenerate the keys.");
        }
        if (wg is not null && wg.Settings.Endpoint is null)
        {
            warnings.Add("No external address is known for the WireGuard endpoint: set PNMTD_WG_EXTERNAL_HOST, PublicBaseUrl, or the admin page's network settings.");
        }
        return new MikrotikRouterDetail(r.Mode, r.SetupState, r.Address, r.SshPort, r.PnmtSourceAddress, r.BackupIntervalHours,
            r.ManagementAccess, r.IncludeSecrets, r.Diagnostics,
            r.HostKeyFingerprint is { } fp ? new MikrotikHostKey(r.HostKeyType ?? "", fp, UdpViews.NullableUtc(r.HostKeyPinnedAt)) : null,
            r.PendingHostKeyFingerprint is { } pfp ? new MikrotikHostKey(r.PendingHostKeyType ?? "", pfp, UdpViews.NullableUtc(r.PendingHostKeySeenAt)) : null,
            r.Identity, r.Model, r.BoardName, r.SerialNumber, r.Architecture, r.Version, r.Channel, latestVersion,
            UpdateAvailable(r.Version, latestVersion), r.FirmwareCurrent, r.FirmwareUpgrade, UdpViews.NullableUtc(r.BootedAt),
            reading is null ? null : new MikrotikResources(reading.Cpu, reading.CpuCount, reading.CpuLoad, reading.FreeMemory, reading.TotalMemory,
                reading.FreeStorage, reading.TotalStorage, reading.WriteSectorsTotal, reading.BadBlocks,
                reading.Uptime is { } u ? (long)u.TotalSeconds : null),
            UdpViews.NullableUtc(r.LastPollAt), UdpViews.NullableUtc(r.LastSuccessAt), UdpViews.NullableUtc(r.FailingSince), r.LastError, r.LastErrorCode,
            differences.Count > 0, differences, warnings);
    }

    /// <summary>The worst security fix each router is behind (docs/ai-operations-plan.md §10.4).</summary>
    private async Task<Dictionary<Guid, ReleaseSeverity?>> SecurityFixesAsync(CancellationToken ct)
    {
        var worst = new Dictionary<Guid, ReleaseSeverity?>();
        foreach (var behind in await advisories.BehindAsync(ct))
        {
            foreach (var (id, _) in behind.Routers)
            {
                if (worst.GetValueOrDefault(id) is not { } current || behind.Release.Severity > current)
                {
                    worst[id] = behind.Release.Severity;
                }
            }
        }
        return worst;
    }

    /// <summary>The releases with a security fix, newest first, and the routers behind each (§10.4).</summary>
    public async Task<IReadOnlyList<MikrotikReleaseItem>> ReleasesAsync(CancellationToken ct) =>
        (await advisories.BehindAsync(ct)).Take(20).Select(b => new MikrotikReleaseItem(b.Release.Version, b.Release.Channel,
            b.Release.PublishedAt is { } p ? Instants.Utc(p) : null, b.Release.Severity, b.Release.ZeroDay, b.Release.Exploited,
            b.Release.RemoteWithoutCredentials, b.Release.Cves, b.Release.Summary, b.Release.AssessedBy,
            b.Routers.Select(r => new Ref(r.SensorId, r.Name)).ToList(), AdvisoryFeed.Assess(b.Release.Changelog).Hits)).ToList();

    /// <summary>Below the update's storage check (docs/ai-operations-plan.md §12.2): the dialog suggests turning it off.</summary>
    private bool LowStorage(RouterReading? reading) =>
        reading?.FreeStorage is { } free && free < options.CurrentValue.Updates.MinimumFreeStorageBytes;

    private static RouterReading? Reading(MikrotikRouterEntity r)
    {
        if (string.IsNullOrEmpty(r.Reading))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<RouterReading>(r.Reading, VersionCatalog.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the router has that its current join script would change (§3.3): the group's policies, PNMTD's
    /// current key, and in the WireGuard modes the tunnel's settings, as the poll read them. A router in
    /// WireGuard only mode has no login: its script is outdated when the settings changed after it was
    /// last shown. Empty until there is something to compare.
    /// </summary>
    internal static IReadOnlyList<string> Differences(MikrotikRouterEntity r, RouterReading? reading, MikrotikInstallation installation,
        WgContext? wg = null)
    {
        var list = new List<string>();
        if (r.Mode == MikrotikMode.WireGuardOnly && wg?.Peer is { } onlyPeer && r.ScriptGeneratedAt is { } shown)
        {
            var changed = new[] { wg.Settings.ScriptSettingsChangedAt, r.ScriptChangedAt, onlyPeer.KeysCreatedAt }.Max();
            if (changed > shown)
            {
                list.Add("The settings or keys changed after the join script was last shown: paste the new one.");
            }
            return list;
        }
        if (reading is null || !r.Mode.UsesSsh())
        {
            return [];
        }
        if (wg?.Peer is { } peer)
        {
            var t = reading.WireGuard;
            var s = wg.Settings;
            if (t is null)
            {
                list.Add("The router has no PNMT tunnel interface.");
            }
            else
            {
                if (t.PublicKey != peer.PublicKey) list.Add("The router's tunnel key is not its current one: the keys were regenerated.");
                if (t.PeerPublicKey != s.GatewayPublicKey) list.Add("The router's peer is not this gateway.");
                if (s.ExternalHost is not null && !string.Equals(t.EndpointAddress, s.ExternalHost, StringComparison.OrdinalIgnoreCase))
                    list.Add($"The router dials {t.EndpointAddress}; the endpoint is now {s.ExternalHost}.");
                if (t.EndpointPort != s.ExternalPort) list.Add($"The router dials port {t.EndpointPort}; the endpoint's port is now {s.ExternalPort}.");
                if (t.Keepalive is { } k && (int)k.TotalSeconds != s.KeepaliveSeconds) list.Add($"The keepalive is {(int)k.TotalSeconds} s; it is now {s.KeepaliveSeconds} s.");
                if (t.Mtu != s.Mtu) list.Add($"The tunnel's MTU is {t.Mtu}; it is now {s.Mtu}.");
            }
            var management = reading.FirewallRules.Any(f => f.SourceAddress == s.Plan.AccessAddress.ToString());
            if (management != r.ManagementAccess)
            {
                list.Add(r.ManagementAccess ? "Management access is on, but the router lacks its firewall rule." : "The router still accepts custom tunnels on its management ports.");
            }
        }
        var policies = reading.GroupPolicies;
        if (r.IncludeSecrets != policies.Contains("sensitive"))
        {
            list.Add(r.IncludeSecrets ? "Secrets in exports are on, but the group pnmt-read lacks the policy sensitive." : "The group pnmt-read still has the policy sensitive.");
        }
        if (r.Diagnostics != policies.Contains("test"))
        {
            list.Add(r.Diagnostics ? "Diagnostics are on, but the group pnmt-read lacks the policy test." : "The group pnmt-read still has the policy test.");
        }
        var current = installation.Current;
        if (reading.SshKeyOwners.Count > 0 && !reading.SshKeyOwners.Contains(current.Comment))
        {
            list.Add("The router does not have PNMTD's current SSH key.");
        }
        return list;
    }

    internal static bool UpdateAvailable(string? installed, string? latest) =>
        RouterOsVersion.TryParse(installed, out var i) && RouterOsVersion.TryParse(latest, out var l) && l > i;

    private async Task<IReadOnlyDictionary<string, LatestVersion>> LatestAsync(CancellationToken ct)
    {
        var settings = await db.MikrotikSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == MikrotikSettingsEntity.SingletonId, ct);
        return VersionCatalog.Read(settings?.LatestVersions);
    }

    // --- the router's own endpoints ------------------------------------------------------------------

    /// <summary>The join script (§4.3) or the removal script (§4.6). Viewing a join script is audit-logged: in the WireGuard modes it holds a private key.</summary>
    public async Task<MikrotikScript> ScriptAsync(Guid sensorId, bool removal, CancellationToken ct)
    {
        var router = await db.MikrotikRouters.Include(r => r.Sensor).SingleOrDefaultAsync(r => r.SensorId == sensorId, ct)
            ?? throw ApiException.NotFound("MikroTik router");
        var installation = await keys.GetAsync(ct);
        var now = Clock.UtcNow;
        var name = router.Sensor.Name;
        if (removal)
        {
            return new MikrotikScript(JoinScript.Removal(name, installation.Instance, installation.Comment, now),
                "pnmt-remove.rsc", router.Mode, now, []);
        }
        JoinScriptTunnel? tunnel = null;
        var notes = new List<string>();
        if (router.Mode.UsesWireGuard())
        {
            if (!wireGuard.Enabled)
            {
                throw ApiException.Conflict("wireguard_unavailable", "The WireGuard gateway is off (WireGuard__Enabled): use Direct SSH, or turn it on.");
            }
            var network = await wireGuard.SettingsAsync(db, ct);
            var peer = router.WgPeerId is { } peerId ? await db.WgPeers.SingleOrDefaultAsync(p => p.Id == peerId, ct) : null;
            if (peer is null)
            {
                throw ApiException.Conflict("no_tunnel", "The router has no tunnel yet: save its settings again.");
            }
            if (network.ExternalHost is null)
            {
                throw ApiException.Conflict("no_endpoint",
                    "No external address is known for the WireGuard endpoint: set PNMTD_WG_EXTERNAL_HOST (or PublicBaseUrl), or the admin page's network settings.");
            }
            var privateKey = wireGuard.Unprotect(peer.PrivateKeyProtected) ?? throw ApiException.Conflict("key_lost",
                "The router's tunnel key cannot be decrypted (the key ring is gone): regenerate the keys.");
            tunnel = new JoinScriptTunnel(privateKey, network.GatewayPublicKey, network.ExternalHost, network.ExternalPort, peer.Address,
                network.Plan.ServerBlock, network.Plan.ServerAddress.ToString(), network.Plan.AccessAddress.ToString(), network.KeepaliveSeconds, network.Mtu);
            if (!network.GatewayUp(now, wireGuard.Options.GatewayDownSeconds))
            {
                notes.Add("The WireGuard gateway is down: the router can join, but its tunnel comes up only when the gateway runs.");
            }
        }
        var settings = new JoinScriptSettings
        {
            RouterName = name,
            Instance = installation.Instance,
            Tag = installation.Comment,
            Mode = router.Mode,
            GeneratedAt = now,
            // The newest key only: a script pasted after a rotation removes the router's old key (§4.2).
            SshPublicKeys = [installation.Current.PublicKey],
            Tunnel = tunnel,
            // Through the tunnel PNMTD's connections come from the server address only (§4.2).
            UserAddress = tunnel is not null ? $"{tunnel.ServerAddress}/32" : SourceNetwork(router.PnmtSourceAddress),
            SshPort = router.SshPort,
            IncludeSecrets = router.IncludeSecrets,
            Diagnostics = router.Diagnostics,
            ManagementAccess = router.ManagementAccess,
        };
        if (settings.UserAddress is null)
        {
            notes.Add("Without PNMTD's source address the user pnmt may log in from anywhere, with PNMTD's key only.");
        }
        UdpViews.Audit(db, accessor, AuditEvents.MikrotikJoinScriptViewed, $"router {name}: join script ({router.Mode.Label()})");
        router.ScriptGeneratedAt = now;
        await db.SaveChangesAsync(ct);
        return new MikrotikScript(JoinScript.Build(settings), "pnmt-join.rsc", router.Mode, now, notes);
    }

    /// <summary>"192.0.2.10" → "192.0.2.10/32"; a network stays as it is.</summary>
    internal static string? SourceNetwork(string? source)
    {
        if (source is null)
        {
            return null;
        }
        if (source.Contains('/'))
        {
            return source;
        }
        return IPAddress.TryParse(source, out var ip) ? $"{ip}/{(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32)}" : source;
    }

    /// <summary>Takes the host key the router presented as its pinned key (§4.4), e.g. after the router was reinstalled.</summary>
    public async Task<MikrotikRouterDetail> AcceptHostKeyAsync(Guid sensorId, CancellationToken ct)
    {
        var router = await db.MikrotikRouters.Include(r => r.Sensor).SingleOrDefaultAsync(r => r.SensorId == sensorId, ct)
            ?? throw ApiException.NotFound("MikroTik router");
        if (router.PendingHostKeyFingerprint is null)
        {
            throw ApiException.Conflict("no_pending_host_key", "The router has not presented another host key.");
        }
        UdpViews.Audit(db, accessor, AuditEvents.MikrotikHostKeyAccepted,
            $"router {router.Sensor.Name}: host key {router.PendingHostKeyType} SHA256:{router.PendingHostKeyFingerprint} accepted (was SHA256:{router.HostKeyFingerprint ?? "none"})");
        router.HostKeyType = router.PendingHostKeyType;
        router.HostKeyFingerprint = router.PendingHostKeyFingerprint;
        router.HostKeyPinnedAt = Clock.UtcNow;
        router.PendingHostKeyType = null;
        router.PendingHostKeyFingerprint = null;
        router.PendingHostKeySeenAt = null;
        router.LastPollAt = null;
        await db.SaveChangesAsync(ct);
        return (await DetailAsync(sensorId, ct))!;
    }

    /// <summary>New tunnel keys for the router (§3.2): every copy of its join script stops working, and so does the router until it gets the new one.</summary>
    public async Task<MikrotikRouterDetail> RegenerateKeysAsync(Guid sensorId, CancellationToken ct)
    {
        var router = await db.MikrotikRouters.Include(r => r.Sensor).SingleOrDefaultAsync(r => r.SensorId == sensorId, ct)
            ?? throw ApiException.NotFound("MikroTik router");
        var peer = router.WgPeerId is { } id ? await db.WgPeers.SingleOrDefaultAsync(p => p.Id == id, ct) : null;
        if (peer is null)
        {
            throw ApiException.Conflict("no_tunnel", "Only a router in a WireGuard mode has tunnel keys.");
        }
        wireGuard.Regenerate(peer, UdpViews.User(accessor).Id);
        UdpViews.Audit(db, accessor, AuditEvents.WireGuardKeysRegenerated, $"router {router.Sensor.Name}: tunnel keys regenerated");
        await db.SaveChangesAsync(ct);
        await WireGuardNetwork.NotifyAsync(db, ct);
        return (await DetailAsync(sensorId, ct))!;
    }

    public async Task EnsureRouterAsync(Guid sensorId, CancellationToken ct)
    {
        if (!await db.MikrotikRouters.AnyAsync(r => r.SensorId == sensorId, ct))
        {
            throw ApiException.NotFound("MikroTik router");
        }
    }

    /// <summary>Polls now and waits for it (§10: "poll now").</summary>
    public async Task<MikrotikRouterDetail> PollNowAsync(Guid sensorId, CancellationToken ct)
    {
        if (!await db.MikrotikRouters.AnyAsync(r => r.SensorId == sensorId, ct))
        {
            throw ApiException.NotFound("MikroTik router");
        }
        await polls.PollAsync(sensorId, wait: true, ct);
        return (await DetailAsync(sensorId, ct))!;
    }

    // --- the admin page ---------------------------------------------------------------------------

    public async Task<Paged<MikrotikFleetItem>> FleetAsync(MikrotikFleetQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "name", SortFields);
        var routers = await db.MikrotikRouters.AsNoTracking().ToListAsync(ct);
        var snapshot = await reader.SnapshotAsync(s => s.Type == SensorType.MIKROTIK, ct);
        var installation = await keys.GetAsync(ct);
        var latest = await LatestAsync(ct);
        var fixes = await SecurityFixesAsync(ct);
        var items = routers
            .Where(r => snapshot.Sensors.ContainsKey(r.SensorId))
            .Select(r =>
            {
                var s = snapshot.Sensors[r.SensorId];
                var latestVersion = r.Channel is { } ch && latest.TryGetValue(ch, out var l) ? l.Version : null;
                return new MikrotikFleetItem(r.SensorId, s.Info.Name, new Ref(s.Info.HostId, s.Info.HostName), s.Info.Enabled, r.Mode, r.SetupState,
                    s.State, s.LastCode, s.LastMessage, r.Address, r.Identity, r.Model ?? r.BoardName, r.SerialNumber, r.Version, r.Channel, latestVersion,
                    UpdateAvailable(r.Version, latestVersion), r.FirmwareCurrent, r.FirmwareUpgrade, UdpViews.NullableUtc(r.BootedAt),
                    UdpViews.NullableUtc(r.LastSuccessAt), r.LastError, Differences(r, Reading(r), installation).Count > 0, r.BackupIntervalHours,
                    r.EncryptBackups, Reading(r)?.FreeStorage, r.UpdateMethod, r.UpdateCheckStorage, LowStorage(Reading(r)), fixes.GetValueOrDefault(r.SensorId));
            })
            .Where(i => query.Mode is not { } m || i.Mode == m)
            .Where(i => query.SetupState is not { } st || i.SetupState == st)
            .Where(i => query.State is not { } state || i.State == state)
            .Where(i => query.UpdateAvailable is not { } u || i.UpdateAvailable == u)
            .Where(i => query.ScriptOutdated is not { } o || i.ScriptOutdated == o)
            .Where(i => query.Channel is not { } c || string.Equals(i.Channel, c, StringComparison.OrdinalIgnoreCase))
            .Where(i => Text.Contains(i.Name, query.Search) || Text.Contains(i.Host.Name, query.Search) || Text.Contains(i.Identity, query.Search)
                || Text.Contains(i.SerialNumber, query.Search) || Text.Contains(i.Address, query.Search));
        IOrderedEnumerable<MikrotikFleetItem> sorted = field switch
        {
            "host" => items.OrderBy(i => i.Host.Name, StringComparer.OrdinalIgnoreCase),
            "state" => items.OrderBy(i => SensorService.StateOrder(i.State)),
            "version" => items.OrderBy(i => RouterOsVersion.TryParse(i.Version, out var v) ? v : null),
            "lastsuccess" => items.OrderBy(i => i.LastSuccessAt ?? DateTime.MinValue),
            _ => items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = (descending ? sorted.Reverse() : sorted.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return Paging.Page(list, query);
    }

    public async Task<MikrotikStatus> StatusAsync(CancellationToken ct)
    {
        var installation = await keys.GetAsync(ct);
        var settings = await db.MikrotikSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == MikrotikSettingsEntity.SingletonId, ct);
        var latest = VersionCatalog.Read(settings?.LatestVersions);
        var routers = await db.MikrotikRouters.AsNoTracking().ToListAsync(ct);
        var o = options.CurrentValue;
        return new MikrotikStatus(o.Enabled, installation.Tag, installation.Instance,
            (await db.MikrotikSshKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct))
                .Select(k => new MikrotikSshPublicKey(k.PublicKey, k.Comment, SshKeys.Fingerprint(k.PublicKey), Instants.Utc(k.CreatedAt), UdpViews.NullableUtc(k.RetiredAt)))
                .ToList(),
            latest.OrderBy(kv => Array.IndexOf(o.VersionCheck.Channels, kv.Key)).Select(kv => new MikrotikChannelVersion(kv.Key, kv.Value.Version, kv.Value.ReleasedAt)).ToList(),
            UdpViews.NullableUtc(settings?.VersionsCheckedAt), settings?.VersionCheckError, o.Enabled && o.VersionCheck.Enabled,
            routers.Count,
            routers.GroupBy(r => r.SetupState).OrderBy(g => g.Key).Select(g => new MikrotikCount(g.Key.ToString(), g.Count())).ToList(),
            routers.Where(r => r.Version is not null).GroupBy(r => r.Version!)
                .OrderByDescending(g => RouterOsVersion.TryParse(g.Key, out var v) ? v : null).Select(g => new MikrotikCount(g.Key, g.Count())).ToList(),
            routers.Count(r => r.Channel is { } ch && latest.TryGetValue(ch, out var l) && UpdateAvailable(r.Version, l.Version)),
            routers.Count(r => Differences(r, Reading(r), installation).Count > 0));
    }

    // --- PNMTD's SSH key (§4.2) ---------------------------------------------------------------------

    /// <summary>The keys, newest first, and the routers that would be cut off if the older keys were retired now.</summary>
    public async Task<MikrotikSshKeyStatus> SshKeysAsync(CancellationToken ct)
    {
        var installation = await keys.GetAsync(ct);
        var all = await db.MikrotikSshKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        var waiting = new List<MikrotikKeyWaiting>();
        if (installation.Keys.Count > 1)
        {
            var current = installation.Current.Comment;
            var routers = await db.MikrotikRouters.AsNoTracking().Include(r => r.Sensor)
                .Where(r => r.Mode != MikrotikMode.WireGuardOnly).ToListAsync(ct);
            foreach (var r in routers.OrderBy(r => r.Sensor.Name, StringComparer.OrdinalIgnoreCase))
            {
                var reading = Reading(r);
                if (reading is null)
                {
                    waiting.Add(new MikrotikKeyWaiting(r.SensorId, r.Sensor.Name, "PNMTD has not read its keys yet."));
                }
                else if (!reading.SshKeyOwners.Contains(current))
                {
                    waiting.Add(new MikrotikKeyWaiting(r.SensorId, r.Sensor.Name, "It accepts only an older key: paste its new join script."));
                }
            }
        }
        return new MikrotikSshKeyStatus(all.Select(k => new MikrotikSshPublicKey(k.PublicKey, k.Comment, SshKeys.Fingerprint(k.PublicKey),
            Instants.Utc(k.CreatedAt), UdpViews.NullableUtc(k.RetiredAt))).ToList(), installation.Current.Comment, waiting);
    }

    /// <summary>A new key pair; the older keys stay offered until they are retired. Audit-logged.</summary>
    public async Task<MikrotikSshKeyStatus> RotateSshKeyAsync(CancellationToken ct)
    {
        var key = await keys.RotateAsync(UdpViews.User(accessor).Id, ct);
        UdpViews.Audit(db, accessor, AuditEvents.MikrotikSshKeyRotated, $"new SSH key {SshKeys.Fingerprint(key.PublicKey)} ({key.Comment})");
        await db.SaveChangesAsync(ct);
        return await SshKeysAsync(ct);
    }

    /// <summary>
    /// Stops offering the older keys. Refused while a router accepts only an older one, unless
    /// <paramref name="force"/>: such a router then fails with 441 until its new script is pasted. Audit-logged.
    /// </summary>
    public async Task<MikrotikSshKeyStatus> RetireSshKeysAsync(bool force, CancellationToken ct)
    {
        var status = await SshKeysAsync(ct);
        if (status.Waiting.Count > 0 && !force)
        {
            throw ApiException.Conflict("routers_waiting",
                $"{status.Waiting.Count} router(s) may still need an older key: {string.Join(", ", status.Waiting.Take(10).Select(w => w.Name))}.");
        }
        var retired = await keys.RetireOlderAsync(ct);
        if (retired > 0)
        {
            UdpViews.Audit(db, accessor, AuditEvents.MikrotikSshKeyRetired,
                $"{retired} older SSH key(s) retired{(status.Waiting.Count > 0 ? $", {status.Waiting.Count} router(s) still waiting" : "")}");
            await db.SaveChangesAsync(ct);
        }
        return await SshKeysAsync(ct);
    }

    // --- helpers ------------------------------------------------------------------------------------

    /// <summary>What only MikroTik & WireGuard's Manage may do (docs/audit-redmine-zabbix-plan.md §5.2); the CLI may.</summary>
    private bool IsAdmin() => accessor.HttpContext is not { } http || http.User.Has(Area.Mikrotik, Level.Manage);

    internal static bool IsHost(string text) =>
        !text.Any(char.IsWhiteSpace) && (IPAddress.TryParse(text.Trim('[', ']'), out _) || Uri.CheckHostName(text) == UriHostNameType.Dns);

    internal static bool IsAddressOrNetwork(string text) =>
        IPAddress.TryParse(text, out _) || IPNetwork.TryParse(text, out _);
}
