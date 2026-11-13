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
    IConfiguration configuration,
    IHttpContextAccessor accessor)
{
    public const int MaxBackupIntervalHours = 168;

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
            v.Check(configuration.GetValue("WireGuard:Enabled", false), "mikrotik.mode",
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
        v.Check(request.BackupIntervalHours is >= 1 and <= MaxBackupIntervalHours, "mikrotik.backupIntervalHours", $"Between 1 and {MaxBackupIntervalHours} hours.");

        // The settings that widen what PNMT may do on the router are an Admin's (§3.5, §6.6, §8.4).
        var before = (existing?.ManagementAccess ?? false, existing?.IncludeSecrets ?? false, existing?.Diagnostics ?? false);
        if ((request.ManagementAccess, request.IncludeSecrets, request.Diagnostics) != before && !IsAdmin())
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.ForbiddenRole,
                "Only an Admin can change management access, secrets in exports or diagnostics.");
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
        request ??= new MikrotikRouterRequest();
        var now = Clock.UtcNow;
        if (router is null)
        {
            router = new MikrotikRouterEntity { SensorId = sensor.Id, Created = now, ScriptChangedAt = now, SetupState = MikrotikSetupState.AwaitingRouter };
            db.MikrotikRouters.Add(router);
        }
        else
        {
            if (router.ManagementAccess != request.ManagementAccess)
            {
                UdpViews.Audit(db, accessor, AuthEvents.MikrotikManagementAccessChanged, $"router {sensor.Name}: management access {(request.ManagementAccess ? "on" : "off")}");
            }
            if (router.IncludeSecrets != request.IncludeSecrets)
            {
                UdpViews.Audit(db, accessor, AuthEvents.MikrotikSecretsChanged, $"router {sensor.Name}: secrets in exports {(request.IncludeSecrets ? "on" : "off")}");
            }
            if (router.Diagnostics != request.Diagnostics)
            {
                UdpViews.Audit(db, accessor, AuthEvents.MikrotikDiagnosticsChanged, $"router {sensor.Name}: diagnostics {(request.Diagnostics ? "on" : "off")}");
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
        router.Address = request.Mode == MikrotikMode.DirectSsh ? address : null;
        router.SshPort = request.SshPort;
        router.PnmtSourceAddress = request.Mode == MikrotikMode.DirectSsh ? source : null;
        router.BackupIntervalHours = request.BackupIntervalHours;
        router.ManagementAccess = request.ManagementAccess;
        router.IncludeSecrets = request.IncludeSecrets;
        router.Diagnostics = request.Diagnostics;
        if (scriptChanged)
        {
            router.ScriptChangedAt = now;
        }
    }

    /// <summary>The type changed away from MIKROTIK: the router row goes.</summary>
    public Task RemoveAsync(Guid sensorId, CancellationToken ct) =>
        db.MikrotikRouters.Where(r => r.SensorId == sensorId).ExecuteDeleteAsync(ct);

    public async Task<MikrotikRouterDetail?> DetailAsync(Guid sensorId, CancellationToken ct)
    {
        var router = await db.MikrotikRouters.AsNoTracking().SingleOrDefaultAsync(r => r.SensorId == sensorId, ct);
        if (router is null)
        {
            return null;
        }
        var installation = await keys.GetAsync(ct);
        var latest = await LatestAsync(ct);
        var backups = db.MikrotikBackups.AsNoTracking().Where(b => b.SensorId == sensorId);
        var count = await backups.CountAsync(ct);
        var newest = await backups.OrderByDescending(b => b.Number).Select(b => new MikrotikBackupEntity
        {
            Id = b.Id, Number = b.Number, Kind = b.Kind, Trigger = b.Trigger, CreatedAt = b.CreatedAt, CreatedByName = b.CreatedByName,
            RouterOsVersion = b.RouterOsVersion, Size = b.Size, LineCount = b.LineCount, LinesAdded = b.LinesAdded, LinesRemoved = b.LinesRemoved,
            IncludesSecrets = b.IncludesSecrets, Encrypted = b.Encrypted,
        }).FirstOrDefaultAsync(ct);
        return Detail(router, installation, latest) with
        {
            LastExportAt = UdpViews.NullableUtc(router.LastExportAt),
            BackupFailures = router.BackupFailures,
            LastBackupError = router.LastBackupError,
            BackupCount = count,
            LatestBackup = newest is null ? null : MikrotikBackupService.Item(newest),
        };
    }

    private MikrotikRouterDetail Detail(MikrotikRouterEntity r, MikrotikInstallation installation, IReadOnlyDictionary<string, LatestVersion> latest)
    {
        var reading = Reading(r);
        var latestVersion = r.Channel is { } ch && latest.TryGetValue(ch, out var l) ? l.Version : null;
        var differences = Differences(r, reading, installation);
        var warnings = new List<string>();
        if (r.Mode == MikrotikMode.DirectSsh && r.PnmtSourceAddress is null)
        {
            warnings.Add("The user pnmt may log in from any address: enter PNMTD's address as the router sees it.");
        }
        if (r.PendingHostKeyFingerprint is not null)
        {
            warnings.Add("The router presented another host key. If the router was replaced or reinstalled, an Admin can accept the new one.");
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
    /// current key. The tunnel's settings follow with the WireGuard modes (phase M3). Empty until the first poll.
    /// </summary>
    internal static IReadOnlyList<string> Differences(MikrotikRouterEntity r, RouterReading? reading, MikrotikInstallation installation)
    {
        if (reading is null || !r.Mode.UsesSsh())
        {
            return [];
        }
        var list = new List<string>();
        var policies = reading.GroupPolicies;
        if (r.IncludeSecrets != policies.Contains("sensitive"))
        {
            list.Add(r.IncludeSecrets ? "Secrets in exports are on, but the group pnmt-read lacks the policy sensitive." : "The group pnmt-read still has the policy sensitive.");
        }
        if (r.Diagnostics != policies.Contains("test"))
        {
            list.Add(r.Diagnostics ? "Diagnostics are on, but the group pnmt-read lacks the policy test." : "The group pnmt-read still has the policy test.");
        }
        var current = installation.Keys.FirstOrDefault();
        if (current is not null && reading.SshKeyOwners.Count > 0 && !reading.SshKeyOwners.Contains(current.Comment))
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
        if (router.Mode.UsesWireGuard())
        {
            throw ApiException.Conflict("wireguard_unavailable", "The WireGuard modes need the WireGuard gateway, which this installation does not have yet.");
        }
        var settings = new JoinScriptSettings
        {
            RouterName = name,
            Instance = installation.Instance,
            Tag = installation.Comment,
            Mode = router.Mode,
            GeneratedAt = now,
            SshPublicKeys = installation.PublicKeys,
            UserAddress = SourceNetwork(router.PnmtSourceAddress),
            SshPort = router.SshPort,
            IncludeSecrets = router.IncludeSecrets,
            Diagnostics = router.Diagnostics,
            ManagementAccess = router.ManagementAccess,
        };
        var notes = new List<string>();
        if (settings.UserAddress is null)
        {
            notes.Add("Without PNMTD's source address the user pnmt may log in from anywhere, with PNMTD's key only.");
        }
        UdpViews.Audit(db, accessor, AuthEvents.MikrotikJoinScriptViewed, $"router {name}: join script ({router.Mode.Label()})");
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
        UdpViews.Audit(db, accessor, AuthEvents.MikrotikHostKeyAccepted,
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
        var items = routers
            .Where(r => snapshot.Sensors.ContainsKey(r.SensorId))
            .Select(r =>
            {
                var s = snapshot.Sensors[r.SensorId];
                var latestVersion = r.Channel is { } ch && latest.TryGetValue(ch, out var l) ? l.Version : null;
                return new MikrotikFleetItem(r.SensorId, s.Info.Name, new Ref(s.Info.HostId, s.Info.HostName), s.Info.Enabled, r.Mode, r.SetupState,
                    s.State, s.LastCode, s.LastMessage, r.Address, r.Identity, r.Model ?? r.BoardName, r.SerialNumber, r.Version, r.Channel, latestVersion,
                    UpdateAvailable(r.Version, latestVersion), r.FirmwareCurrent, r.FirmwareUpgrade, UdpViews.NullableUtc(r.BootedAt),
                    UdpViews.NullableUtc(r.LastSuccessAt), r.LastError, Differences(r, Reading(r), installation).Count > 0);
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

    // --- helpers ------------------------------------------------------------------------------------

    private bool IsAdmin() => accessor.HttpContext is not { } http || http.User.IsInRole(Roles.Admin);

    internal static bool IsHost(string text) =>
        !text.Any(char.IsWhiteSpace) && (IPAddress.TryParse(text.Trim('[', ']'), out _) || Uri.CheckHostName(text) == UriHostNameType.Dns);

    internal static bool IsAddressOrNetwork(string text) =>
        IPAddress.TryParse(text, out _) || IPNetwork.TryParse(text, out _);
}
