using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Api.V1;

/// <summary>The audit log (docs/audit-redmine-zabbix-plan.md §4.2). It was the auth log until 1.4.</summary>
[Route("api/v1/audit-log")]
[Authorize]
public sealed class AuditLogController(PnmtdDbContext db) : V1ControllerBase
{
    [HttpGet]
    [RequireRight(Area.AuditLog, Level.View)]
    public async Task<ActionResult<Paged<AuditLogItem>>> List([FromQuery] AuditLogQuery query, CancellationToken ct)
    {
        var log = Filter(query);
        var page = await Services.Domain.Paging.PageAsync(log.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id), query, ct);
        // A sign-in the access watch reviewed (docs/cookie-access-plan.md §10.4): its verdict and case, for the badge.
        var ids = page.Items.Select(l => l.Id).ToList();
        var reviews = (await db.AccessReviews.AsNoTracking().Where(r => r.AuditLogId != null && ids.Contains(r.AuditLogId.Value))
            .Select(r => new { Id = r.AuditLogId!.Value, ReviewId = r.Id, r.CaseId, r.State }).ToListAsync(ct))
            .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        return new Paged<AuditLogItem>(page.Items.Select(l => ToItem(l) with
        {
            Review = reviews.TryGetValue(l.Id, out var r) ? new AccessReviewRef(r.ReviewId, r.CaseId, r.State) : null,
        }).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>
    /// What the filters show, as CSV (§4.2): at most 100,000 entries, newest first, streamed. The export is an entry itself.
    /// </summary>
    [HttpGet("export")]
    [RequireRight(Area.AuditLog, Level.View)]
    [Audit(AuditEvents.AuditLogExported)]
    public async Task Export([FromQuery] AuditLogQuery query, CancellationToken ct)
    {
        var log = Filter(query);
        AuditLog.Summary("filters: " + Describe(query));
        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"pnmt-audit-log-{Clock.UtcNow:yyyyMMdd-HHmm}.csv\"";
        await using var writer = new StreamWriter(Response.Body, new System.Text.UTF8Encoding(false));
        await writer.WriteLineAsync("time,user,actor,event,category,outcome,error,target type,target id,target,detail,address,request,correlation");
        await foreach (var l in log.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id).Take(MaxExport).AsAsyncEnumerable().WithCancellation(ct))
        {
            await writer.WriteLineAsync(string.Join(",", new[]
            {
                l.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture), l.Username, l.Actor.ToString(), l.EventType,
                l.Category.ToString(), l.Outcome.ToString(), l.Error, l.TargetType, l.TargetId, l.TargetName, l.Detail, l.IpAddress, l.Request, l.CorrelationId,
            }.Select(Csv)));
        }
    }

    public const int MaxExport = 100_000;

    /// <summary>
    /// An object's history (§4.3): the entries about it and what belongs to it (a host's sensors), for everyone who sees
    /// monitoring, without addresses and browsers, which stay on the audit log.
    /// </summary>
    [HttpGet("subjects/{id:guid}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<AuditLogItem>>> Subject(Guid id, [FromQuery] PageQuery query, CancellationToken ct)
    {
        var page = await Services.Domain.Paging.PageAsync(db.AuditLog.AsNoTracking().Where(l => l.Subjects.Contains(id))
            .OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id), query, ct);
        return new Paged<AuditLogItem>(page.Items.Select(l => ToItem(l) with { IpAddress = null, UserAgent = null }).ToList(), page.Page,
            page.PageSize, page.TotalCount);
    }

    private IQueryable<Models.Db.AuditLogEntity> Filter(AuditLogQuery query)
    {
        new ApiValidator()
            .Check(query.Type is null || AuditCatalog.Contains(query.Type), "type", "One of the types of GET /api/v1/audit-log/types.")
            .ThrowIfInvalid();
        var log = db.AuditLog.AsNoTracking();
        if (query.UserId is { } userId)
        {
            log = log.Where(l => l.UserId == userId || l.Subjects.Contains(userId));
        }
        if (query.Type is { } type)
        {
            log = log.Where(l => l.EventType == type);
        }
        if (query.Categories is { Count: > 0 } categories)
        {
            log = log.Where(l => categories.Contains(l.Category));
        }
        if (query.Outcome is { } outcome)
        {
            log = log.Where(l => l.Outcome == outcome);
        }
        if (query.Actor is { } actor)
        {
            log = log.Where(l => l.Actor == actor);
        }
        if (query.Subject is { } subject)
        {
            log = log.Where(l => l.Subjects.Contains(subject));
        }
        if (query.From is { } from)
        {
            var since = Clock.AsUtc(from);
            log = log.Where(l => l.CreatedAt >= since);
        }
        if (query.To is { } to)
        {
            var until = Clock.AsUtc(to);
            log = log.Where(l => l.CreatedAt < until);
        }
        if (query.CorrelationId is { Length: > 0 } correlation)
        {
            log = log.Where(l => l.CorrelationId == correlation);
        }
        if (Services.Domain.Text.Pattern(query.Search) is { } pattern)
        {
            log = log.Where(l => EF.Functions.ILike(l.Username, pattern, @"\") || (l.Detail != null && EF.Functions.ILike(l.Detail, pattern, @"\"))
                || (l.TargetName != null && EF.Functions.ILike(l.TargetName, pattern, @"\")));
        }
        return log;
    }

    private static string Describe(AuditLogQuery q)
    {
        var parts = new List<string>();
        void Add(string name, object? value)
        {
            if (value is not null && value.ToString() is { Length: > 0 } text)
            {
                parts.Add($"{name} {text}");
            }
        }
        Add("search", q.Search);
        Add("user", q.UserId);
        Add("type", q.Type);
        Add("categories", q.Categories is { Count: > 0 } c ? string.Join("/", c) : null);
        Add("outcome", q.Outcome);
        Add("actor", q.Actor);
        Add("subject", q.Subject);
        Add("from", q.From?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        Add("to", q.To?.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>A CSV field: quoted when it must be, and never a formula a spreadsheet would run.</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>The catalog (§3.9): every event type with its category, label and tone.</summary>
    [HttpGet("types")]
    [RequireRight(Area.AuditLog, Level.View)]
    public IReadOnlyList<AuditEventTypeItem> Types() =>
        AuditCatalog.All.Select(i => new AuditEventTypeItem(i.Type, i.Category, i.Label, i.Tone, i.TargetType)).ToList();

    internal static AuditLogItem ToItem(Models.Db.AuditLogEntity l) => new(l.Id, l.CreatedAt, l.UserId, l.Username, l.EventType, l.Category,
        l.Actor, l.ActorRef, l.Outcome, l.Error, l.IpAddress, l.UserAgent, l.Detail, l.TargetType, l.TargetId, l.TargetName, l.Request,
        l.CorrelationId, l.Changes is null ? [] : JsonSerializer.Deserialize<List<AuditChange>>(l.Changes, AuditLog.Json) ?? []);
}
