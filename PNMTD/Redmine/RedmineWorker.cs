using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Events;

namespace PNMTD.Redmine;

/// <summary>What a job is: its key (null: never joined), its lane, what for and for whom; <c>Fresh</c> joins only a job not started yet.</summary>
public sealed record RedmineJobSpec(string? Key, RedmineLane Lane, string Purpose, string AskedBy, bool Fresh = false, bool IsWrite = false);

/// <summary>One thing the worker does (docs/redmine-queue-outbound-plan.md §3.2): queued until its first call gets a slot, then running.</summary>
public sealed class RedmineJob
{
    private int lane;
    private long startedTicks;
    private int callsRunning;
    private int callsDone;

    internal RedmineJob(long seq, RedmineJobSpec spec)
    {
        Seq = seq;
        Key = spec.Key;
        lane = (int)spec.Lane;
        Purpose = spec.Purpose;
        AskedBy = spec.AskedBy;
        IsWrite = spec.IsWrite;
        QueuedAt = Clock.UtcNow;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public long Seq { get; }

    public string? Key { get; }

    public string Purpose { get; }

    public string AskedBy { get; }

    public bool IsWrite { get; }

    public DateTime QueuedAt { get; }

    public RedmineLane Lane => (RedmineLane)Volatile.Read(ref lane);

    public DateTime? StartedAt => Interlocked.Read(ref startedTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;

    public int CallsRunning => Volatile.Read(ref callsRunning);

    public int CallsDone => Volatile.Read(ref callsDone);

    internal Task<object?> Task { get; set; } = null!;

    /// <summary>Moves the job up to <paramref name="to"/> if that lane is worked earlier (§3.3); true if it moved.</summary>
    internal bool Raise(RedmineLane to)
    {
        while (true)
        {
            var current = Volatile.Read(ref lane);
            if ((int)to >= current)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref lane, (int)to, current) == current)
            {
                return true;
            }
        }
    }

    internal void CallStarted()
    {
        Interlocked.CompareExchange(ref startedTicks, Clock.UtcNow.Ticks, 0);
        Interlocked.Increment(ref callsRunning);
    }

    internal void CallEnded()
    {
        Interlocked.Decrement(ref callsRunning);
        Interlocked.Increment(ref callsDone);
    }

    /// <summary>A write waits for its turn among the writes before its first call (§3.3): that counts as started.</summary>
    internal void Started() => Interlocked.CompareExchange(ref startedTicks, Clock.UtcNow.Ticks, 0);
}

/// <summary>
/// Slots handed out in the lanes' order (§3.3, §3.4): button, write, interactive, background; within a lane, the earlier job's
/// calls first. With <c>keepOne</c> a background call never takes the last free slot, and background calls wait while the
/// lane is paused after a 429 or 503.
/// </summary>
internal sealed class RedmineGate(Func<int> capacity, bool keepOne)
{
    private sealed class Waiter(RedmineJob job, long seq)
    {
        public RedmineJob Job { get; } = job;

        public long Seq { get; } = seq;

        public TaskCompletionSource Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object sync = new();
    private readonly List<Waiter> waiters = [];
    private int inUse;
    private long seq;
    private DateTime pausedUntil = DateTime.MinValue;

    public int InUse
    {
        get
        {
            lock (sync)
            {
                return inUse;
            }
        }
    }

    public DateTime PausedUntil
    {
        get
        {
            lock (sync)
            {
                return pausedUntil;
            }
        }
    }

    public async Task AcquireAsync(RedmineJob job, CancellationToken ct)
    {
        Waiter w;
        lock (sync)
        {
            w = new Waiter(job, ++seq);
            waiters.Add(w);
            PumpLocked();
        }
        if (w.Granted.Task.IsCompleted)
        {
            return;
        }
        await using var registration = ct.Register(() =>
        {
            lock (sync)
            {
                if (waiters.Remove(w))
                {
                    w.Granted.TrySetCanceled(ct);
                }
            }
        });
        await w.Granted.Task;
    }

    public void Release()
    {
        lock (sync)
        {
            inUse--;
            PumpLocked();
        }
    }

