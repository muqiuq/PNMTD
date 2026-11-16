using System.Text.Json;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Redmine;
using PNMTD.Zabbix;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// Redmine, Zabbix and the availability plan (docs/mcp-plan.md §7.2), always through <see cref="RedmineService"/> and
/// <see cref="ZabbixService"/>, so the projects' and groups' allowlists apply (docs/audit-redmine-zabbix-plan.md §8.7).
/// </summary>
public static class IntegrationReadTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("ticket_get", "A Redmine ticket", "A Redmine ticket of the projects PNMT shows: header, description and its journals.",
            Schema(Int("number", "The ticket's number", true)), Area.Redmine, Level.View, ToolKind.Read, TicketAsync)
        {
            ForCookie = true, OpenWorld = true, Available = RedmineReadable,
        },
        new("zabbix_problems", "Zabbix's problems",
            "Zabbix's open problems, the most severe first, for one Zabbix host or all: name, severity, since, acknowledged, host.",
            Schema(Int("zabbixHostId", "One Zabbix host's id; all when left out")), Area.Zabbix, Level.View, ToolKind.Read, ZabbixAsync)
        {
            ForCookie = true, Available = ZabbixAvailable,
        },
        new("availability_get", "The availability plan",
            "The availability plan for some days (at most 14): who is responsible, who works and where (on site, work travel, home office), who is away " +
            "and why, the counts below their minimum, the day's comment.",
            Schema(Text("from", "The first day, yyyy-MM-dd; today when left out"), Int("days", "How many days, 1 to 14; 1 when left out")),
            Area.Availability, Level.View, ToolKind.Read, AvailabilityAsync) { ForCookie = true },
        new("tickets_overview", "Redmine's overview",
            "The Redmine overview: the open tickets that need attention, as urgent, new and unassigned, overdue, linked to a problem in PNMT, waiting " +
            "for a long time, recently updated.",
            Schema(Int("project", "One project's id; all of PNMT's projects when left out")), Area.Redmine, Level.View, ToolKind.Read, OverviewAsync) { OpenWorld = true },
        new("tickets_search", "Search Redmine",
            "Redmine tickets of the projects PNMT shows, by text (subject, description, notes), status (open, closed, all), assignee, overdue, or only " +
            "those linked in PNMT. Paged.",
            Schema([
                Text("query", "Words to look for"), Choice("status", "open (when left out), closed or all", ["open", "closed", "all"]),
                Text("assignee", "A Redmine user's id, or none"), Bool("overdue", "Only overdue tickets"), Bool("linked", "Only tickets linked in PNMT"),
                Int("project", "One project's id"), .. PageArgs,
            ]), Area.Redmine, Level.View, ToolKind.Read, TicketsAsync) { OpenWorld = true },
        new("zabbix_summary", "Zabbix's summary", "Zabbix's open problems by severity and by host group, and its hosts by availability.",
            Schema(), Area.Zabbix, Level.View, ToolKind.Read, ZabbixSummaryAsync),
        new("zabbix_hosts_search", "Find Zabbix hosts",
            "Zabbix hosts by name or address, with their problems, availability and the PNMT host they are attached to. Paged.",
            Schema([Text("query", "Part of the name or address"), Text("group", "Only this host group"), Bool("attached", "Only hosts attached to PNMT (true) or not (false)"), .. PageArgs]),
            Area.Zabbix, Level.View, ToolKind.Read, ZabbixHostsAsync),
        new("zabbix_host_get", "A Zabbix host",
            "A Zabbix host: its groups, interfaces, availability, problems, and with Zabbix Details its inventory.",
            Schema(Int("id", "The Zabbix host's id", true)), Area.Zabbix, Level.View, ToolKind.Read, ZabbixHostAsync),
    ];

    /// <summary>Not offered while Redmine or Zabbix isn't available (docs/cookie-fallback-plan.md §4.3): Cookie's and MCP's lists agree.</summary>
    private static bool RedmineReadable(IServiceProvider services) => services.GetRequiredService<CookieIntegrations>().Current.RedmineReadable;

    private static bool ZabbixAvailable(IServiceProvider services) => services.GetRequiredService<CookieIntegrations>().Current.Zabbix;

    private static async Task<object> TicketAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var number = IntOf(args, "number");
        RedmineIssueDetail t;
        try
        {
            t = await ctx.Services.GetRequiredService<RedmineService>().IssueAsync(number, ct);
        }
        catch (Api.ApiException)
        {
            throw new ToolException("not_found", "No such ticket in the projects PNMT shows.");
        }
        ctx.Target("RedmineTicket", t.Id, $"#{t.Id}");
        return new
        {
            Number = t.Id, Project = t.Project.Name, t.Tracker, t.Status, t.IsClosed, t.Priority, Author = ctx.Privacy.Mask(t.Author),
            AssignedTo = ctx.Privacy.Mask(t.AssignedTo), Subject = ctx.Untrusted($"ticket #{t.Id}", t.Subject, 300), t.CreatedOn, t.UpdatedOn, t.DueDate,
            Description = t.Description is null ? null : ctx.Untrusted($"ticket #{t.Id}", t.Description.Text, 3000),
            Journals = t.Journals.TakeLast(15).Select(j => new
            {
                User = ctx.Privacy.Mask(j.User), j.CreatedOn, j.Private, Changes = j.Changes.Select(c => $"{c.Field}: {ctx.Privacy.Mask(c.Old)} → {ctx.Privacy.Mask(c.New)}"),
                Notes = j.Notes is null ? null : ctx.Untrusted($"ticket #{t.Id} note", j.Notes.Text, 1200),
            }),
            Url = ctx.Link($"redmine/issues/{t.Id}"),
        };
    }

    private static Task<object> ZabbixAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var snapshot = ctx.Services.GetRequiredService<ZabbixSnapshot>().Current;
        var host = OptionalLong(args, "zabbixHostId");
        var problems = snapshot.Problems.Where(p => host is null || p.Hosts.Any(x => ZabbixPoller.Int(x.Hostid) == host))
            .OrderByDescending(p => p.Severity).ThenByDescending(p => p.Since).Take(30)
            .Select(p => new
            {
                p.EventId, Name = ctx.Untrusted("Zabbix", p.Name, 300), Severity = p.Severity.ToString(), Since = Clock.AsUtc(p.Since), p.Acknowledged, p.Suppressed,
                Hosts = p.Hosts.Select(x => new { ZabbixHostId = ZabbixPoller.Int(x.Hostid), x.Name }),
            }).ToList();
        ctx.Summary = $"{problems.Count} Zabbix problems";
        return Task.FromResult<object>(new { snapshot.AsOf, snapshot.Error, Problems = problems });
    }

    private static async Task<object> AvailabilityAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var from = OptionalText(args, "from", 10) is { } f
            ? DateOnly.TryParseExact(f, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d
                : throw ToolException.Invalid("from must be a date as yyyy-MM-dd.")
            : DateOnly.FromDateTime(Clock.LocalNow);
        var days = Math.Clamp(IntOf(args, "days", 1), 1, CookieStaffing.MaxDays);
        ctx.Areas.Add(nameof(Area.Availability));
        var plan = ctx.Services.GetRequiredService<Availability.AvailabilityPlan>();
        ctx.Summary = $"the availability plan from {from:yyyy-MM-dd}, {days} days";
        return await CookieStaffing.DescribeAsync(plan, db, from, from.AddDays(days - 1), ctx.Privacy, ct);
    }

    private static async Task<object> OverviewAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var o = await ctx.Services.GetRequiredService<RedmineService>().OverviewAsync(IntOrNull(args, "project"), ct);
        return Redact(ctx, o);
    }

    private static async Task<object> TicketsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var result = await ctx.Services.GetRequiredService<RedmineService>().SearchAsync(new RedmineQuery
        {
            Search = OptionalText(args, "query", 200),
            Status = OptionalText(args, "status", 8),
            Assignee = OptionalText(args, "assignee", 32),
            Overdue = OptionalBool(args, "overdue") == true,
            Linked = OptionalBool(args, "linked") == true,
            Project = IntOrNull(args, "project"),
            Page = paging.Offset / paging.Limit + 1,
            PageSize = paging.Limit,
        }, ct);
        var more = result.Page * result.PageSize < result.TotalCount;
        ctx.Summary = $"{result.Items.Count} of {result.TotalCount} tickets";
        return new ToolPage<object>([.. result.Items.Select(t => Ticket(ctx, t))], more ? Paging.Cursor(result.Page * result.PageSize) : null, result.TotalCount);
    }

    private static object Ticket(ToolContext ctx, RedmineIssueItem t) => new
    {
        Number = t.Id, Project = t.ProjectName, t.Tracker, t.Status, t.IsClosed, t.Priority, Subject = ctx.Untrusted($"ticket #{t.Id}", t.Subject, 300),
        AssignedTo = ctx.Privacy.Mask(t.AssignedTo), t.UpdatedOn, t.DueDate, LinkedInPnmt = t.Links, Url = ctx.Link($"redmine/issues/{t.Id}"), Redmine = t.Url,
    };

    /// <summary>The overview's lists of tickets, each ticket as <see cref="Ticket"/>.</summary>
    private static object Redact(ToolContext ctx, RedmineOverview o)
    {
        var node = JsonSerializer.SerializeToNode(o, ToolResults.Json)!.AsObject();
        foreach (var (name, value) in node.ToList())
        {
            if (value is System.Text.Json.Nodes.JsonArray list && list.Count > 0 && list[0] is System.Text.Json.Nodes.JsonObject first && first.ContainsKey("subject"))
            {
                var items = list.Deserialize<List<RedmineIssueItem>>(ToolResults.Json) ?? [];
                node[name] = JsonSerializer.SerializeToNode(items.Select(t => Ticket(ctx, t)).ToList(), ToolResults.Json);
            }
        }
        ctx.Summary = "Redmine's overview";
        return node;
    }

    private static async Task<object> ZabbixSummaryAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var s = await ctx.Services.GetRequiredService<ZabbixService>().SummaryAsync(null, false, ct);
        ctx.Summary = "Zabbix's summary";
        return new { s.AsOf, s.Stale, s.Error, s.Severities, s.Categories, s.Hosts, s.Suppressed, Url = ctx.Link("zabbix/overview") };
    }

    private static async Task<object> ZabbixHostsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var result = await ctx.Services.GetRequiredService<ZabbixService>().HostsAsync(new ZabbixHostQuery
        {
            Search = OptionalText(args, "query", 200),
            Group = OptionalText(args, "group", 200),
            Attached = OptionalBool(args, "attached"),
            Page = paging.Offset / paging.Limit + 1,
            PageSize = paging.Limit,
        }, ct);
        var more = result.Page * result.PageSize < result.TotalCount;
        ctx.Summary = $"{result.Items.Count} of {result.TotalCount} Zabbix hosts";
        return new ToolPage<object>([.. result.Items.Select(h => (object)new
        {
            h.Id, h.Name, h.TechnicalName, h.Monitored, h.InMaintenance, h.Groups, h.Addresses, h.Availability, h.Problems, h.WorstSeverity,
            PnmtHost = h.PnmtHost, h.SensorId, h.Gone, Url = ctx.Link($"zabbix/hosts/{h.Id}"),
        })], more ? Paging.Cursor(result.Page * result.PageSize) : null, result.TotalCount);
    }

    private static async Task<object> ZabbixHostAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = OptionalLong(args, "id") ?? throw ToolException.Invalid("id must be a number.");
        ZabbixHostDetail host;
        try
        {
            host = await ctx.Services.GetRequiredService<ZabbixService>().HostAsync(id, ctx.Rights.Has(Area.Zabbix, Level.Details), ct);
        }
        catch (Api.ApiException e) when (e.Status == 404)
        {
            throw ToolException.NotFound("Zabbix host");
        }
        ctx.Target("ZabbixHost", id, null);
        var node = JsonSerializer.SerializeToNode(host, ToolResults.Json)!.AsObject();
        node["url"] = ctx.Link($"zabbix/hosts/{id}");
        return node;
    }

    private static int? IntOrNull(JsonElement args, string name) => Has(args, name) ? IntOf(args, name) : null;
}
