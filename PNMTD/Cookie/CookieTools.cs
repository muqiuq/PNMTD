using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PNMTD.Ai;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Services.Domain;
using PNMTD.Zabbix;

namespace PNMTD.Cookie;

/// <summary>What a tool runs with: the rights it may use (Cookie's group, or the lower of that and an asker's), the round's masking, and the areas used.</summary>
public sealed class ToolContext(IServiceProvider services, UserRights rights, Pseudonyms privacy)
{
    public IServiceProvider Services { get; } = services;

    public UserRights Rights { get; } = rights;

    public Pseudonyms Privacy { get; } = privacy;

    /// <summary>The areas the results came from: a note or a reply carries them (§9.2).</summary>
    public HashSet<string> Areas { get; } = [];

    /// <summary>Outside text as data (§13.1): cleaned, masked, scanned and wrapped; an obvious injection is withheld.</summary>
    public string Untrusted(string source, string? text, int maxChars = 2000)
    {
        var cleaned = InputSanitizer.Clean(text, maxChars);
        var scan = InjectionScanner.Check(cleaned.Text, cleaned.InvisibleRemoved);
        if (scan.Score >= InjectionScanner.BlockScore)
        {
            return $"<untrusted source=\"{source}\" withheld=\"looks like instructions to an AI\"></untrusted>";
        }
        var flags = scan.Flags.Count > 0 ? $" suspicious=\"{string.Join(", ", scan.Flags)}\"" : "";
        return $"<untrusted source=\"{source}\"{flags}>{Privacy.Mask(cleaned.Text)}</untrusted>";
    }
}

/// <summary>
/// A read tool (docs/cookie-plan.md §3.5): its name, what it does, its parameters as JSON schema, the right it needs, and
/// whether it writes. Cookie only ever gets tools that read. Package eight's MCP server is to offer the same ones.
/// </summary>
public sealed record CookieTool(
    string Name,
    string Description,
    JsonObject Parameters,
    Area? Area,
    Level Level,
    bool Writes,
    Func<JsonElement, ToolContext, PnmtdDbContext, CancellationToken, Task<object>> Run);