    /// <summary>Hands out what is free now: after a job moved up a lane, or a pause ended.</summary>
    public void Pump()
    {
        lock (sync)
        {
            PumpLocked();
        }
    }

    public void PauseBackground(TimeSpan span)
    {
        lock (sync)
        {
            var until = Clock.UtcNow + span;
            if (until > pausedUntil)
            {
                pausedUntil = until;
            }
        }
    }

    private void PumpLocked()
    {
        var cap = Math.Max(1, capacity());
        while (waiters.Count > 0 && inUse < cap)
        {
            var best = waiters[0];
            foreach (var w in waiters)
            {
                if (Before(w, best))
                {
                    best = w;
                }
            }
            if (best.Job.Lane == RedmineLane.Background
                && ((keepOne && cap > 1 && cap - inUse < 2) || Clock.UtcNow < pausedUntil))
            {
                // Sorted by lane: what waits is background only.
                return;
            }
            waiters.Remove(best);
            inUse++;
            best.Granted.TrySetResult();
        }
    }

    private static bool Before(Waiter a, Waiter b) =>
        a.Job.Lane != b.Job.Lane ? a.Job.Lane < b.Job.Lane : a.Job.Seq != b.Job.Seq ? a.Job.Seq < b.Job.Seq : a.Seq < b.Seq;
}

/// <summary>What a job reads Redmine with: each call waits for a slot in the job's lane (§3.4) and goes into the Redmine log.</summary>
public sealed class RedmineCalls
{
    private readonly RedmineWorker worker;

    internal RedmineCalls(RedmineWorker worker, RedmineJob job, CancellationToken token)
    {
        this.worker = worker;
        Job = job;
        Token = token;
    }

    public RedmineJob Job { get; }

    public CancellationToken Token { get; }

    /// <summary>A read; <paramref name="summary"/> says what for within the job, for the log ("overview: urgent").</summary>
    public Task<T> GetAsync<T>(string pathAndQuery, string summary, bool asWriter = false) =>
        worker.CallAsync(Job, (info, ct) => worker.Client.GetAsync<T>(pathAndQuery, ct, asWriter, info), summary, Token);

    public Task<JsonDocument?> WriteAsync(HttpMethod method, string path, object body, string summary) =>
        worker.CallAsync(Job, (info, ct) => worker.Client.WriteAsync(method, path, body, ct, info), summary, Token);
}

/// <summary>Something read from Redmine, kept under its key (§4.1), and how to read it again.</summary>
internal sealed class RedmineSnapshot(string key, string purpose, Func<RedmineCalls, Task<object>> load, bool pinned, TimeSpan? maxAge)
{
    public string Key { get; } = key;

    public string Purpose { get; } = purpose;

    public Func<RedmineCalls, Task<object>> Load { get; } = load;

    /// <summary>Never removed: the lookups and the overview of all projects.</summary>
    public bool Pinned { get; } = pinned;

    /// <summary>How old it may get before a look at it refreshes it; null: <c>Redmine__RefreshSeconds</c>.</summary>
    public TimeSpan? MaxAge { get; } = maxAge;

    public object? Value { get; set; }

    public DateTime? RefreshedAt { get; set; }

    public DateTime? FailedAt { get; set; }

    public string? Error { get; set; }

    public DateTime RequestedAt { get; set; }

    /// <summary>Set by a write that may have changed it (§3.8): refreshed if watched, or when looked at.</summary>
    public DateTime? DueSince { get; set; }

    public int Failures { get; set; }

    public DateTime NextAttemptAt { get; set; }
}

/// <summary>
/// The one task that talks to Redmine (docs/redmine-queue-outbound-plan.md §3): its jobs, their lanes and slots, the snapshots
/// of what it read (§4), and its scheduler. Jobs run whether or not the scheduler does (the tests run without it); a caller
/// that stops waiting doesn't stop a job. The loaders are in <c>RedmineWorker.Loads.cs</c>.
/// </summary>
public sealed partial class RedmineWorker : BackgroundService
{
    private readonly IOptionsMonitor<RedmineOptions> options;
    private readonly IDbContextFactory<PnmtdDbContext> dbs;
    private readonly SystemSensors systemSensors;
    private readonly ILogger<RedmineWorker> logger;
    private readonly object sync = new();
    private readonly Dictionary<string, RedmineJob> keyed = [];
    private readonly Dictionary<Guid, RedmineJob> all = [];
    private readonly ConcurrentDictionary<string, RedmineSnapshot> snapshots = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim nudge = new(0);
    private readonly RedmineGate slots;
    private readonly RedmineGate writes;
    private long seq;
    private int syncFailures;

