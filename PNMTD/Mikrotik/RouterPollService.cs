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

namespace PNMTD.Mikrotik;

/// <summary>What one poll found, and the events it wrote.</summary>
public sealed record RouterPollOutcome(
    bool Succeeded,
    int? Code,
    string Message,
    RouterReading? Reading,
    string? RawOutput,
    IReadOnlyList<RouterEvent> Events,
    bool Skipped = false);

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
    public async Task<RouterPollOutcome> PollAsync(Guid sensorId, bool wait, CancellationToken ct)
    {
        var routerLock = routerLocks.GetOrAdd(sensorId, _ => new SemaphoreSlim(1, 1));
        if (!await routerLock.WaitAsync(wait ? TimeSpan.FromMinutes(2) : TimeSpan.Zero, ct))
        {
            return new RouterPollOutcome(false, null, "Busy: another poll or a job holds the router.", null, null, [], Skipped: true);
        }
        try
        {
            return await PollLockedAsync(sensorId, ct);
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

    private async Task<RouterPollOutcome> PollLockedAsync(Guid sensorId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var router = await db.MikrotikRouters.Include(r => r.Sensor).SingleOrDefaultAsync(r => r.SensorId == sensorId, ct);
        if (router is null)
        {
            return new RouterPollOutcome(false, null, "Not a MikroTik router.", null, null, [], Skipped: true);
        }
        if (!router.Mode.UsesSsh())
        {
            // WireGuard only: the tunnel's state, from the gateway (§5.2, phase M3).
            return new RouterPollOutcome(false, null, "WireGuard only: there is nothing to log in to.", null, null, [], Skipped: true);
        }
        if (router.Mode == MikrotikMode.WireGuardSsh)
        {
            return await RecordAsync(db, router, Failure(RouterFailureKind.Unreachable, "The WireGuard gateway is not available yet."), ct);
        }
        if (string.IsNullOrWhiteSpace(router.Address))
        {
            return await RecordAsync(db, router, Failure(RouterFailureKind.Unreachable, "No address to connect to."), ct);
        }

        var o = options.CurrentValue.Ssh;
        var installation = await keys.GetAsync(ct);
        var privateKeys = await keys.PrivateKeysAsync(ct);
        if (privateKeys.Count == 0)
        {
            return await RecordAsync(db, router, Failure(RouterFailureKind.AuthenticationFailed,
                "PNMTD's SSH key cannot be decrypted (the key ring is gone): rotate the key and paste the join scripts again."), ct);
        }
        var pinned = router.HostKeyFingerprint is { } fp && router.HostKeyType is { } type ? new RouterHostKey(type, fp) : null;
        var target = new RouterTarget(sensorId, router.Address!, router.SshPort, RouterPoll.UserName, RouterCredentials.Keys(privateKeys),
            pinned, TimeSpan.FromSeconds(o.ConnectTimeoutSeconds));

        PollResult result;
        await sessions.WaitAsync(ct);
        try
        {
            result = await ReadAsync(target, installation, o, ct);
        }
        finally
        {
            sessions.Release();
        }
        return await RecordAsync(db, router, result, ct);
    }

    private sealed record PollResult(RouterReading? Reading, string? Raw, RouterHostKey? HostKey, RouterFailureKind? Failure, string? Error,
        RouterHostKey? PresentedHostKey = null);

    private static PollResult Failure(RouterFailureKind kind, string error, RouterHostKey? presented = null) =>
        new(null, null, null, kind, error, presented);

    private async Task<PollResult> ReadAsync(RouterTarget target, MikrotikInstallation installation, MikrotikOptions.SshOptions o, CancellationToken ct)
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
                return new PollResult(reading, exec.Output, session.HostKey, null, null);
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

    /// <summary>RouterOS's own error text where there is one ("not enough permissions (9)"), else "unexpected output".</summary>
    private static string Unexpected(string output, string fallback)
    {
        var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return first is { Length: > 0 and < 300 } && !first.StartsWith('{') ? $"The router answered: {first}" : fallback;
    }

    private async Task<RouterPollOutcome> RecordAsync(PnmtdDbContext db, MikrotikRouterEntity router, PollResult result, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var sensor = router.Sensor;
        var events = new List<RouterEvent>();
        router.LastPollAt = now;
        var everLoggedIn = router.LastSuccessAt is not null || router.HostKeyPinnedAt is not null;

        if (result.Reading is { } r)
        {
            events.AddRange(Success(db, router, r, result, now));
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
            await SensorEventWriter.WriteAsync(db, sensor.Id, e.Code, e.Message, Source(router), ct);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (result.Reading is null)
        {
            logger.LogDebug("MikroTik {Sensor}: {Error}", sensor.Name, result.Error);
        }
        var last = events.LastOrDefault();
        return new RouterPollOutcome(result.Reading is not null && last?.Code is not PNMTStatusCodes.MIKROTIK_ACCESS_DENIED,
            last?.Code ?? router.LastErrorCode, last?.Message ?? result.Error ?? "", result.Reading, result.Raw, events);
    }

    private static IEnumerable<RouterEvent> Success(PnmtdDbContext db, MikrotikRouterEntity router, RouterReading r, PollResult result, DateTime now)
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

        var available = Available(db, r);
        if (available is not null && router.NotifiedUpdateVersion != available)
        {
            events.Add(new RouterEvent(PNMTStatusCodes.MIKROTIK_UPDATE_AVAILABLE, $"{available} available on {r.Channel} ({version} installed)"));
            router.NotifiedUpdateVersion = available;
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
    public static string Source(MikrotikRouterEntity router) => $"mikrotik:{router.Address ?? router.SensorId.ToString()}";

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
