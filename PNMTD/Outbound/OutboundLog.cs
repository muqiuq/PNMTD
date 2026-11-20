using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Outbound;

/// <summary>An operation in flight (docs/redmine-queue-outbound-plan.md §6.7).</summary>
public sealed record RunningOperation(long Id, string Log, string Channel, string Method, string Target, string Purpose, string AskedBy, DateTime StartedAt);

/// <summary>
/// The two outbound logs (§6): <see cref="Record(OutboundNotificationEntity, string?, string?, string?)"/> never waits, it
/// queues the record (5 000 at most; beyond that a record is dropped and counted, R12). The writer stores what is queued
/// once a second, the bodies gzip-compressed and cut at <c>Outbound__MaxBodyKiB</c>, and every 100 records keeps each
/// table to its newest <c>Outbound__KeepNotifications</c> and <c>Outbound__KeepRedmine</c>.
/// </summary>
public sealed class OutboundLog(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<OutboundOptions> options, ILogger<OutboundLog> logger)
{
    public const string NotificationsLog = "notifications";
    public const string RedmineLog = "redmine";

    private const int QueueLength = 5_000;
    private const int TrimEvery = 100;

    private sealed record Pending(object Entity, string? Request, string? Response, string? Transcript);

    private readonly Channel<Pending> queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(QueueLength)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true,
    });
    private readonly SemaphoreSlim flushing = new(1, 1);
    private readonly ConcurrentDictionary<long, RunningOperation> running = new();
    private long runningIds;
    private long droppedNotifications;
    private long droppedRedmine;
    private int notificationsSinceTrim;
    private int redmineSinceTrim;

    public long DroppedNotifications => Interlocked.Read(ref droppedNotifications);

    public long DroppedRedmine => Interlocked.Read(ref droppedRedmine);

    public IReadOnlyList<RunningOperation> Running => running.Values.OrderBy(r => r.StartedAt).ToList();

    /// <summary>An operation starts: shown under <i>Running now</i> until <see cref="End"/>.</summary>
    public long Begin(string log, string channel, string method, string target, string purpose, string askedBy)
    {
        var id = Interlocked.Increment(ref runningIds);
        running[id] = new RunningOperation(id, log, channel, method, target, purpose, askedBy, Clock.UtcNow);
        return id;
    }

    public void End(long id) => running.TryRemove(id, out _);

    public void Record(OutboundNotificationEntity entry, string? request, string? response, string? transcript = null)
    {
        Fit(entry);
        if (!queue.Writer.TryWrite(new Pending(entry, request, response, transcript)))
        {
            Interlocked.Increment(ref droppedNotifications);
        }
    }

    public void Record(RedmineCallEntity entry, string? request, string? response)
    {
        Fit(entry);
        if (!queue.Writer.TryWrite(new Pending(entry, request, response, null)))
        {
            Interlocked.Increment(ref droppedRedmine);
        }
    }

    /// <summary>The writer's loop (<see cref="OutboundLogWriter"/>): what is queued, once a second.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await queue.Reader.WaitToReadAsync(ct))
                {
                    return;
                }
                await FlushAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "The outbound log could not be written");
            }
        }
    }

    /// <summary>Stores everything queued now. The tests call it to see their records without waiting.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await flushing.WaitAsync(ct);
        try
        {
            var batch = new List<Pending>();
            while (queue.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }
            if (batch.Count == 0)
            {
                return;
            }
            var max = options.CurrentValue.MaxBodyBytes;
            await using var db = await dbs.CreateDbContextAsync(ct);
            int notifications = 0, calls = 0;
            foreach (var p in batch)
            {
                var (request, requestBytes, cutRequest) = Gzip(p.Request, max);
                var (response, responseBytes, cutResponse) = Gzip(p.Response, max);
                switch (p.Entity)
                {
                    case OutboundNotificationEntity n:
                        n.RequestBody = request;
                        n.ResponseBody = response;
                        n.RequestBytes = requestBytes;
                        n.ResponseBytes = responseBytes;
                        n.Truncated = cutRequest || cutResponse;
                        n.Transcript = Gzip(p.Transcript, max).Bytes;
                        db.OutboundNotifications.Add(n);
                        notifications++;
                        break;
                    case RedmineCallEntity c:
                        c.RequestBody = request;
                        c.ResponseBody = response;
                        c.RequestBytes = requestBytes;
                        c.ResponseBytes = responseBytes;
                        c.Truncated = cutRequest || cutResponse;
                        db.RedmineCalls.Add(c);
                        calls++;
                        break;
                }
            }
            await db.SaveChangesAsync(ct);
            notificationsSinceTrim += notifications;
            redmineSinceTrim += calls;
            if (notificationsSinceTrim >= TrimEvery)
            {
                notificationsSinceTrim = 0;
                await TrimAsync(db, NotificationsLog, ct);
            }
            if (redmineSinceTrim >= TrimEvery)
            {
                redmineSinceTrim = 0;
                await TrimAsync(db, RedmineLog, ct);
            }
        }
        finally
        {
            flushing.Release();
        }
    }

    /// <summary>Keeps a log's newest records, by <c>Id</c>, as the AI's request log does (<c>AiRequestLog.TrimAsync</c>).</summary>
    public async Task TrimAsync(PnmtdDbContext db, string log, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var (table, keep) = log == RedmineLog ? ("redmine_calls", o.KeepRedmine) : ("outbound_notifications", o.KeepNotifications);
        keep = Math.Max(1, keep);
        // The table's name is one of two constants; the number is a parameter.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync(
            $$"""DELETE FROM {{table}} WHERE "Id" < (SELECT "Id" FROM {{table}} ORDER BY "Id" DESC OFFSET {0} LIMIT 1)""", [keep - 1], ct);
