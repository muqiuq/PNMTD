using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Events;
using PNMTD.WireGuard;

namespace PNMTD.Mikrotik;

/// <summary>What one poll found, and the events it wrote.</summary>
public sealed record RouterPollOutcome(
    bool Succeeded,
    int? Code,
    string Message,
    RouterReading? Reading,
    string? RawOutput,
    IReadOnlyList<RouterEvent> Events,
    bool Skipped = false,
    StoredExport? Export = null,
    string? ExportError = null);

/// <summary>What a poll does besides reading: an export now (§6.1, "Back up now"), and on whose behalf.</summary>
public sealed record PollRequest(bool ForceExport = false, MikrotikBackupTrigger? Trigger = null, Guid? UserId = null, string? UserName = null);

public sealed record RouterEvent(int Code, string Message);

/// <summary>
/// One poll of one router (docs/mikrotik-plan.md §5): log in as <c>pnmt</c>, run <see cref="RouterPoll.Script"/>,
/// store the reading, and write the events of §5.3. Used by <see cref="RouterPoller"/>, the API's
/// "poll now" and <c>pnmtd mikrotik poll</c>. One session per router at a time, and at most
/// <c>MaxConcurrentSessions</c> over all routers.
/// <list type="bullet">
/// <item>The sensor's state is the worst current condition, written at every poll; the dedupe keeps a
/// steady state at two rows. Changes (241, 242, 244) are single events before it.</item>
/// <item>Before the first successful login a router writes no events: it waits for its join script,
/// and the Setup tab shows why (§4.3).</item>
/// <item>440 only after the GracePeriod; 441 at once, because it does not heal by itself.</item>
/// </list>
/// </summary>
public sealed class RouterPollService(
    IDbContextFactory<PnmtdDbContext> dbs,
    IRouterConnector connector,
    MikrotikKeyStore keys,
    IOptionsMonitor<MikrotikOptions> options,
    WireGuardNetwork wireGuard,
    ILogger<RouterPollService> logger)
{
    /// <summary>A boot time that moved by more than this is a restart (§5.3); less is the clock's jitter.</summary>
    public static readonly TimeSpan RestartThreshold = TimeSpan.FromMinutes(2);

    /// <summary>When a router's GracePeriod is 0: how long polls may fail before 440 (§5.2).</summary>
    public const int DefaultGracePeriodSeconds = 600;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> routerLocks = new();
    private readonly SemaphoreSlim sessions = new(Math.Max(1, options.CurrentValue.Ssh.MaxConcurrentSessions));

    /// <summary>
    /// Polls now. With <paramref name="wait"/> false (the poller) a router that is busy (another poll, a
    /// job) is skipped; otherwise this waits for it.
    /// </summary>
    public async Task<RouterPollOutcome> PollAsync(Guid sensorId, bool wait, CancellationToken ct, PollRequest? request = null)
    {
        var routerLock = routerLocks.GetOrAdd(sensorId, _ => new SemaphoreSlim(1, 1));
        if (!await routerLock.WaitAsync(wait ? TimeSpan.FromMinutes(2) : TimeSpan.Zero, ct))
        {
            return new RouterPollOutcome(false, null, "Busy: another poll or a job holds the router.", null, null, [], Skipped: true);
        }
        try
        {
            return await PollLockedAsync(sensorId, request ?? new PollRequest(), ct);
        }
        finally
        {
            routerLock.Release();
        }
    }

    /// <summary>Holds the router's lock for a job's whole run (§5.2: the poller pauses for it).</summary>
    public async Task<IAsyncDisposable> LockAsync(Guid sensorId, CancellationToken ct)
    {
        var routerLock = routerLocks.GetOrAdd(sensorId, _ => new SemaphoreSlim(1, 1));
        await routerLock.WaitAsync(ct);
        return new Releaser(routerLock);
    }

    public bool IsBusy(Guid sensorId) => routerLocks.TryGetValue(sensorId, out var l) && l.CurrentCount == 0;

    private async Task<RouterPollOutcome> PollLockedAsync(Guid sensorId, PollRequest request, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var router = await db.MikrotikRouters.Include(r => r.Sensor).SingleOrDefaultAsync(r => r.SensorId == sensorId, ct);
        if (router is null)
        {
            return new RouterPollOutcome(false, null, "Not a MikroTik router.", null, null, [], Skipped: true);
        }
        string host;
        RouterProxy? proxy = null;
        string? tunnelAddress = null;
        if (router.Mode.UsesWireGuard())
        {
            if (!wireGuard.Enabled)
            {
                return new RouterPollOutcome(false, null, "The WireGuard gateway is off (WireGuard__Enabled).", null, null, [], Skipped: true);
            }
            var network = await wireGuard.EnsureAsync(db, ct);
            var settings = wireGuard.Settings(network);
            var now = Clock.UtcNow;
            if (!settings.GatewayUp(now, wireGuard.Options.GatewayDownSeconds))
            {
                // As the uplink guard does: a gateway restart must not make every tunnel router fail (§3.4).
                return new RouterPollOutcome(false, null, "The WireGuard gateway is down: tunnel routers write no events.", null, null, [], Skipped: true);
            }
            var peer = router.WgPeerId is { } peerId ? await db.WgPeers.AsNoTracking().SingleOrDefaultAsync(p => p.Id == peerId, ct) : null;
            if (peer is null)
            {
                return await RecordAsync(db, router, Failure(RouterFailureKind.Unreachable, "The router has no tunnel: save its settings again."), request, ct);
            }
            tunnelAddress = peer.Address;
            var handshake = (await db.WgPeerStatus.AsNoTracking().SingleOrDefaultAsync(s => s.PeerId == peer.Id, ct))?.LatestHandshake;
            var fresh = handshake is { } h && now - h <= TimeSpan.FromSeconds(settings.StaleHandshakeSeconds);
            if (!fresh && settings.Settling(now))
            {
                return new RouterPollOutcome(false, null, "The WireGuard gateway restarted: the tunnel is reconnecting.", null, null, [], Skipped: true);
            }
            if (router.Mode == MikrotikMode.WireGuardOnly)
            {
                return await RecordTunnelAsync(db, router, fresh, handshake, tunnelAddress, now, ct);
            }
            if (!fresh)
            {
                return await RecordAsync(db, router, Failure(RouterFailureKind.Unreachable, NoHandshake(handshake, now)), request, ct, tunnelAddress);
            }
            if (router.SetupState == MikrotikSetupState.AwaitingRouter)
            {
                router.SetupState = MikrotikSetupState.TunnelUp;
            }
            var socks = wireGuard.Unprotect(network.SocksSecretProtected);
            if (socks is null || !TryEndpoint(wireGuard.Options.Gateway, out var gatewayHost, out var gatewayPort))
            {
                return await RecordAsync(db, router, Failure(RouterFailureKind.Unreachable,
                    "The gateway's SOCKS endpoint or secret is not usable (WireGuard__Gateway, the keys volume)."), request, ct, tunnelAddress);
            }
            host = peer.Address;
            proxy = new RouterProxy(gatewayHost, gatewayPort, PNMTD.WireGuard.Gateway.WireGuardGateway.SocksUser, socks);
        }
        else if (string.IsNullOrWhiteSpace(router.Address))
        {
            return await RecordAsync(db, router, Failure(RouterFailureKind.Unreachable, "No address to connect to."), request, ct);
        }
        else
        {
            host = router.Address!;
        }

        var o = options.CurrentValue.Ssh;
        var installation = await keys.GetAsync(ct);
        var privateKeys = await keys.PrivateKeysAsync(ct);
        if (privateKeys.Count == 0)
        {
            return await RecordAsync(db, router, Failure(RouterFailureKind.AuthenticationFailed,
                "PNMTD's SSH key cannot be decrypted (the key ring is gone): rotate the key and paste the join scripts again."), request, ct);
        }
        var pinned = router.HostKeyFingerprint is { } fp && router.HostKeyType is { } type ? new RouterHostKey(type, fp) : null;
        var target = new RouterTarget(sensorId, host, router.SshPort, RouterPoll.UserName, RouterCredentials.Keys(privateKeys),
            pinned, TimeSpan.FromSeconds(o.ConnectTimeoutSeconds), proxy);

        var export = new ExportPlan(request.ForceExport || ExportDue(router, Clock.UtcNow), router.IncludeSecrets);
        PollResult result;
        await sessions.WaitAsync(ct);
        try
        {
            result = await ReadAsync(target, installation, o, export, ct);
        }
        finally
        {
            sessions.Release();
        }
        return await RecordAsync(db, router, result, request, ct, tunnelAddress);
    }

    private static string NoHandshake(DateTime? handshake, DateTime now) =>
        handshake is { } h ? $"No WireGuard handshake since {Time(h, now)}" : "No WireGuard handshake yet";

    private static bool TryEndpoint(string text, out string host, out int port)
    {
        var i = text.LastIndexOf(':');
        host = i > 0 ? text[..i].Trim('[', ']') : text;
        port = 0;
        return i > 0 && int.TryParse(text[(i + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;
    }

    /// <summary>
    /// WireGuard only (§5.2): no login, the handshake is the state. 240 while it is fresh; after the GracePeriod
    /// without one, 440. Before the first handshake, no events: the router waits for its join script.
    /// </summary>
    private async Task<RouterPollOutcome> RecordTunnelAsync(PnmtdDbContext db, MikrotikRouterEntity router, bool fresh, DateTime? handshake,
        string tunnelAddress, DateTime now, CancellationToken ct)
    {
        var events = new List<RouterEvent>();
        router.LastPollAt = now;
        if (fresh)
        {
            router.SetupState = MikrotikSetupState.Ready;
            router.LastSuccessAt = now;
            router.FailingSince = null;
            router.LastError = null;
            router.LastErrorCode = null;
            events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_OK, "Tunnel up (WireGuard only)"));
        }
        else
        {
            var error = NoHandshake(handshake, now);
            router.LastError = error;
            router.LastErrorCode = PNMTStatusCodes.MIKROTIK_UNREACHABLE;
            if (router.LastSuccessAt is not null)
            {
                router.FailingSince ??= now;
                var grace = TimeSpan.FromSeconds(router.Sensor.GracePeriod > 0 ? router.Sensor.GracePeriod : DefaultGracePeriodSeconds);
                if (now - router.FailingSince.Value >= grace)
                {
                    events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_UNREACHABLE, $"Unreachable: {error}"));
                }
            }
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var e in events)
        {
            await SensorEventWriter.WriteAsync(db, router.SensorId, e.Code, e.Message, $"mikrotik:{tunnelAddress}", ct);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        var last = events.LastOrDefault();
        return new RouterPollOutcome(fresh, last?.Code ?? router.LastErrorCode, last?.Message ?? router.LastError ?? "", null, null, events);
    }

    /// <summary>
    /// Due when the backup interval has passed since the last successful export (§6.1), or there was none;
    /// after a failed export, not before <c>Backups:RetryMinutes</c>.
    /// </summary>
    private bool ExportDue(MikrotikRouterEntity router, DateTime now)
    {
        var b = options.CurrentValue.Backups;
        if (router.BackupFailures > 0 && router.LastExportAttemptAt is { } attempt && now - attempt < TimeSpan.FromMinutes(b.RetryMinutes))
        {
            return false;
        }
        return router.LastExportAt is null || now - router.LastExportAt.Value >= TimeSpan.FromHours(Math.Max(1, router.BackupIntervalHours));
    }

    private sealed record ExportPlan(bool Due, bool IncludeSecrets);

    private sealed record PollResult(RouterReading? Reading, string? Raw, RouterHostKey? HostKey, RouterFailureKind? Failure, string? Error,
        RouterHostKey? PresentedHostKey = null)
    {
        public bool ExportAttempted { get; init; }

        public string? ExportOutput { get; init; }

        public string? ExportError { get; init; }

        public bool ExportWithSecrets { get; init; }
    }

    private static PollResult Failure(RouterFailureKind kind, string error, RouterHostKey? presented = null) =>
        new(null, null, null, kind, error, presented);

    private async Task<PollResult> ReadAsync(RouterTarget target, MikrotikInstallation installation, MikrotikOptions.SshOptions o, ExportPlan plan,
        CancellationToken ct)
    {
        try
        {
            await using var session = await connector.ConnectAsync(target, ct);
            var exec = await session.ExecAsync(RouterPoll.Script(installation.Comment), TimeSpan.FromSeconds(o.CommandTimeoutSeconds), o.MaxOutputBytes, ct);
            if (exec.TimedOut)
            {
                return Failure(RouterFailureKind.Protocol, $"The poll timed out after {o.CommandTimeoutSeconds} s") with { HostKey = session.HostKey, Raw = exec.Output };
            }
            try
            {
                var reading = RouterPoll.Parse(exec.Output);
                var result = new PollResult(reading, exec.Output, session.HostKey, null, null);
                return plan.Due && reading.Version is not { IsBelowFloor: true } ? await ExportAsync(session, reading, plan, o, result, ct) : result;
            }
            catch (FormatException ex)
            {
                var text = (exec.Output + "\n" + exec.Error).Trim();
                return Failure(RouterFailureKind.Protocol, Unexpected(text, ex.Message)) with { HostKey = session.HostKey, Raw = exec.Output };
            }
        }
        catch (RouterSessionException ex)
        {
            return Failure(ex.Kind, ex.Message, ex.PresentedHostKey);
        }
    }

    /// <summary>
    /// <c>/export terse</c> in the poll's session (§6.1), with <c>show-sensitive</c> only when the router opted in
    /// and its group has <c>sensitive</c>: without it RouterOS writes <c>*****</c> (M2, 7.13 and 7.25.1).
    /// </summary>
    private async Task<PollResult> ExportAsync(IRouterSession session, RouterReading reading, ExportPlan plan, MikrotikOptions.SshOptions o,
        PollResult result, CancellationToken ct)
    {
        var withSecrets = plan.IncludeSecrets && reading.GroupPolicies.Contains("sensitive");
        var max = options.CurrentValue.Backups.MaxExportBytes;
        try
        {
            var export = await session.ExecAsync(withSecrets ? "/export terse show-sensitive" : "/export terse",
                TimeSpan.FromSeconds(o.ExportTimeoutSeconds), max, ct);
            var error = export.TimedOut ? $"The export timed out after {o.ExportTimeoutSeconds} s"
                : export.Truncated ? $"The export is larger than {max / 1024 / 1024} MiB"
                : ExportText.Problem(export.Output) is { } problem ? problem
                : export.ExitStatus is { } status and not 0 ? $"The export failed with exit status {status}"
                : null;
            return result with { ExportAttempted = true, ExportOutput = error is null ? export.Output : null, ExportError = error, ExportWithSecrets = withSecrets };
        }
        catch (RouterSessionException ex)
        {
            return result with { ExportAttempted = true, ExportError = $"The export broke off: {ex.Message}" };
        }
    }

    /// <summary>RouterOS's own error text where there is one ("not enough permissions (9)"), else "unexpected output".</summary>
    private static string Unexpected(string output, string fallback)
    {
        var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first is { Length: > 0 and < 300 } && !first.StartsWith('{') ? $"The router answered: {first}" : fallback;
    }

    private async Task<RouterPollOutcome> RecordAsync(PnmtdDbContext db, MikrotikRouterEntity router, PollResult result, PollRequest request,
        CancellationToken ct, string? tunnelAddress = null)
    {
        var now = Clock.UtcNow;
        var sensor = router.Sensor;
        var events = new List<RouterEvent>();
        router.LastPollAt = now;
        var everLoggedIn = router.LastSuccessAt is not null || router.HostKeyPinnedAt is not null;

        StoredExport? stored = null;
        if (result.Reading is { } r)
        {
            if (result.ExportAttempted)
            {
                router.LastExportAttemptAt = now;
                if (result.ExportError is null && result.ExportOutput is { } output)
                {
                    var trigger = request.Trigger ?? (router.LastExportHash is null ? MikrotikBackupTrigger.FirstLogin : MikrotikBackupTrigger.Schedule);
                    stored = await BackupStore.SaveExportAsync(db, router, sensor.Name, output, result.ExportWithSecrets, trigger,
                        request.UserId, request.UserName, now, ct);
                    router.BackupFailures = 0;
                    router.LastBackupError = null;
                }
                else
                {
                    router.BackupFailures++;
                    router.LastBackupError = result.ExportError;
                }
            }
            events.AddRange(Success(db, router, r, result, now, stored));
        }
        else
        {
            var kind = result.Failure ?? RouterFailureKind.Protocol;
            var error = result.Error ?? "The poll failed";
            router.LastError = error;
            router.FailingSince ??= now;
            if (kind == RouterFailureKind.HostKeyChanged && result.PresentedHostKey is { } presented)
            {
                router.PendingHostKeyType = presented.Type;
                router.PendingHostKeyFingerprint = presented.Fingerprint;
                router.PendingHostKeySeenAt = now;
            }
            var denied = kind is RouterFailureKind.AuthenticationFailed or RouterFailureKind.HostKeyChanged;
            router.LastErrorCode = denied ? PNMTStatusCodes.MIKROTIK_ACCESS_DENIED : PNMTStatusCodes.MIKROTIK_UNREACHABLE;
            if (everLoggedIn)
            {
                if (denied)
                {
                    router.SetupState = MikrotikSetupState.NeedsAttention;
                    events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_ACCESS_DENIED,
                        kind == RouterFailureKind.AuthenticationFailed ? $"SSH login as {RouterPoll.UserName} refused" : error));
                }
                else
                {
                    var grace = TimeSpan.FromSeconds(sensor.GracePeriod > 0 ? sensor.GracePeriod : DefaultGracePeriodSeconds);
                    if (now - router.FailingSince.Value >= grace)
                    {
                        events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_UNREACHABLE, $"Unreachable since {Time(router.FailingSince.Value, now)}: {error}"));
                    }
                }
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var e in events)
        {
            await SensorEventWriter.WriteAsync(db, sensor.Id, e.Code, e.Message, Source(router, tunnelAddress), ct);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (result.Reading is null)
        {
            logger.LogDebug("MikroTik {Sensor}: {Error}", sensor.Name, result.Error);
        }
        var last = events.LastOrDefault();
        return new RouterPollOutcome(result.Reading is not null && last?.Code is not PNMTStatusCodes.MIKROTIK_ACCESS_DENIED,
            last?.Code ?? router.LastErrorCode, last?.Message ?? result.Error ?? "", result.Reading, result.Raw, events,
            Export: stored, ExportError: result.ExportAttempted ? result.ExportError : null);
    }

    private static IEnumerable<RouterEvent> Success(PnmtdDbContext db, MikrotikRouterEntity router, RouterReading r, PollResult result, DateTime now,
        StoredExport? stored)
    {
        var events = new List<RouterEvent>();
        if (router.HostKeyFingerprint is null && result.HostKey is { } key)
        {
            // Pinned at the first successful login (§4.4).
            router.HostKeyType = key.Type;
            router.HostKeyFingerprint = key.Fingerprint;
            router.HostKeyPinnedAt = now;
        }
        router.PendingHostKeyType = null;
        router.PendingHostKeyFingerprint = null;
        router.PendingHostKeySeenAt = null;

        var version = r.VersionText ?? "?";
        var bootedAt = r.Uptime is { } uptime ? now - uptime : (DateTime?)null;
        var firstReading = router.LastSuccessAt is null;
        if (!firstReading && router.BootedAt is { } before && bootedAt is { } after && (after - before).Duration() > RestartThreshold)
        {
            events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_RESTARTED, $"Restarted at {Time(after, now)} (RouterOS {version})"));
        }
        if (!firstReading && router.Version is { } previous && r.VersionText is { } current && previous != current)
        {
            var newer = RouterOsVersion.TryParse(previous, out var p) && r.Version is { } c && c > p;
            events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_UPDATED, newer ? $"Updated {previous} → {current}" : $"RouterOS changed {previous} → {current}"));
        }

        router.Identity = r.Identity;
        router.Model = r.Model;
        router.BoardName = r.BoardName;
        router.SerialNumber = r.SerialNumber;
        router.Architecture = r.Architecture;
        router.Version = r.VersionText;
        router.Channel = r.Channel;
        router.FirmwareCurrent = r.FirmwareCurrent;
        router.FirmwareUpgrade = r.FirmwareUpgrade;
        router.BootedAt = bootedAt;
        router.Reading = JsonSerializer.Serialize(r, VersionCatalog.Json);
        router.LastSuccessAt = now;
        router.FailingSince = null;
        router.LastError = null;
        router.LastErrorCode = null;
        if (router.Mode == MikrotikMode.WireGuardSsh && r.SshPort is > 0 and <= 65535 and var port)
        {
            // Through the tunnel PNMTD reaches the router's own SSH service; behind Direct SSH's NAT the
            // configured port is what counts.
            router.SshPort = port;
        }

        if (r.Version is { IsBelowFloor: true })
        {
            router.SetupState = MikrotikSetupState.NeedsAttention;
            router.LastErrorCode = PNMTStatusCodes.MIKROTIK_ACCESS_DENIED;
            router.LastError = $"RouterOS {version} is below {RouterOsVersion.Floor}";
            events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_ACCESS_DENIED, router.LastError));
            return events;
        }
        router.SetupState = MikrotikSetupState.Ready;

        if (stored is { Changed: true, First: false, Backup: { } backup })
        {
            events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_CONFIG_CHANGED,
                $"Configuration changed: +{stored.Added} −{stored.Removed} lines (version {backup.Number})"));
        }
        var available = Available(db, r);
        if (available is not null && router.NotifiedUpdateVersion != available)
        {
            events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_UPDATE_AVAILABLE, $"{available} available on {r.Channel} ({version} installed)"));
            router.NotifiedUpdateVersion = available;
        }
        if (router.BackupFailures >= 2)
        {
            // The current condition after 440 and 441 (§5.3): it stays until a backup succeeds.
            events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_BACKUP_FAILED, $"Backups failing: {router.LastBackupError}"));
            return events;
        }
        var model = r.DisplayModel is { Length: > 0 } m ? $" · {m}" : "";
        var channel = r.Channel is { Length: > 0 } ch ? $" ({ch})" : "";
        events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_OK,
            $"RouterOS {version}{channel}{model}{(available is null ? "" : $" · {available} available")}"));
        return events;
    }

    /// <summary>The newest version of the router's channel when it is newer than the installed one (§5.4).</summary>
    private static string? Available(PnmtdDbContext db, RouterReading r)
    {
        if (r.Version is null || r.Channel is null)
        {
            return null;
        }
        var settings = db.MikrotikSettings.AsNoTracking().SingleOrDefault(s => s.Id == MikrotikSettingsEntity.SingletonId);
        return VersionCatalog.Read(settings?.LatestVersions).TryGetValue(r.Channel, out var latest)
            && RouterOsVersion.TryParse(latest.Version, out var v) && v > r.Version
            ? latest.Version
            : null;
    }

    /// <summary>"mikrotik:&lt;address&gt;": the dedupe compares like with like (§5.3).</summary>
    public static string Source(MikrotikRouterEntity router, string? tunnelAddress = null) =>
        $"mikrotik:{tunnelAddress ?? router.Address ?? router.SensorId.ToString()}";

    /// <summary>A time in the configured zone: "10:42" today, "2026-11-12 10:42" before.</summary>
    internal static string Time(DateTime utc, DateTime now)
    {
        var local = Clock.ToLocal(utc);
        return local.Date == Clock.ToLocal(now).Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
