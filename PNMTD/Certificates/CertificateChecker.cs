using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Certificates;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Events;
using PNMTD.Tasks.Helpers;

namespace PNMTD.Certificates;

/// <summary>The jsonb columns' JSON: camelCase, enums as strings, as the API.</summary>
public static class CertificateJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// One check of a sensor, and the evaluations (docs/ssl-certificate-plan.md §5.2, §5.3): a check under the sensor's own
/// lock (the timer skips a busy sensor, <i>Check now</i> waits for it), the version upserted and pinned at the first
/// success, the retention, then the sensor's evaluation. An evaluation writes an event through
/// <see cref="SensorEventWriter"/> only when the verdict key changes.
/// </summary>
public sealed class CertificateCheckService(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<CertificateOptions> options,
    ILogger<CertificateCheckService> logger)
{
    public const string Source = "SSL certificate check";

    public const string FirstCheck = "first check";

    public const string Renewal = "automatically (renewal)";

    /// <summary>The interval of a sensor whose Interval is 0: daily.</summary>
    public const int DefaultIntervalSeconds = 86_400;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();
    private readonly SemaphoreSlim evaluating = new(1, 1);

    public bool IsChecking(Guid sensorId) => locks.TryGetValue(sensorId, out var gate) && gate.CurrentCount == 0;

    public static TimeSpan IntervalOf(int seconds) => TimeSpan.FromSeconds(seconds > 0 ? seconds : DefaultIntervalSeconds);

    /// <summary>Checks now; false when the sensor was busy (the timer) or stayed busy for 30 seconds (Check now).</summary>
    public async Task<bool> CheckAsync(Guid sensorId, bool wait, CancellationToken ct)
    {
        var gate = locks.GetOrAdd(sensorId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(wait ? TimeSpan.FromSeconds(30) : TimeSpan.Zero, ct))
        {
            return false;
        }
        try
        {
            await CheckLockedAsync(sensorId, ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CheckLockedAsync(Guid sensorId, CancellationToken ct)
    {
        var o = options.CurrentValue;
        CertificateSensorEntity? row;
        string? parameters;
        List<CertificateAuthorityEntity> authorities;
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            var loaded = await db.CertificateSensors.AsNoTracking().Where(c => c.SensorId == sensorId)
                .Select(c => new { Row = c, c.Sensor.Parameters }).SingleOrDefaultAsync(ct);
            if (loaded is null)
            {
                return;
            }
            row = loaded.Row;
            parameters = loaded.Parameters;
            authorities = row.Trust == CertificateTrust.Private
                ? await db.CertificateSensorAuthorities.AsNoTracking().Where(a => a.SensorId == sensorId).Select(a => a.Authority).ToListAsync(ct)
                : [];
        }

        CertificateProbeResult result;
        if (!TlsEndpoint.TryParse(parameters, out var endpoint, out var error))
        {
            result = CertificateProbeResult.Failed(CertificateFailureKind.NoSuchName, $"no address to check: {error}");
        }
        else
        {
            var trust = CertificateAuthorityService.TrustFor(row.Trust, authorities);
            result = await CertificateProbe.ReadAsync(endpoint, row.ServerName, row.ConnectTo, trust, o.Timeout, ct);
        }

        await using var store = await dbs.CreateDbContextAsync(ct);
        var tracked = await store.CertificateSensors.Include(c => c.Sensor).SingleOrDefaultAsync(c => c.SensorId == sensorId, ct);
        if (tracked is null || tracked.Sensor.Parameters != parameters || tracked.ServerName != row.ServerName || tracked.ConnectTo != row.ConnectTo
            || tracked.Trust != row.Trust)
        {
            // Deleted, or another endpoint or trust meanwhile: its own check comes.
            return;
        }
        var now = Clock.UtcNow;
        tracked.LastCheckAt = now;
        if (result.Read is { } read)
        {
            var sha = read.Facts.Sha256;
            var version = await store.CertificateVersions.SingleOrDefaultAsync(v => v.SensorId == sensorId && v.Sha256 == sha, ct);
            if (version is null)
            {
                version = new CertificateVersionEntity
                {
                    Id = Guid.NewGuid(),
                    SensorId = sensorId,
                    Sha256 = sha,
                    SpkiSha256 = read.Facts.SpkiSha256,
                    Subject = read.Facts.Subject.Dn,
                    SubjectCommonName = read.Facts.Subject.CommonName,
                    Issuer = read.Facts.Issuer.Dn,
                    IssuerOrganization = read.Facts.Issuer.Organization,
                    IssuerCommonName = read.Facts.Issuer.CommonName,
                    Serial = read.Facts.Serial.Length <= 128 ? read.Facts.Serial : read.Facts.Serial[..128],
                    NotBefore = read.Facts.NotBefore,
                    NotAfter = read.Facts.NotAfter,
                    Names = read.Facts.Names.ToList(),
                    SelfSigned = read.Facts.SelfSigned,
                    Facts = CertificateJson.Write(read.Facts),
                    FirstSeenAt = now,
                };
                store.CertificateVersions.Add(version);
            }
            version.LastSeenAt = now;
            version.Chain = CertificateJson.Write(read.Chain);
            version.ChainPem = read.ChainPem;
            tracked.CurrentVersionId = version.Id;
            tracked.Connection = CertificateJson.Write(read.Connection);
            tracked.Validation = CertificateJson.Write(read.Validation);
            tracked.LastSuccessAt = now;
            tracked.ConsecutiveFailures = 0;
            tracked.FirstFailureAt = null;
            tracked.LastError = null;
            if (tracked.AcceptedVersionId is null)
            {
                tracked.AcceptedVersionId = version.Id;
                tracked.AcceptedAt = now;
                tracked.AcceptedBy = FirstCheck;
                tracked.AcceptedNote = null;
            }
            tracked.NextCheckAt = now + IntervalOf(tracked.Sensor.Interval);
        }
        else
        {
            tracked.ConsecutiveFailures++;
            tracked.FirstFailureAt ??= now;
            tracked.LastError = result.Error is { Length: > 500 } long_ ? long_[..499] + "…" : result.Error;
            tracked.NextCheckAt = now + o.Retry;
        }
        await store.SaveChangesAsync(ct);
        await RetainAsync(store, tracked, o.KeepVersions, ct);
        await EvaluateAsync(sensorId, ct);
    }

    /// <summary>The newest versions per sensor; the current, accepted and announced ones always.</summary>
    private static async Task RetainAsync(PnmtdDbContext db, CertificateSensorEntity row, int keep, CancellationToken ct)
    {
        Guid?[] referenced = [row.CurrentVersionId, row.AcceptedVersionId, row.AnnouncedVersionId];
        var surplus = await db.CertificateVersions.Where(v => v.SensorId == row.SensorId).OrderByDescending(v => v.LastSeenAt)
            .Skip(Math.Max(3, keep)).Select(v => v.Id).ToListAsync(ct);
        var remove = surplus.Where(id => !referenced.Contains(id)).ToList();
        if (remove.Count > 0)
        {
            await db.CertificateVersions.Where(v => remove.Contains(v.Id)).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// The verdict of one sensor, or of every enabled one (§5.3), without connecting anywhere: an event when the key changes,
    /// the baseline moved for a change the rules don't alarm on, and at most an hour to the next check while it fails.
    /// </summary>
    public async Task EvaluateAsync(Guid? only, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            return;
        }
        await evaluating.WaitAsync(ct);
        try
        {
            var o = options.CurrentValue;
            await using var db = await dbs.CreateDbContextAsync(ct);
            var rows = await db.CertificateSensors.Where(c => c.Sensor.Enabled && (only == null || c.SensorId == only))
                .Select(c => new { Row = c, c.Sensor.Parameters }).ToListAsync(ct);
            if (rows.Count == 0)
            {
                return;
            }
            var ids = rows.SelectMany(r => new[] { r.Row.CurrentVersionId, r.Row.AcceptedVersionId }).OfType<Guid>().Distinct().ToList();
            var versions = await db.CertificateVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
            var now = Clock.UtcNow;
            foreach (var item in rows)
            {
                var row = item.Row;
                var expected = row.ServerName ?? (TlsEndpoint.TryParse(item.Parameters, out var endpoint, out _) ? endpoint.Host : item.Parameters ?? "");
                var current = row.CurrentVersionId is { } c && versions.TryGetValue(c, out var cv) ? CertificateSeen.From(cv) : null;
                var accepted = row.AcceptedVersionId is { } a && versions.TryGetValue(a, out var av) ? CertificateSeen.From(av) : null;
                var state = new CertificateCheckState(row.ConsecutiveFailures, row.FirstFailureAt, row.LastError, CertificateJson.Read<CertificateValidation>(row.Validation));
                var verdict = CertificateVerdict.Evaluate(CertificateRules.From(row, expected), state, current, accepted, row.AnnouncedVersionId, now, o.Failures);
                if (verdict is null)
                {
                    continue;
                }
                if (verdict.MoveBaseline && current is not null)
                {
                    row.AcceptedVersionId = current.Id;
                    row.AcceptedAt = now;
                    row.AcceptedBy = Renewal;
                    row.AcceptedNote = null;
                }
                if (verdict.Code is >= Lib.Models.PNMTStatusCodes.SSL_ABOUT_TO_EXPIRE and <= Lib.Models.PNMTStatusCodes.SSL_INVALID
                    && (row.NextCheckAt is null || row.NextCheckAt > now + o.FailingCheck))
                {
                    row.NextCheckAt = now + o.FailingCheck;
                }
                if (verdict.Key == row.LastVerdictKey)
                {
                    continue;
                }
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await SensorEventWriter.WriteAsync(db, row.SensorId, verdict.Code, verdict.Message, Source, ct);
                row.LastCode = verdict.Code;
                row.LastVerdictKey = verdict.Key;
                row.AnnouncedVersionId = current?.Id ?? row.AnnouncedVersionId;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Evaluating the SSL certificate sensors failed");
        }
        finally
        {
            evaluating.Release();
        }
    }
}

/// <summary>
/// The certificate checks' timer (docs/ssl-certificate-plan.md §5.2): every 15 seconds the due checks, up to
/// <c>Certificates__MaxConcurrent</c> at once, paused while PNMTD's uplink is down; every minute an evaluation of every
/// sensor, which needs no network and goes on while the uplink is down.
/// </summary>
public sealed class CertificateChecker(IDbContextFactory<PnmtdDbContext> dbs, CertificateCheckService checks, IOptionsMonitor<CertificateOptions> options,
    ILogger<CertificateChecker> logger) : IHostedService, IDisposable
{
    private readonly RunGuard runGuard = new();
    private readonly TaskRunDecisionMaker uplink = new();
    private readonly CancellationTokenSource stopping = new();
    private DateTime lastEvaluation = DateTime.MinValue;
    private Timer? timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(15));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        timer?.Change(Timeout.Infinite, 0);
        await stopping.CancelAsync();
    }

    public void Dispose()
    {
        timer?.Dispose();
        stopping.Dispose();
    }

    private async Task TickAsync()
    {
        if (!runGuard.TryEnter())
        {
            return;
        }
        try
        {
            await RunOnceAsync(stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The certificate checker failed");
        }
        finally
        {
            runGuard.Exit();
        }
    }

    /// <summary>One round: the due checks, and the evaluation when a minute has passed. Public for the tests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct, bool evaluate = false)
    {
        var o = options.CurrentValue;
        if (!o.Enabled)
        {
            return 0;
        }
        List<Guid> due = [];
        var now = Clock.UtcNow;
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            if (uplink.DetermineIfTaskShouldRun(db))
            {
                due = await db.CertificateSensors.AsNoTracking()
                    .Where(c => c.Sensor.Enabled && (c.NextCheckAt == null || c.NextCheckAt <= now))
                    .OrderBy(c => c.NextCheckAt)
                    .Select(c => c.SensorId)
                    .Take(1000)
                    .ToListAsync(ct);
            }
            db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_CERTIFICATE_CHECK);
            await db.SaveChangesAsync(ct);
        }
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(o.MaxConcurrent, 1, 64), CancellationToken = ct },
            async (id, token) =>
            {
                try
                {
                    await checks.CheckAsync(id, wait: false, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Checking the certificate of sensor {SensorId} failed", id);
                }
            });
        if (evaluate || now - lastEvaluation >= TimeSpan.FromMinutes(1))
        {
            lastEvaluation = now;
            await checks.EvaluateAsync(null, ct);
        }
        return due.Count;
    }
}