#pragma warning restore EF1002
    }

    /// <summary>The text gzip-compressed, cut at <paramref name="max"/> bytes; its full size in bytes, and whether it was cut.</summary>
    public static (byte[]? Bytes, long Size, bool Truncated) Gzip(string? text, int max)
    {
        if (text is null)
        {
            return (null, 0, false);
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        var truncated = bytes.Length > max;
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes, 0, truncated ? max : bytes.Length);
        }
        return (buffer.ToArray(), bytes.Length, truncated);
    }

    public static string? Gunzip(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        // A body cut inside a multi-byte character ends in a replacement character, which is what it is.
        return reader.ReadToEnd();
    }

    public static string Cut(string? text, int max) => text is null ? "" : text.Length <= max ? text : text[..(max - 1)] + "…";

    private static void Fit(OutboundNotificationEntity e)
    {
        e.Method = Cut(e.Method, 16);
        e.Target = Cut(e.Target, 2000);
        e.Recipient = e.Recipient is null ? null : Cut(e.Recipient, 2000);
        e.Summary = Cut(e.Summary, 500);
        e.Purpose = Cut(e.Purpose, 500);
        e.AskedBy = Cut(e.AskedBy, 200);
        e.Error = e.Error is null ? null : Cut(e.Error, 1000);
    }

    private static void Fit(RedmineCallEntity e)
    {
        e.Method = Cut(e.Method, 16);
        e.Target = Cut(e.Target, 4000);
        e.Summary = Cut(e.Summary, 500);
        e.Purpose = Cut(e.Purpose, 500);
        e.AskedBy = Cut(e.AskedBy, 200);
        e.Error = e.Error is null ? null : Cut(e.Error, 1000);
    }
}

/// <summary>The outbound logs' writer (§6.4): on in any case, not only with the monitoring tasks, so the tests record too.</summary>
public sealed class OutboundLogWriter(OutboundLog log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await log.RunAsync(stoppingToken);
        // What is still queued at shutdown is stored if the database lets it.
        try
        {
            using var last = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await log.FlushAsync(last.Token);
        }
        catch (Exception)
        {
            // Shutting down: what can't be stored now is lost, as a dropped record would be.
        }
    }
}