    public RedmineWorker(RedmineClient client, IOptionsMonitor<RedmineOptions> options, IDbContextFactory<PnmtdDbContext> dbs, SystemSensors systemSensors,
        ILogger<RedmineWorker> logger)
    {
        Client = client;
        this.options = options;
        this.dbs = dbs;
        this.systemSensors = systemSensors;
        this.logger = logger;
        slots = new RedmineGate(() => Math.Clamp(options.CurrentValue.Parallelism, 1, 32), keepOne: true);
        writes = new RedmineGate(() => 1, keepOne: false);
    }

    internal RedmineClient Client { get; }

    private RedmineOptions O => options.CurrentValue;

    private TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Max(5, O.RefreshSeconds));

    private TimeSpan WaitTime => TimeSpan.FromSeconds(Math.Clamp(O.WaitSeconds, 1, 600));

    // --- jobs ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs a job, or joins the one with the same key that is queued or running (§3.5), moving it up to this lane. A fresh job
    /// joins only one that hasn't started; writes never join.
    /// </summary>
    public Task<T> RunAsync<T>(RedmineJobSpec spec, Func<RedmineCalls, Task<T>> work)
    {
        lock (sync)
        {
            if (spec.Key is { } key && !spec.IsWrite && keyed.TryGetValue(key, out var existing) && !(spec.Fresh && existing.StartedAt is not null))
            {
                if (existing.Raise(spec.Lane))
                {
                    slots.Pump();
                    writes.Pump();
                }
                return Unwrap<T>(existing.Task);
            }
            var job = new RedmineJob(++seq, spec);
            if (spec.Key is { } k)
            {
                keyed[k] = job;
            }
            all[job.Id] = job;
            job.Task = RunJobAsync(job, async calls => (object?)await work(calls));
            return Unwrap<T>(job.Task);
        }
    }

    private async Task<object?> RunJobAsync(RedmineJob job, Func<RedmineCalls, Task<object?>> work)
    {
        // Out of the caller's lock and stack before anything runs.
        await Task.Yield();
        try
        {
            if (job.IsWrite)
            {
                await writes.AcquireAsync(job, stopping.Token);
                job.Started();
            }
            try
            {
                return await work(new RedmineCalls(this, job, stopping.Token));
            }
            finally
            {
                if (job.IsWrite)
                {
                    writes.Release();
                }
            }
        }
        finally
        {
            lock (sync)
            {
                if (job.Key is { } k && keyed.TryGetValue(k, out var current) && current == job)
                {
                    keyed.Remove(k);
                }
                all.Remove(job.Id);
            }
        }
    }

    private static async Task<T> Unwrap<T>(Task<object?> task) => (T)(await task)!;

    internal async Task<T> CallAsync<T>(RedmineJob job, Func<RedmineCallInfo, CancellationToken, Task<T>> call, string summary, CancellationToken ct)
    {
        var waited = Stopwatch.StartNew();
        await slots.AcquireAsync(job, ct);
        job.CallStarted();
        try
        {
            return await call(new RedmineCallInfo(job.Id, job.Lane, (int)waited.ElapsedMilliseconds, job.Purpose, job.AskedBy, summary), ct);
        }
        catch (RedmineException e) when (e.HttpStatus is 429 or 503)
        {
            slots.PauseBackground(e.RetryAfter is { } after && after > TimeSpan.Zero ? after : TimeSpan.FromSeconds(Math.Max(1, O.BackoffSeconds)));
            throw;
        }
        finally
        {
            job.CallEnded();
            slots.Release();
        }
    }

    public bool IsRunning(string key)
    {
        lock (sync)
        {
            return keyed.ContainsKey(key);
        }
    }

