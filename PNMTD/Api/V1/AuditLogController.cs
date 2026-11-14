using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Api.V1;

/// <summary>The audit log (docs/audit-redmine-zabbix-plan.md §4.2). It was the auth log until 1.4.</summary>
[Route("api/v1/audit-log")]
[Authorize(Policy = Policies.Admin)]
public sealed class AuditLogController(PnmtdDbContext db) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<AuditLogItem>>> List([FromQuery] AuditLogQuery query, CancellationToken ct)
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
        var page = await Services.Domain.Paging.PageAsync(log.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id), query, ct);
        return new Paged<AuditLogItem>(page.Items.Select(ToItem).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>The catalog (§3.9): every event type with its category, label and tone.</summary>
    [HttpGet("types")]
    public IReadOnlyList<AuditEventTypeItem> Types() =>
        AuditCatalog.All.Select(i => new AuditEventTypeItem(i.Type, i.Category, i.Label, i.Tone, i.TargetType)).ToList();

    internal static AuditLogItem ToItem(Models.Db.AuditLogEntity l) => new(l.Id, l.CreatedAt, l.UserId, l.Username, l.EventType, l.Category,
        l.Actor, l.ActorRef, l.Outcome, l.Error, l.IpAddress, l.UserAgent, l.Detail, l.TargetType, l.TargetId, l.TargetName, l.Request,
        l.CorrelationId, l.Changes is null ? [] : JsonSerializer.Deserialize<List<AuditChange>>(l.Changes, AuditLog.Json) ?? []);
}
