using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Services.Domain;

namespace PNMTD.Outbound;

/// <summary>The two logs for <i>System health → Outbound</i> (docs/redmine-queue-outbound-plan.md §7, §8): lists, a record in full, what runs now.</summary>
public sealed class OutboundQueries(PnmtdDbContext db, OutboundLog log, RedmineWorker redmine)
{
    public async Task<Paged<OutboundNotificationItem>> NotificationsAsync(OutboundNotificationQuery query, CancellationToken ct)
    {
        var q = db.OutboundNotifications.AsNoTracking();
        if (query.Channel is { } channel)
        {
            q = q.Where(n => n.Channel == channel);
        }
        if (query.Outcome is { } outcome)
        {
            q = q.Where(n => n.Outcome == outcome);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            q = q.Where(n => EF.Functions.ILike(n.Target, pattern) || EF.Functions.ILike(n.Recipient ?? "", pattern) || EF.Functions.ILike(n.Summary, pattern)
                || EF.Functions.ILike(n.Purpose, pattern) || EF.Functions.ILike(n.AskedBy, pattern));
        }
        var (page, size) = Page(query);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(n => n.Id).Skip((page - 1) * size).Take(size).Select(ItemOf).ToListAsync(ct);
        return new Paged<OutboundNotificationItem>(items, page, size, total);
    }

    public async Task<Paged<RedmineCallItem>> RedmineAsync(RedmineCallQuery query, CancellationToken ct)
    {
        var q = db.RedmineCalls.AsNoTracking();
        if (query.Kind is { } kind)
        {
            q = q.Where(c => c.Kind == kind);
        }
        if (query.Lane is { } lane)
        {
            q = q.Where(c => c.Lane == lane);
        }
        if (query.Outcome is { } outcome)
        {
            q = q.Where(c => c.Outcome == outcome);
        }
        if (query.Job is { } job)
        {
            q = q.Where(c => c.JobId == job);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            q = q.Where(c => EF.Functions.ILike(c.Target, pattern) || EF.Functions.ILike(c.Summary, pattern) || EF.Functions.ILike(c.Purpose, pattern)
                || EF.Functions.ILike(c.AskedBy, pattern));
        }
        var (page, size) = Page(query);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(c => c.Id).Skip((page - 1) * size).Take(size).Select(CallOf).ToListAsync(ct);
        return new Paged<RedmineCallItem>(items, page, size, total);
    }

    public async Task<OutboundDetail> NotificationAsync(long id, CancellationToken ct)
    {
        var n = await db.OutboundNotifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Notification");
        return new OutboundDetail(ItemOfCompiled(n), null, Headers(n.RequestHeaders), OutboundLog.Gunzip(n.RequestBody), Headers(n.ResponseHeaders),
            OutboundLog.Gunzip(n.ResponseBody), OutboundLog.Gunzip(n.Transcript));
    }

    public async Task<OutboundDetail> RedmineCallAsync(long id, CancellationToken ct)
    {
        var c = await db.RedmineCalls.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Redmine call");
        return new OutboundDetail(null, CallOfCompiled(c), Headers(c.RequestHeaders), OutboundLog.Gunzip(c.RequestBody), Headers(c.ResponseHeaders),
            OutboundLog.Gunzip(c.ResponseBody), null);
    }

    public async Task<OutboundLive> LiveAsync(CancellationToken ct)
    {
        var newestNotification = await db.OutboundNotifications.AsNoTracking().MaxAsync(n => (long?)n.Id, ct) ?? 0;
        var newestCall = await db.RedmineCalls.AsNoTracking().MaxAsync(c => (long?)c.Id, ct) ?? 0;
        var running = log.Running.Select(r => new OutboundRunningItem(r.Log, r.Channel, r.Method, r.Target, r.Purpose, r.AskedBy, r.StartedAt)).ToList();
        return new OutboundLive(running, redmine.QueueState(), newestNotification, newestCall, log.DroppedNotifications, log.DroppedRedmine);
    }

    private static (int Page, int Size) Page(PageQuery query) => (Math.Max(1, query.Page), Math.Clamp(query.PageSize, 1, PageQuery.MaxPageSize));

    private static IReadOnlyList<OutboundHeader> Headers(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string[]>>(json) ?? []).Where(h => h.Length == 2).Select(h => new OutboundHeader(h[0], h[1])).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly System.Linq.Expressions.Expression<Func<OutboundNotificationEntity, OutboundNotificationItem>> ItemOf = n =>
        new OutboundNotificationItem(n.Id, n.StartedAt, n.DurationMs, n.Channel, n.Method, n.Target, n.Recipient, n.Summary, n.Purpose, n.AskedBy, n.Outcome,
            n.StatusCode, n.Error, n.RequestBytes, n.ResponseBytes, n.Truncated);

    private static readonly System.Linq.Expressions.Expression<Func<RedmineCallEntity, RedmineCallItem>> CallOf = c =>
        new RedmineCallItem(c.Id, c.StartedAt, c.DurationMs, c.QueuedMs, c.Kind, c.Method, c.Target, c.Summary, c.Purpose, c.AskedBy, c.JobId, c.Lane,
            c.Outcome, c.StatusCode, c.Error, c.RequestBytes, c.ResponseBytes, c.Truncated);

    private static readonly Func<OutboundNotificationEntity, OutboundNotificationItem> ItemOfCompiled = ItemOf.Compile();

    private static readonly Func<RedmineCallEntity, RedmineCallItem> CallOfCompiled = CallOf.Compile();
}