    /// <summary>The queue now, for <i>System health → Outbound</i> (§6.7).</summary>
    public RedmineQueueState QueueState()
    {
        List<RedmineJob> jobs;
        lock (sync)
        {
            jobs = [.. all.Values];
        }
        int Queued(RedmineLane lane) => jobs.Count(j => j.StartedAt is null && j.Lane == lane);
        return new RedmineQueueState(Queued(RedmineLane.Button), Queued(RedmineLane.Write), Queued(RedmineLane.Interactive), Queued(RedmineLane.Background),
            jobs.Count(j => j.StartedAt is not null), slots.InUse, Math.Clamp(O.Parallelism, 1, 32),
            slots.PausedUntil > Clock.UtcNow ? slots.PausedUntil : null,
            jobs.OrderBy(j => j.Lane).ThenBy(j => j.Seq).Select(j => new RedmineJobItem(j.Id, j.Key, j.Purpose, j.AskedBy, j.Lane, j.StartedAt is not null,
                j.QueuedAt, j.StartedAt, j.CallsRunning, j.CallsDone)).ToList());
    }

    // --- snapshots -------------------------------------------------------------------------------------------------------

    internal RedmineSnapshot Define(string key, string purpose, Func<RedmineCalls, Task<object>> load, bool pinned = false, TimeSpan? maxAge = null) =>
        snapshots.GetOrAdd(key, _ => new RedmineSnapshot(key, purpose, load, pinned, maxAge));

    internal bool TryGetSnapshot(string key, out RedmineSnapshot snapshot) => snapshots.TryGetValue(key, out snapshot!);

    private TimeSpan MaxAge(RedmineSnapshot s) => s.MaxAge ?? RefreshInterval;

    /// <summary>
    /// A snapshot's value at once, however old (§4.2), and a refresh queued if it is old or due; without one, the caller waits
    /// for the job, at most <c>Redmine__WaitSeconds</c> (<paramref name="wait"/> for the global search), then 504.
    /// </summary>
    internal async Task<(T Value, RedmineFreshness Freshness)> ReadAsync<T>(RedmineSnapshot s, RedmineLane lane, string askedBy, CancellationToken ct,
        TimeSpan? wait = null)
    {
        var now = Clock.UtcNow;
        s.RequestedAt = now;
        if (s.Value is T value)
        {
            if ((s.DueSince is not null || now - s.RefreshedAt >= MaxAge(s)) && now >= s.NextAttemptAt)
            {
                Observe(RefreshAsync(s, RedmineLane.Interactive, askedBy));
            }
            return (value, Freshness(s));
        }
        var task = RefreshAsync(s, lane, askedBy);
        try
        {
            await task.WaitAsync(wait ?? WaitTime, ct);
        }
        catch (TimeoutException)
        {
            Observe(task);
            throw new ApiException(StatusCodes.Status504GatewayTimeout, "redmine_busy", "Redmine is slow; the data will be here when it answers. Try again in a moment.");
        }
        return ((T)s.Value!, Freshness(s));
    }

    /// <summary>Reads the snapshot again through the queue; the old value stays until the new one is in, and stays on failure (§3.7).</summary>
    internal Task<object> RefreshAsync(RedmineSnapshot s, RedmineLane lane, string askedBy, bool fresh = false) =>
        RunAsync(new RedmineJobSpec(s.Key, lane, s.Purpose, askedBy, Fresh: fresh), async calls =>
        {
            var started = Clock.UtcNow;
            try
            {
                var value = await s.Load(calls);
                lock (s)
                {
                    s.Value = value;
                    s.RefreshedAt = Clock.UtcNow;
                    s.FailedAt = null;
                    s.Error = null;
                    s.Failures = 0;
                    s.NextAttemptAt = default;
                    if (s.DueSince is { } due && due <= started)
                    {
                        s.DueSince = null;
                    }
                }
                return value;
            }
            catch (Exception e) when (e is RedmineException or ApiException)
            {
                lock (s)
                {
                    s.FailedAt = Clock.UtcNow;
                    s.Error = e.Message;
                    s.Failures++;
                    s.NextAttemptAt = s.FailedAt.Value + Backoff(s.Failures);
                }
                throw;
            }
        });

