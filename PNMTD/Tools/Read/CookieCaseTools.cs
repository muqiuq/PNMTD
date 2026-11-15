using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>Cookie's cases (docs/cookie-plan.md §4): Cookie reads its own; a person over MCP needs <i>Cookie: Use</i> and sees what their rights cover.</summary>
public static class CookieCaseTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("cases_list", "Cookie's cases", "The open cases you may see: id, kind, title, importance, when opened, last change, last progress, the next deadline, Cookie's note.",
            Schema(Bool("closed", "true for the cases closed in the last 7 days instead")), null, Level.None, ToolKind.Read, CasesAsync)
        {
            ForCookie = true,
            McpRequires = [(Area.Cookie, Level.Use)],
        },
        new("case_get", "One of Cookie's cases", "One case: its members (bad sensors, Zabbix problems), timeline, deadlines, the ticket's clock, Cookie's last note.",
            Schema(Id("id", "The case's id")), null, Level.None, ToolKind.Read, CaseAsync)
        {
            ForCookie = true,
            McpRequires = [(Area.Cookie, Level.Use)],
        },
    ];

    private static async Task<object> CasesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var closed = OptionalBool(args, "closed") == true;
        var since = Clock.UtcNow.AddDays(-7);
        var cases = await db.CookieCases.AsNoTracking().Where(x => closed ? !x.Open && x.ClosedAt >= since : x.Open)
            .OrderByDescending(x => x.Importance).ThenByDescending(x => x.ChangedAt).Take(200).ToListAsync(ct);
        var visible = cases.Where(x => x.Kind != CookieCaseKind.Request && CookieAccess.CanSee(ctx.Rights, x.Areas)).Take(50).ToList();
        var ids = visible.Select(x => x.Id).ToList();
        var next = (await db.CookieDuties.AsNoTracking().Where(d => ids.Contains(d.CaseId) && d.State == CookieDutyState.Due).ToListAsync(ct))
            .GroupBy(d => d.CaseId).ToDictionary(g => g.Key, g => g.OrderBy(d => d.DueAt).First());
        foreach (var x in visible)
        {
            ctx.Areas.UnionWith(x.Areas);
        }
        ctx.Summary = $"{visible.Count} {(closed ? "closed" : "open")} cases";
        return visible.Select(x => new
        {
            x.Id, x.Kind, Title = ctx.Privacy.Mask(x.Title), x.Importance, x.OpenedAt, x.ChangedAt, x.ProgressAt,
            NextDeadline = next.TryGetValue(x.Id, out var d) ? $"{d.Kind} at {d.DueAt:u}" : null,
            YourNote = ctx.Privacy.Mask(x.CookieNote),
            Url = ctx.Link($"cookie/cases?case={x.Id}"),
        }).ToList();
    }

    private static async Task<object> CaseAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var x = await db.CookieCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (x is null || x.Kind == CookieCaseKind.Request || !CookieAccess.CanSee(ctx.Rights, x.Areas))
        {
            throw ToolException.NotFound("case");
        }
        ctx.Areas.UnionWith(x.Areas);
        ctx.Target("CookieCase", x.Id, x.Title);
        var members = JsonSerializer.Deserialize<List<CaseMember>>(x.Members, CookieSettings.Json) ?? [];
        var timeline = await db.CookieCaseEntries.AsNoTracking().Where(e => e.CaseId == id).OrderByDescending(e => e.At).Take(40).ToListAsync(ct);
        var duties = await db.CookieDuties.AsNoTracking().Where(d => d.CaseId == id).OrderBy(d => d.DueAt).Take(20).ToListAsync(ct);
        return new
        {
            x.Id, x.Kind, Title = ctx.Privacy.Mask(x.Title), x.Open, x.Importance, x.HostId, x.LocationId, x.ZabbixHostId, x.OpenedAt, x.ChangedAt, x.ProgressAt,
            Members = members.Select(m => new
            {
                m.Type, m.Id, Name = ctx.Privacy.Mask(m.Name), m.Since, m.Code, m.Severity, m.Acknowledged,
                Message = m.Message is null ? null : ctx.Untrusted(m.Type == CaseMember.Zabbix ? "Zabbix" : "sensor message", m.Message, 300),
            }),
            Timeline = timeline.OrderBy(e => e.At).Select(e => new { e.At, e.Kind, Text = ctx.Privacy.Mask(e.Text), e.Actor }),
            Deadlines = duties.Select(d => new { d.Kind, d.DueAt, d.State, Outcome = ctx.Privacy.Mask(d.Outcome) }),
            Ticket = x.IssueId is null ? null : new
            {
                Number = x.IssueId, Author = ctx.Privacy.Mask(x.AuthorName), x.AuthorIsStaff, Assignee = ctx.Privacy.Mask(x.AssigneeName), Status = x.StatusName,
                Created = x.TicketCreatedAt, x.FirstAnswerAt, x.LastStaffUpdateAt,
            },
            OwnTicket = x.OwnIssueId,
            YourNote = ctx.Privacy.Mask(x.CookieNote),
            Url = ctx.Link($"cookie/cases?case={x.Id}"),
        };
    }
}