/// <summary>The read tools, and running one with its right checked in code, whatever the model asks.</summary>
public static class CookieTools
{
    public const int MaxResultChars = 8000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static JsonObject Params(params (string Name, string Type, string Description, bool Required)[] fields)
    {
        var properties = new JsonObject();
        foreach (var (name, type, description, _) in fields)
        {
            properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. fields.Where(f => f.Required).Select(f => (JsonNode)f.Name)]),
            ["additionalProperties"] = false,
        };
    }

    public static readonly IReadOnlyList<CookieTool> All =
    [
        new("cases_list", "The open cases you may see: id, kind, title, importance, when opened, last change, last progress, the next deadline, your note.",
            Params(("closed", "boolean", "true for the cases closed in the last 7 days instead", false)), null, Level.None, false, CasesAsync),
        new("case_get", "One case: its members (bad sensors, Zabbix problems), timeline, deadlines, the ticket's clock, your last note.",
            Params(("id", "string", "The case's id", true)), null, Level.None, false, CaseAsync),
        new("hosts_search", "Hosts whose name contains the text: id, name, importance, location, failing sensors.",
            Params(("query", "string", "Part of the host's name", true)), Auth.Rights.Area.Monitoring, Level.View, false, HostsAsync),
        new("host_get", "A host: importance, location, notes, and each sensor with its state, last code and message.",
            Params(("id", "string", "The host's id", true)), Auth.Rights.Area.Monitoring, Level.View, false, HostAsync),
        new("sensor_events", "A sensor's latest events: when, code (above 399 is a failure) and message.",
            Params(("id", "string", "The sensor's id", true), ("limit", "integer", "At most 50; 20 when left out", false)), Auth.Rights.Area.Monitoring, Level.View,
            false, EventsAsync),
        new("location_get", "A location: its name, its parent, and its hosts.",
            Params(("id", "string", "The location's id", true)), Auth.Rights.Area.Locations, Level.View, false, LocationAsync),
        new("kb_search", "Knowledge base pages that contain the text: id, number, title, summary. Pages marked sensitive never appear.",
            Params(("query", "string", "Words to look for", true)), Auth.Rights.Area.KnowledgeBase, Level.View, false, KbSearchAsync),
        new("kb_page", "A knowledge base page's text. Pages marked sensitive are never given.",
            Params(("number", "integer", "The page's number", true)), Auth.Rights.Area.KnowledgeBase, Level.View, false, KbPageAsync),
        new("ticket_get", "A Redmine ticket of the projects PNMT shows: header, description and its journals.",
            Params(("number", "integer", "The ticket's number", true)), Auth.Rights.Area.Redmine, Level.View, false, TicketAsync),
        new("zabbix_problems", "Zabbix's open problems, the most severe first, for one Zabbix host or all: name, severity, since, acknowledged, host.",
            Params(("zabbixHostId", "integer", "One Zabbix host's id; all when left out", false)), Auth.Rights.Area.Zabbix, Level.View, false, ZabbixAsync),
        new("availability_get", "The availability plan for some days (at most 14): who is responsible, who works and where (on site, work travel, home " +
            "office), who is away and why, the counts below their minimum, the day's comment. People are placeholders.",
            Params(("from", "string", "The first day, yyyy-MM-dd; today when left out", false), ("days", "integer", "How many days, 1 to 14; 1 when left out", false)),
            Auth.Rights.Area.Availability, Level.View, false, AvailabilityAsync),
    ];

    /// <summary>The tools these rights allow; never one that writes (§3.5).</summary>
    public static IReadOnlyList<CookieTool> For(UserRights rights) =>
        All.Where(t => !t.Writes && (t.Area is not { } area || rights.Has(area, t.Level))).ToList();

    public static JsonArray Definitions(IEnumerable<CookieTool> tools) => new([.. tools.Select(t => (JsonNode)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
    })]);

    /// <summary>Runs a tool the model asked for: unknown, not allowed or failing comes back as an error the model can read.</summary>
    public static async Task<string> RunAsync(string name, string arguments, ToolContext ctx, CancellationToken ct)
    {
        var tool = For(ctx.Rights).FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            return Error(All.Any(t => t.Name == name) ? $"{name} needs a right Cookie doesn't have here." : $"There is no tool {name}.");
        }
        JsonElement args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments).RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error("The arguments are not JSON.");
        }
        try
        {
            await using var scope = ctx.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();
            var scoped = new ToolContext(scope.ServiceProvider, ctx.Rights, ctx.Privacy);
            var result = await tool.Run(args, scoped, db, ct);
            ctx.Areas.UnionWith(scoped.Areas);
            if (tool.Area is { } area)
            {
                ctx.Areas.Add(area.ToString());
            }
            var text = JsonSerializer.Serialize(result, Json);
            return text.Length <= MaxResultChars ? text : text[..MaxResultChars] + "\n[… cut: ask for less]";
        }
        catch (ToolException e)
        {
            return Error(e.Message);
        }
        catch (RedmineException e)
        {
            return Error($"Redmine: {e.Message}");
        }
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);

    private sealed class ToolException(string message) : Exception(message);

    private static Guid IdOf(JsonElement args, string name = "id") =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id) ? id
            : throw new ToolException($"{name} must be an id from your data.");

    private static int IntOf(JsonElement args, string name, int? fallback = null) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
            : fallback ?? throw new ToolException($"{name} must be a number.");

    private static string TextOf(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length >= 2 ? v.GetString()!.Trim()
            : throw new ToolException($"{name} needs at least 2 characters.");

    // --- The availability plan (§8.4) -------------------------------------------------------------------------------------

    private static async Task<object> AvailabilityAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var from = args.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String
            ? DateOnly.TryParseExact(f.GetString(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None,
                out var d) ? d : throw new ToolException("from must be a date as yyyy-MM-dd.")
            : DateOnly.FromDateTime(Clock.LocalNow);
        var days = Math.Clamp(IntOf(args, "days", 1), 1, CookieStaffing.MaxDays);
        ctx.Areas.Add(nameof(Auth.Rights.Area.Availability));
        var plan = ctx.Services.GetRequiredService<PNMTD.Availability.AvailabilityPlan>();
        return await CookieStaffing.DescribeAsync(plan, db, from, from.AddDays(days - 1), ctx.Privacy, ct);
    }

    // --- Cases ------------------------------------------------------------------------------------------------------------

    private static async Task<object> CasesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var closed = args.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True;
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
        return visible.Select(x => new
        {
            x.Id, x.Kind, Title = ctx.Privacy.Mask(x.Title), x.Importance, x.OpenedAt, x.ChangedAt, x.ProgressAt,
            NextDeadline = next.TryGetValue(x.Id, out var d) ? $"{d.Kind} at {d.DueAt:u}" : null,
            YourNote = ctx.Privacy.Mask(x.CookieNote),
        }).ToList();
    }

    private static async Task<object> CaseAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var x = await db.CookieCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (x is null || x.Kind == CookieCaseKind.Request || !CookieAccess.CanSee(ctx.Rights, x.Areas))
        {
            throw new ToolException("No such case.");
        }
        ctx.Areas.UnionWith(x.Areas);
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
        };
    }

    // --- Monitoring ---------------------------------------------------------------------------------------------------------

    private static async Task<object> HostsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var query = TextOf(args, "query");
        var pattern = $"%{query.Replace("%", "").Replace("_", "")}%";
        var hosts = await db.Hosts.AsNoTracking().Where(h => EF.Functions.ILike(h.Name, pattern)).OrderBy(h => h.Name).Take(20)
            .Select(h => new { h.Id, h.Name, h.Importance, h.LocationId, Location = h.AssignedLocation != null ? h.AssignedLocation.Name : h.Location, h.Enabled })
            .ToListAsync(ct);
        var ids = hosts.Select(h => h.Id).ToList();
        var snapshot = await ctx.Services.GetRequiredService<MonitoringReader>().SnapshotAsync(s => ids.Contains(s.ParentId), ct);
        return hosts.Select(h => new
        {
            h.Id, h.Name, h.Importance, h.LocationId, h.Location, h.Enabled,
            Failing = snapshot.Sensors.Values.Count(s => s.Info.HostId == h.Id && s.State == Api.V1.Contracts.SensorState.Failing),
            Sensors = snapshot.Sensors.Values.Count(s => s.Info.HostId == h.Id && !s.IsContainer),
        }).ToList();
    }

    private static async Task<object> HostAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var host = await db.Hosts.AsNoTracking().Where(h => h.Id == id)
            .Select(h => new { h.Id, h.Name, h.Importance, h.Enabled, h.LocationId, LocationName = h.AssignedLocation != null ? h.AssignedLocation.Name : null, h.Location, h.Notes })
            .SingleOrDefaultAsync(ct) ?? throw new ToolException("No such host.");
        var snapshot = await ctx.Services.GetRequiredService<MonitoringReader>().SnapshotAsync(s => s.ParentId == id, ct);
        var notes = await db.Sensors.AsNoTracking().Where(s => s.ParentId == id && s.Notes != null).ToDictionaryAsync(s => s.Id, s => s.Notes, ct);
        return new
        {
            host.Id, host.Name, host.Importance, host.Enabled, host.LocationId, Location = host.LocationName ?? host.Location,
            Notes = string.IsNullOrWhiteSpace(host.Notes) ? null : ctx.Untrusted("host notes", host.Notes, 1500),
            Sensors = snapshot.Sensors.Values.Where(s => !s.IsContainer).OrderByDescending(s => s.State == Api.V1.Contracts.SensorState.Failing)
                .ThenBy(s => s.Info.Name).Take(60).Select(s => new
                {
                    s.Info.Id, s.Info.Name, Type = s.Info.Type.ToString(), Importance = s.Info.EffectiveImportance, s.State, s.LastCode, s.LastEventAt, s.FailingSince,
                    LastMessage = string.IsNullOrWhiteSpace(s.LastMessage) ? null : ctx.Untrusted("sensor message", s.LastMessage, 300),
                    Notes = notes.TryGetValue(s.Info.Id, out var n) && !string.IsNullOrWhiteSpace(n) ? ctx.Untrusted("sensor notes", n, 500) : null,
                }),
        };
    }

    private static async Task<object> EventsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var limit = Math.Clamp(IntOf(args, "limit", 20), 1, 50);
        if (!await db.Sensors.AnyAsync(s => s.Id == id, ct))
        {
            throw new ToolException("No such sensor.");
        }
        var events = await db.Events.AsNoTracking().Where(e => e.SensorId == id).OrderByDescending(e => e.Created).Take(limit)
            .Select(e => new { e.Created, e.Code, e.Message }).ToListAsync(ct);
        return events.Select(e => new
        {
            At = Clock.AsUtc(e.Created), e.Code, Message = string.IsNullOrWhiteSpace(e.Message) ? null : ctx.Untrusted("sensor message", e.Message, 300),
        }).ToList();
    }

    private static async Task<object> LocationAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var location = await db.Locations.AsNoTracking().Where(l => l.Id == id).Select(l => new { l.Id, l.Name, l.ParentId }).SingleOrDefaultAsync(ct)
            ?? throw new ToolException("No such location.");
        var parent = location.ParentId is { } p ? await db.Locations.AsNoTracking().Where(l => l.Id == p).Select(l => l.Name).FirstOrDefaultAsync(ct) : null;
        var hosts = await db.Hosts.AsNoTracking().Where(h => h.LocationId == id).OrderBy(h => h.Name).Take(100).Select(h => new { h.Id, h.Name, h.Importance })
            .ToListAsync(ct);
        return new { location.Id, location.Name, location.ParentId, Parent = parent, Hosts = hosts };
    }

    // --- The knowledge base: only what MachineAccess lets through (docs/locations-knowledge-plan.md §7.4) ------------------

    private static async Task<object> KbSearchAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var query = TextOf(args, "query");
        var pattern = $"%{query.Replace("%", "").Replace("_", "")}%";
        var pages = await MachineAccess.Pages(db).Where(p => p.Status == KbPageStatus.Published && p.ArchivedAt == null
                && (EF.Functions.ILike(p.Title, pattern) || EF.Functions.ILike(p.PlainText, pattern) || EF.Functions.ILike(p.TagsText, pattern)))
            .OrderByDescending(p => p.UpdatedAt).Take(10).Select(p => new { p.Id, p.Number, p.Title, p.Summary, p.UpdatedAt }).ToListAsync(ct);
        return pages.Select(p => new
        {
            p.Id, p.Number, Title = ctx.Untrusted("page title", p.Title, 200), Summary = p.Summary is null ? null : ctx.Untrusted("page summary", p.Summary, 400),
            p.UpdatedAt,
        }).ToList();
    }

    private static async Task<object> KbPageAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var number = IntOf(args, "number");
        var page = await MachineAccess.Pages(db).Where(p => p.Number == number && p.ArchivedAt == null)
            .Select(p => new { p.Id, p.Number, p.Title, p.PlainText, p.UpdatedAt }).SingleOrDefaultAsync(ct) ?? throw new ToolException("No such page.");
        return new { page.Id, page.Number, Title = ctx.Untrusted("page title", page.Title, 200), Text = ctx.Untrusted("page", page.PlainText, 6000), page.UpdatedAt };
    }

    // --- Redmine and Zabbix -----------------------------------------------------------------------------------------------

    private static async Task<object> TicketAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var number = IntOf(args, "number");
        Api.V1.Contracts.RedmineIssueDetail t;
        try
        {
            t = await ctx.Services.GetRequiredService<RedmineService>().IssueAsync(number, ct);
        }
        catch (Api.ApiException)
        {
            throw new ToolException("No such ticket in the projects PNMT shows.");
        }
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
        };
    }

    private static Task<object> ZabbixAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var snapshot = ctx.Services.GetRequiredService<ZabbixSnapshot>().Current;
        long? host = args.TryGetProperty("zabbixHostId", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt64() : null;
        var problems = snapshot.Problems.Where(p => host is null || p.Hosts.Any(x => ZabbixPoller.Int(x.Hostid) == host))
            .OrderByDescending(p => p.Severity).ThenByDescending(p => p.Since).Take(30)
            .Select(p => new
            {
                p.EventId, Name = ctx.Untrusted("Zabbix", p.Name, 300), Severity = p.Severity.ToString(), Since = Clock.AsUtc(p.Since), p.Acknowledged, p.Suppressed,
                Hosts = p.Hosts.Select(x => new { ZabbixHostId = ZabbixPoller.Int(x.Hostid), x.Name }),
            }).ToList();
        return Task.FromResult<object>(new { snapshot.AsOf, snapshot.Error, Problems = problems });
    }
}