    /// <summary>A value read by another job (the overview reads the stale tickets), kept as if its own job had read it.</summary>
    internal void Store(RedmineSnapshot s, object value)
    {
        lock (s)
        {
            s.Value = value;
            s.RefreshedAt = Clock.UtcNow;
            s.FailedAt = null;
            s.Error = null;
            if (s.RequestedAt == default)
            {
                s.RequestedAt = Clock.UtcNow;
            }
        }
    }

    internal RedmineFreshness Freshness(RedmineSnapshot s)
    {
        var failed = s.FailedAt is { } f && (s.RefreshedAt is null || f > s.RefreshedAt) ? s.FailedAt : null;
        return new RedmineFreshness(s.RefreshedAt, IsRunning(s.Key), failed, failed is null ? null : s.Error);
    }

    private static TimeSpan Backoff(int failures) => TimeSpan.FromMinutes(failures switch { <= 1 => 1, 2 => 2, 3 => 5, _ => 10 });

    /// <summary>
    /// After Cookie wrote to Redmine (§3.8): the views that may show it are due, and a ticket PNMT keeps a snapshot of is read
    /// again at once, in the write's lane.
    /// </summary>
    public void AfterWrite(int? issueId, RedmineLane lane)
    {
        var now = Clock.UtcNow;
        foreach (var s in snapshots.Values.Where(s => s.Key.StartsWith("overview:", StringComparison.Ordinal)
            || s.Key.StartsWith("stale:", StringComparison.Ordinal) || s.Key.StartsWith("search:", StringComparison.Ordinal)))
        {
            s.DueSince ??= now;
        }
        if (issueId is { } id && snapshots.TryGetValue(IssueKey(id), out var issue))
        {
            Observe(RefreshAsync(issue, lane, "PNMT", fresh: true));
        }
        Nudge();
    }

    /// <summary>A link added or removed: searches with the <i>linked</i> filter are due (§3.8); link counts are read at every request anyway.</summary>
    public void AfterLinkChange()
    {
        var now = Clock.UtcNow;
        foreach (var s in snapshots.Values.Where(s => s.Key.StartsWith("search:", StringComparison.Ordinal) && s.Key.Contains("|linked=True|", StringComparison.Ordinal)))
        {
            s.DueSince ??= now;
        }
        Nudge();
    }

    public void Nudge()
    {
        if (nudge.CurrentCount == 0)
        {
            nudge.Release();
        }
    }

    private void Observe(Task task) =>
        task.ContinueWith(t => logger.LogDebug(t.Exception?.GetBaseException(), "A Redmine refresh failed"), TaskContinuationOptions.OnlyOnFaulted);

