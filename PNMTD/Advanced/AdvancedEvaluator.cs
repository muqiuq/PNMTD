using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Events;
using PNMTD.Tasks.Helpers;

namespace PNMTD.Advanced;

/// <summary>What one run of the evaluator did, for the tests and the log.</summary>
public sealed record AdvancedRunResult(int Reports, int Reevaluated, IReadOnlyList<Guid> Timed, int Events);

/// <summary>
/// The one task that judges the advanced sensors' reports and writes their codes (docs/advanced-sensor-plan.md §7.3).
/// A run takes, in this order: every waiting report of every sensor, oldest first, each as of its receipt (§6.4); the newest
/// report of each sensor whose rules or settings changed; and, on the timer, the sensors with an interval or an age rule, as
/// of now. Each sensor's work is one transaction under its advisory lock, the key the report endpoint and the event writer
/// use. An event is written only when the verdict key changes (§3.3). A nudge starts a run at once; the timer
/// (<see cref="AdvancedEvaluatorTask"/>) every <c>Advanced__EvaluateSeconds</c> finds what a lost nudge left.
/// </summary>
public sealed class AdvancedEvaluator(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<AdvancedOptions> options, IConfiguration configuration,
    IHostApplicationLifetime lifetime, ILogger<AdvancedEvaluator> logger)
{
    /// <summary>The source of the events the timer's pass writes.</summary>
    public const string TimerSource = "PNMTD";

    /// <summary>A report whose judging threw this often is marked judged, as 496 internal.</summary>
    public const int MaxAttempts = 3;

    private static readonly AdvancedOperator[] AgeOperators = [AdvancedOperator.OlderThan, AdvancedOperator.NewerThan];

    private readonly SemaphoreSlim running = new(1, 1);
    private readonly TaskRunDecisionMaker uplink = new();
    private readonly DateTime startedAt = Clock.UtcNow;
    private DateTime lastMaintenance = DateTime.MinValue;
    private ConcurrentDictionary<Guid, TaskCompletionSource> waiters = new();
    private int pumping;
    private int dirty;

    private TimeSpan StartupGrace => TimeSpan.FromMinutes(configuration.GetValue("Heartbeat:StartupGraceMinutes", 5));

    /// <summary>A report was stored: judge it now rather than at the next timer run (unless <c>Advanced__Nudge</c> is off).</summary>
    public void Nudge()
    {
        if (options.CurrentValue.Nudge)
        {
            Wake();
        }
    }

    /// <summary>
    /// A sensor's rules or settings changed (§7.5): its <c>ReevaluateRequestedAt</c> is committed; this wakes the evaluator and,
    /// with <paramref name="wait"/>, waits up to 5 seconds for a run that took the request. False when it took longer.
    /// </summary>
    public async Task<bool> RequestAsync(Guid sensorId, bool wait, CancellationToken ct)
    {
        var waiter = waiters.GetOrAdd(sensorId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Wake();
        if (!wait)
        {
            return true;
        }
        try
        {
            await waiter.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void Wake()
    {
        Volatile.Write(ref dirty, 1);
        if (Interlocked.CompareExchange(ref pumping, 1, 0) == 0)
        {
            _ = Task.Run(PumpAsync);
        }
    }

    /// <summary>Runs while nudges keep coming; a nudge during a run is taken by the next one, straight after.</summary>
    private async Task PumpAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref dirty, 0) == 1 && !lifetime.ApplicationStopping.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(lifetime.ApplicationStopping, timerPass: false);
                }
                catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
                {
                }
                catch (Exception e)
                {
                    logger.LogError(e, "The advanced sensors' evaluator failed");
                }
            }
        }
        finally
        {
            Volatile.Write(ref pumping, 0);
            if (Volatile.Read(ref dirty) == 1 && !lifetime.ApplicationStopping.IsCancellationRequested
                && Interlocked.CompareExchange(ref pumping, 1, 0) == 0)
            {
                _ = Task.Run(PumpAsync);
            }
        }
    }

    /// <summary>
    /// One run (§7.3): the queue, the requests, and with <paramref name="timerPass"/> the sensors with an interval or an age
    /// rule, then the hourly maintenance. One run at a time; a run takes all the work there is. Public for the tests.
    /// </summary>
    public async Task<AdvancedRunResult> RunOnceAsync(CancellationToken ct, bool timerPass = true)
    {
        await running.WaitAsync(ct);
        var served = Interlocked.Exchange(ref waiters, new ConcurrentDictionary<Guid, TaskCompletionSource>());
        try
        {
            var now = Clock.UtcNow;
            bool holdStale;
            List<Guid> queued, requested, timed = [];
            await using (var db = await dbs.CreateDbContextAsync(ct))
            {
                // 460 waits while reports can't arrive (the uplink is down) and right after a start, as HeartbeatCheckTask does.
                holdStale = !uplink.DetermineIfTaskShouldRun(db) || (StartupGrace > TimeSpan.Zero && now - startedAt < StartupGrace);
                queued = await db.AdvancedReports.Where(r => r.EvaluatedAt == null).Select(r => r.SensorId).Distinct().ToListAsync(ct);
                requested = await db.AdvancedSensors.Where(a => a.ReevaluateRequestedAt != null).Select(a => a.SensorId).ToListAsync(ct);
                if (timerPass)
                {
                    timed = await db.AdvancedSensors
                        .Where(a => a.Sensor.Enabled && a.Sensor.Type == SensorType.ADVANCED && a.LastReportId != null
                            && (a.Sensor.Interval > 0 || db.AdvancedRules.Any(r => r.SensorId == a.SensorId && r.Enabled && AgeOperators.Contains(r.Operator))))
                        .Select(a => a.SensorId).ToListAsync(ct);
                }
                db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_ADVANCED_EVALUATION);
                await db.SaveChangesAsync(ct);
            }
            int reports = 0, events = 0;
            foreach (var id in queued)
            {
                var (judged, written) = await JudgeAsync(id, Mode.Queue, holdStale, ct);
                reports += judged;
                events += written;
            }
            foreach (var id in requested)
            {
                events += (await JudgeAsync(id, Mode.Reevaluate, holdStale, ct)).Events;
            }
            foreach (var id in timed)
            {
                events += (await JudgeAsync(id, Mode.Timer, holdStale, ct)).Events;
            }
            if (timerPass && now - lastMaintenance >= TimeSpan.FromHours(1))
            {
                lastMaintenance = now;
                await MaintainAsync(ct);
            }
            return new AdvancedRunResult(reports, requested.Count, timed, events);
        }
        finally
        {
            running.Release();
            foreach (var waiter in served.Values)
            {
                waiter.TrySetResult();
            }
        }
    }

    private enum Mode { Queue, Reevaluate, Timer }

    /// <summary>One sensor's work in one transaction; a failure is logged, rolled back, and counted against the report.</summary>
    private async Task<(int Reports, int Events)> JudgeAsync(Guid sensorId, Mode mode, bool holdStale, CancellationToken ct)
    {
        Guid? current = null;
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(db, sensorId, ct);
            var work = await SensorWork.LoadAsync(db, sensorId, ct);
            var now = Clock.UtcNow;
            if (work is null)
            {
                // The type changed away or the sensor is gone: nothing to judge, and nothing may wait forever.
                await db.AdvancedReports.Where(r => r.SensorId == sensorId && r.EvaluatedAt == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.EvaluatedAt, now), ct);
                await db.AdvancedSensors.Where(a => a.SensorId == sensorId).ExecuteUpdateAsync(u => u.SetProperty(a => a.ReevaluateRequestedAt, (DateTime?)null), ct);
                await transaction.CommitAsync(ct);
                return (0, 0);
            }
            var (reports, events) = mode == Mode.Queue
                ? await QueueAsync(db, work, id => current = id, ct)
                : (0, await NewestAsync(db, work, mode, holdStale, ct));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (reports, events);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Evaluating the advanced sensor {SensorId} failed", sensorId);
            if (current is { } report)
            {
                try
                {
                    await using var db = await dbs.CreateDbContextAsync(ct);
                    await db.AdvancedReports.Where(r => r.Id == report && r.EvaluatedAt == null)
                        .ExecuteUpdateAsync(u => u.SetProperty(r => r.EvaluationAttempts, r => (short)(r.EvaluationAttempts + 1)), ct);
                }
                catch (Exception inner) when (inner is not OperationCanceledException)
                {
                    logger.LogWarning(inner, "Counting the failed evaluation of report {ReportId} failed", report);
                }
            }
            return (0, 0);
        }
    }

    internal static Task LockAsync(PnmtdDbContext db, Guid sensorId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [BitConverter.ToInt64(sensorId.ToByteArray(), 0)], ct);

    /// <summary>What a sensor's judging reads once: its side row (tracked), rules, fields, and newest event's code.</summary>
    private sealed class SensorWork
    {
        public required AdvancedSensorEntity Side { get; init; }

        public required List<AdvancedRuleSpec> Rules { get; init; }

        public required List<AdvancedFieldEntity> Fields { get; init; }

        public required TimeSpan Interval { get; init; }

        public required TimeSpan Grace { get; init; }

        public required Dictionary<Guid, AdvancedRuleState> States { get; init; }

        public int? LastEventCode { get; set; }

        public DateTime LastEventAt { get; set; }

        public static async Task<SensorWork?> LoadAsync(PnmtdDbContext db, Guid sensorId, CancellationToken ct)
        {
            var side = await db.AdvancedSensors.Include(a => a.Sensor).SingleOrDefaultAsync(a => a.SensorId == sensorId, ct);
            if (side is null || side.Sensor.Type != SensorType.ADVANCED)
            {
                return null;
            }
            var rules = await db.AdvancedRules.AsNoTracking().Where(r => r.SensorId == sensorId).OrderBy(r => r.Order).ThenBy(r => r.Created).ToListAsync(ct);
            var fields = await db.AdvancedFields.AsNoTracking().Where(f => f.SensorId == sensorId).OrderBy(f => f.Order).ThenBy(f => f.Created).ToListAsync(ct);
            var last = await db.Events.AsNoTracking().Where(e => e.SensorId == sensorId).OrderByDescending(e => e.Created)
                .Select(e => new { e.Code, e.Created }).FirstOrDefaultAsync(ct);
            return new SensorWork
            {
                Side = side,
                Rules = rules.Select(r => r.ToSpec()).ToList(),
                Fields = fields,
                Interval = TimeSpan.FromSeconds(Math.Max(0, side.Sensor.Interval)),
                Grace = TimeSpan.FromSeconds(Math.Max(0, side.Sensor.GracePeriod)),
                States = AdvancedJson.Read<Dictionary<Guid, AdvancedRuleState>>(side.RuleStates) ?? [],
                LastEventCode = last?.Code,
                LastEventAt = last?.Created ?? DateTime.MinValue,
            };
        }

        public bool HasAgeRule => Rules.Any(r => r.Enabled && AdvancedRules.IsAge(r.Operator));
    }

    /// <summary>Every waiting report of the sensor, in arrival order, each as of its receipt and against the report before it.</summary>
    private async Task<(int Reports, int Events)> QueueAsync(PnmtdDbContext db, SensorWork work, Action<Guid> judging, CancellationToken ct)
    {
        var sensorId = work.Side.SensorId;
        var queue = await db.AdvancedReports.Where(r => r.SensorId == sensorId && r.EvaluatedAt == null)
            .OrderBy(r => r.ReceivedAt).ThenBy(r => r.Id).ToListAsync(ct);
        if (queue.Count == 0)
        {
            return (0, 0);
        }
        var first = queue[0];
        var previousBody = await db.AdvancedReports.AsNoTracking()
            .Where(r => r.SensorId == sensorId && r.Valid && r.EvaluatedAt != null && r.Id != first.Id && r.ReceivedAt <= first.ReceivedAt)
            .OrderByDescending(r => r.ReceivedAt).Select(r => r.Body).FirstOrDefaultAsync(ct);
        var previous = AdvancedReportReader.Parse(previousBody);
        var events = 0;
        try
        {
            foreach (var report in queue)
            {
                judging(report.Id);
                var at = report.LastReceivedAt;
                AdvancedVerdict? verdict;
                AdvancedResults results;
                if (report.EvaluationAttempts >= MaxAttempts)
                {
                    verdict = AdvancedVerdict.Internal();
                    results = new AdvancedResults([], [], $"PNMT couldn't evaluate this report in {MaxAttempts} attempts; see PNMTD's log.");
                }
                else
                {
                    var document = report.Valid ? AdvancedReportReader.Parse(report.Body) : null;
                    var input = document is not null
                        ? AdvancedReportInput.Of(document.RootElement)
                        : new AdvancedReportInput(false, report.Valid ? AdvancedReportError.Internal : report.ErrorKind, report.Error, null);
                    // A repeat is compared with itself: the same body again (§6.3).
                    var before = report.Repeats > 0 && document is not null ? document.RootElement : previous?.RootElement;
                    verdict = AdvancedVerdict.Evaluate(work.Rules, input, before, at, at, work.Interval, work.Grace,
                        withValues: !work.Side.NamesPeople)!;
                    var values = document is null ? [] : FieldValues(work.Fields, document.RootElement);
                    results = new AdvancedResults(verdict.Results, values);
                    if (document is not null)
                    {
                        await SampleAsync(db, sensorId, work.Fields, values, at, ct);
                        previous?.Dispose();
                        previous = document;
                    }
                }
                report.Code = verdict.Code;
                report.Failed = verdict.Failed;
                report.Warnings = verdict.Warnings;
                report.Results = AdvancedJson.Write(results);
                report.EvaluatedAt = Clock.UtcNow;
                if (await ApplyAsync(db, work, verdict, at, string.IsNullOrEmpty(report.Source) ? "report" : report.Source, ct))
                {
                    events++;
                }
            }
        }
        finally
        {
            previous?.Dispose();
        }
        return (queue.Count, events);
    }

    /// <summary>
    /// The newest report as of now: after a change of the rules or the settings (its outcomes are stored anew), or in the
    /// timer's pass, for staleness and age rules (§7.3, steps 2 and 3).
    /// </summary>
    private async Task<int> NewestAsync(PnmtdDbContext db, SensorWork work, Mode mode, bool holdStale, CancellationToken ct)
    {
        var side = work.Side;
        var sensorId = side.SensorId;
        if (mode == Mode.Reevaluate)
        {
            side.ReevaluateRequestedAt = null;
        }
        var newest = await db.AdvancedReports.Where(r => r.SensorId == sensorId).OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);
        if (newest is null || newest.EvaluatedAt is null)
        {
            // None yet, or one that arrived during this run: the next run's queue takes it.
            return 0;
        }
        var now = Clock.UtcNow;
        var lastReportAt = side.LastReportAt ?? newest.LastReceivedAt;
        var stale = work.Interval > TimeSpan.Zero && now - lastReportAt > work.Interval + work.Grace;
        if (mode == Mode.Timer)
        {
            if (holdStale && stale)
            {
                // Held back: the uplink is down, or PNMTD just started.
                return 0;
            }
            if (!work.HasAgeRule)
            {
                if (!stale)
                {
                    return 0;
                }
                // Staleness needs only the receipt's time, not the body.
                var missing = AdvancedVerdict.Evaluate([], new AdvancedReportInput(true, null, null, null), null, now, lastReportAt, work.Interval, work.Grace)!;
                return await ApplyAsync(db, work, missing, now, TimerSource, ct, keepStates: true) ? 1 : 0;
            }
        }
        using var document = newest.Valid ? AdvancedReportReader.Parse(newest.Body) : null;
        JsonDocument? earlier = null;
        try
        {
            if (newest.Repeats == 0 && work.Rules.Any(r => r.Enabled && AdvancedRules.IsChange(r.Operator)))
            {
                earlier = AdvancedReportReader.Parse(await db.AdvancedReports.AsNoTracking()
                    .Where(r => r.SensorId == sensorId && r.Valid && r.Id != newest.Id && r.ReceivedAt <= newest.ReceivedAt)
                    .OrderByDescending(r => r.ReceivedAt).Select(r => r.Body).FirstOrDefaultAsync(ct));
            }
            var input = document is not null
                ? AdvancedReportInput.Of(document.RootElement)
                : new AdvancedReportInput(false, newest.Valid ? AdvancedReportError.Internal : newest.ErrorKind, newest.Error, null);
            var before = newest.Repeats > 0 && document is not null ? document.RootElement : earlier?.RootElement;
            var verdict = newest.EvaluationAttempts >= MaxAttempts
                ? AdvancedVerdict.Internal()
                : AdvancedVerdict.Evaluate(work.Rules, input, before, now, lastReportAt, work.Interval, work.Grace, checkStale: !holdStale,
                    withValues: !work.Side.NamesPeople)!;
            if (mode == Mode.Reevaluate)
            {
                newest.Code = verdict.Code;
                newest.Failed = verdict.Failed;
                newest.Warnings = verdict.Warnings;
                var values = document is null ? [] : FieldValues(work.Fields, document.RootElement);
                newest.Results = AdvancedJson.Write(new AdvancedResults(verdict.Results, values,
                    AdvancedJson.Read<AdvancedResults>(newest.Results)?.Error));
            }
            // A rule change while 460 is held back: the outcomes are stored, the state stays as it is.
            return await ApplyAsync(db, work, verdict, now, TimerSource, ct, statesOnly: holdStale && stale) ? 1 : 0;
        }
        finally
        {
            earlier?.Dispose();
        }
    }

    /// <summary>
    /// The verdict onto the side row; an event when the key changed, or when the sensor's newest event has another code (one
    /// posted through /event). Events stay in order: a second one in the same instant is a microsecond later.
    /// </summary>
    private static async Task<bool> ApplyAsync(PnmtdDbContext db, SensorWork work, AdvancedVerdict verdict, DateTime at, string source,
        CancellationToken ct, bool keepStates = false, bool statesOnly = false)
    {
        var side = work.Side;
        side.LastEvaluatedAt = Clock.UtcNow;
        if (!keepStates && verdict.Results.Count > 0)
        {
            var known = work.Rules.Select(r => r.Id).ToHashSet();
            foreach (var id in work.States.Keys.Where(id => !known.Contains(id)).ToList())
            {
                work.States.Remove(id);
            }
            foreach (var result in verdict.Results)
            {
                work.States[result.RuleId] = work.States.TryGetValue(result.RuleId, out var state) && state.Outcome == result.Outcome
                    ? state with { Result = result }
                    : new AdvancedRuleState(result.Outcome, at, result);
            }
            side.RuleStates = AdvancedJson.Write(work.States);
        }
        if (statesOnly || (verdict.Key == side.LastVerdictKey && work.LastEventCode == verdict.Code))
        {
            return false;
        }
        var written = await SensorEventWriter.WriteAsync(db, side.SensorId, verdict.Code, verdict.Message, AdvancedValues.Cut(source, 100), ct);
        if (written.Created <= work.LastEventAt)
        {
            written.Created = work.LastEventAt.AddTicks(10);
        }
        work.LastEventAt = written.Created;
        work.LastEventCode = verdict.Code;
        side.LastVerdictKey = verdict.Key;
        side.LastCode = verdict.Code;
        side.LastMessage = verdict.Message;
        // The writer reads the newest events from the database: what this one added must be there before the next.
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Each field's value in a report (null when its path matches nothing), for the overview and the history.</summary>
    internal static List<AdvancedFieldValue> FieldValues(IEnumerable<AdvancedFieldEntity> fields, JsonElement root) =>
        fields.Select(f => new AdvancedFieldValue(f.Id,
            AdvancedPath.TryParse(f.Path, out var path, out _) && path.TryGet(root, out var value) ? value.Clone() : null)).ToList();

    /// <summary>The charted fields' numeric values into their minute (§5.5): min, max, last and count, upserted.</summary>
    private static async Task SampleAsync(PnmtdDbContext db, Guid sensorId, List<AdvancedFieldEntity> fields, List<AdvancedFieldValue> values,
        DateTime at, CancellationToken ct)
    {
        var minute = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        foreach (var field in fields.Where(f => f.Chart))
        {
            if (values.FirstOrDefault(v => v.FieldId == field.Id)?.Value is not { } value || !AdvancedValues.TryNumber(value, out var number)
                || !double.IsFinite(number.Double))
            {
                continue;
            }
            var x = number.Double;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO advanced_samples ("SensorId", "FieldId", "Minute", "Min", "Max", "Last", "Count")
                VALUES ({sensorId}, {field.Id}, {minute}, {x}, {x}, {x}, 1)
                ON CONFLICT ("SensorId", "FieldId", "Minute") DO UPDATE SET
                    "Min" = LEAST(advanced_samples."Min", EXCLUDED."Min"),
                    "Max" = GREATEST(advanced_samples."Max", EXCLUDED."Max"),
                    "Last" = EXCLUDED."Last",
                    "Count" = advanced_samples."Count" + 1
                """, ct);
        }
    }

    /// <summary>
    /// The retention (§5.4, §5.5), hourly: per sensor the newest <c>KeepReports</c>, none older than <c>KeepDays</c>, at most
    /// <c>MaxBytesPerSensor</c>; the newest two and every waiting report always. Samples older than <c>SampleDays</c>.
    /// </summary>
    public async Task MaintainAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var cutoff = now - o.KeepFor;
        var reports = await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM advanced_reports r USING (
                SELECT "Id", "EvaluatedAt", "LastReceivedAt",
                       row_number() OVER w AS n,
                       sum("Bytes") OVER w AS total
                FROM advanced_reports
                WINDOW w AS (PARTITION BY "SensorId" ORDER BY "LastReceivedAt" DESC, "Id" DESC)
            ) x
            WHERE r."Id" = x."Id" AND x.n > 2 AND x."EvaluatedAt" IS NOT NULL
              AND (x.n > {o.Keep} OR x."LastReceivedAt" < {cutoff} OR x.total > {o.MaxBytesKept})
            """, ct);
        var sampleCutoff = now - o.SamplesFor;
        var samples = await db.AdvancedSamples.Where(s => s.Minute < sampleCutoff).ExecuteDeleteAsync(ct);
        if (reports + samples > 0)
        {
            logger.LogInformation("Advanced sensors: {Reports} reports and {Samples} samples past their retention removed", reports, samples);
        }
    }

    /// <summary>The newest reports of a sensor beyond <c>KeepReports</c>, after a report was stored (§7.1); never a waiting one.</summary>
    internal static Task<int> TrimAsync(PnmtdDbContext db, Guid sensorId, int keep, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM advanced_reports WHERE "Id" IN (
                SELECT "Id" FROM (
                    SELECT "Id", "EvaluatedAt", row_number() OVER (ORDER BY "LastReceivedAt" DESC, "Id" DESC) AS n
                    FROM advanced_reports WHERE "SensorId" = {sensorId}
                ) x
                WHERE x.n > {Math.Max(2, keep)} AND x."EvaluatedAt" IS NOT NULL)
            """, ct);
}

/// <summary>The evaluator's timer (§7.3): every <c>Advanced__EvaluateSeconds</c> a full run, the timer's pass included.</summary>
public sealed class AdvancedEvaluatorTask(AdvancedEvaluator evaluator, IOptionsMonitor<AdvancedOptions> options, ILogger<AdvancedEvaluatorTask> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await evaluator.RunOnceAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "The advanced sensors' evaluator failed");
                }
                await Task.Delay(options.CurrentValue.Evaluate, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