    // --- the scheduler -----------------------------------------------------------------------------------------------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, stopping.Token);
        // The linked copy follows the warm-up after 30 s, as RedmineSync did.
        var nextSync = Clock.UtcNow.AddSeconds(30);
        while (!linked.IsCancellationRequested)
        {
            try
            {
                if (O.Enabled)
                {
                    Tick();
                    if (Clock.UtcNow >= nextSync)
                    {
                        nextSync = Clock.UtcNow.AddMinutes(Math.Max(1, O.SyncMinutes));
                        Observe(SyncAsync(linked.Token));
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "The Redmine worker's tick failed");
            }
            try
            {
                await nudge.WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// One pass (§4.3): watched snapshots older than <c>RefreshSeconds</c> or due are refreshed in the background lane, the
    /// lookups hourly, the overview of all projects every <c>SyncMinutes</c> while nobody watches it; the rest is removed
    /// <c>KeepMinutes</c> after its last request, and beyond <c>MaxSnapshots</c>.
    /// </summary>
    internal void Tick()
    {
        var o = O;
        var now = Clock.UtcNow;
        Pinned();
        var watchFor = TimeSpan.FromMinutes(Math.Max(1, o.WatchMinutes));
        var keepFor = TimeSpan.FromMinutes(Math.Max(1, o.KeepMinutes));
        foreach (var s in snapshots.Values)
        {
            var watched = now - s.RequestedAt < watchFor;
            if (!s.Pinned && !watched)
            {
                if (now - s.RequestedAt > keepFor && !IsRunning(s.Key))
                {
                    snapshots.TryRemove(s.Key, out _);
                }
                continue;
            }
            if (IsRunning(s.Key) || now < s.NextAttemptAt)
            {
                continue;
            }
            var age = s.RefreshedAt is { } at ? now - at : TimeSpan.MaxValue;
            var interval = s.Key == OverviewKey(null) && !watched ? TimeSpan.FromMinutes(Math.Max(1, o.SyncMinutes)) : MaxAge(s);
            if (age >= interval || (watched && s.DueSince is not null))
            {
                Observe(RefreshAsync(s, RedmineLane.Background, "PNMT"));
            }
        }
        var excess = snapshots.Count - Math.Max(10, o.MaxSnapshots);
        if (excess > 0)
        {
            foreach (var s in snapshots.Values.Where(s => !s.Pinned && !IsRunning(s.Key)).OrderBy(s => s.RequestedAt).Take(excess).ToList())
            {
                snapshots.TryRemove(s.Key, out _);
            }
        }
        foreach (var (key, change) in lastChanges)
        {
            if (now - change.ReadAt > TimeSpan.FromDays(1))
            {
                lastChanges.TryRemove(key, out _);
            }
        }
        slots.Pump();
    }

    // --- the linked copy (docs/audit-redmine-zabbix-plan.md §6.7) ------------------------------------------------------------

    /// <summary>
    /// Every <c>SyncMinutes</c>, through the queue: the lookups read again, PNMT's copy of linked tickets refreshed, a hundred a
    /// call; tickets Redmine no longer returns marked missing; copies without links deleted. Two failures in a row fail the
    /// Redmine system sensor (429); the next success clears it (220).
    /// </summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        try
        {
            var notFound = await RunAsync(new RedmineJobSpec("sync", RedmineLane.Background, "linked copy", "PNMT"), async calls =>
            {
                var lookups = (RedmineLookups)await RefreshAsync(LookupsSnapshot, calls.Job.Lane, "PNMT");
                await using var db = await dbs.CreateDbContextAsync(ct);
                var linked = await db.RedmineLinks.AsNoTracking().Select(l => l.IssueId).Distinct().ToListAsync(ct);
                var seen = new HashSet<int>();
                foreach (var batch in linked.Chunk(100))
                {
                    var page = await calls.GetAsync<RmIssues>($"/issues.json?issue_id={string.Join(",", batch)}&status_id=*&limit=100", "linked tickets");
                    var allowed = page.Issues.Where(i => lookups.Allowed.Contains(i.Project.Id)).ToList();
                    await RedmineService.UpsertCopyAsync(db, allowed, ct);
                    seen.UnionWith(allowed.Select(i => i.Id));
                }
                var gone = linked.Where(id => !seen.Contains(id)).ToList();
                await db.RedmineIssues.Where(i => gone.Contains(i.IssueId)).ExecuteUpdateAsync(u => u.SetProperty(i => i.Missing, true), ct);
                await db.RedmineIssues.Where(i => !db.RedmineLinks.Any(l => l.IssueId == i.IssueId)).ExecuteDeleteAsync(ct);
                await db.SaveChangesAsync(ct);
                db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_REDMINE_SYNC);
                return lookups.NotFound;
            });
            if (notFound.Count > 0)
            {
                await FailAsync($"Not found in Redmine: {string.Join(", ", notFound)} (Redmine__Projects).", ct);
                return;
            }
            if (syncFailures > 0 || (await systemSensors.LastAsync(SystemSensorKeys.Redmine, ct))?.Code == PNMTStatusCodes.REDMINE_FAILING)
            {
                await systemSensors.WriteAsync(SystemSensorKeys.Redmine, PNMTStatusCodes.SYSTEM_OK, "Redmine answers again.", ct);
            }
            syncFailures = 0;
        }
        catch (RedmineException e)
        {
            await FailAsync(e.Message, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Redmine's sync failed");
        }
    }

    private async Task FailAsync(string message, CancellationToken ct)
    {
        syncFailures++;
        logger.LogWarning("Redmine: {Message}", message);
        if (syncFailures == 2)
        {
            await systemSensors.WriteAsync(SystemSensorKeys.Redmine, PNMTStatusCodes.REDMINE_FAILING, message, ct);
        }
    }
}
