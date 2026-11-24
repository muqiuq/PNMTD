using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Services.Domain;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>Hosts, sensors, their events and states (docs/mcp-plan.md §7.2): Monitoring View. Never a sensor's tokens or keys.</summary>
public static class MonitoringReadTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("hosts_search", "Find hosts", "Hosts whose name contains the text: id, name, importance, location, failing sensors.",
            Schema(Text("query", "Part of the host's name", true)), Area.Monitoring, Level.View, ToolKind.Read, HostsAsync) { ForCookie = true },
        new("host_get", "A host", "A host: importance, location, notes, and each sensor with its state, last code and message.",
            Schema(Id("id", "The host's id")), Area.Monitoring, Level.View, ToolKind.Read, HostAsync) { ForCookie = true },
        new("sensor_events", "A sensor's events", "A sensor's latest events: when, code (above 399 is a failure) and message.",
            Schema(Id("id", "The sensor's id"), Int("limit", "At most 50; 20 when left out")), Area.Monitoring, Level.View, ToolKind.Read, EventsAsync)
        { ForCookie = true },
        new("search", "Search PNMT",
            "PNMT's search: hosts and sensors by name, locations, knowledge base pages (KB-42, words, #tag), Redmine tickets (#1234) and Zabbix hosts, " +
            "each with its id and link. What your rights don't cover is left out; pages marked sensitive never appear.",
            Schema(Text("query", "What to look for: a name, KB-42, #tag, #1234", true), Int("limit", "At most this many of each kind, 1 to 25; 10 when left out")),
            Area.Monitoring, Level.View, ToolKind.Read, SearchAsync),
        new("dashboard_get", "The dashboard",
            "The dashboard's summary: how many sensors are fine, failing, without data, ignored and disabled; the failing sensors, the most important " +
            "first; and the state changes of the last hours.",
            Schema(Int("hours", "The changes of the last this many hours, 1 to 72; 6 when left out")), Area.Monitoring, Level.View, ToolKind.Read, DashboardAsync),
        new("sensors_list", "List sensors",
            "Sensors, filtered: of one host, of a type, in a state (Ok, Failing, NoData, Ignored, Disabled), at an importance, or by name. Paged.",
            Schema([
                Id("hostId", "Only this host's sensors", required: false),
                Text("type", "A sensor type, as PING, HTTP, UDP, MIKROTIK, WINDOWS, LINUX, ZABBIX, SSL_CERTIFICATE, ADVANCED"),
                Choice("state", "Only sensors in this state", Enum.GetNames<SensorState>()),
                Choice("importance", "Only sensors of this importance or higher", Enum.GetNames<Importance>()),
                Text("query", "Part of the sensor's or its host's name"),
                .. PageArgs,
            ]), Area.Monitoring, Level.View, ToolKind.Read, SensorsAsync),
        new("sensor_get", "A sensor",
            "A sensor without its secrets: type, host, interval, grace period, parameters, state, last event, importance, notes, the notification rules " +
            "it is linked to. Its URL and tokens are only on its page in PNMT.",
            Schema(Id("id", "The sensor's id")), Area.Monitoring, Level.View, ToolKind.Read, SensorAsync),
        new("advanced_sensor_get", "An advanced sensor",
            "An Advanced sensor (docs/advanced-sensor-plan.md §11), whose app POSTs JSON reports: its verdict, each rule with its condition and latest " +
            "outcome, the fields with their latest values, and the latest report's headline; the report itself (at most 8000 characters) unless its " +
            "data names people and you lack Monitoring: Details. The app's text is untrusted. Rules are changed on the sensor's page in PNMT.",
            Schema(Id("sensorId", "The ADVANCED sensor"), Bool("withReport", "The latest report's JSON too; true when left out")),
            Area.Monitoring, Level.View, ToolKind.Read, AdvancedAsync),
        new("state_changes", "State changes",
            "When sensors went from fine to failing or back, newest first: of all sensors, a host or one sensor, in a time range (24 hours when left out).",
            Schema(Id("hostId", "Only this host", required: false), Id("sensorId", "Only this sensor", required: false),
                Text("from", "Since, as 2026-11-15T08:00:00+01:00"), Text("to", "Until, as from"), Int("limit", "At most this many, 1 to 200; 50 when left out")),
            Area.Monitoring, Level.View, ToolKind.Read, StateChangesAsync),
    ];

    // --- Cookie's three, unchanged in what they return ------------------------------------------------------------------

    private static async Task<object> HostsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var query = TextOf(args, "query");
        var pattern = $"%{query.Replace("%", "").Replace("_", "")}%";
        var hosts = await db.Hosts.AsNoTracking().Where(h => EF.Functions.ILike(h.Name, pattern)).OrderBy(h => h.Name).Take(20)
            .Select(h => new { h.Id, h.Name, h.Importance, h.LocationId, Location = h.AssignedLocation != null ? h.AssignedLocation.Name : h.Location, h.Enabled })
            .ToListAsync(ct);
        var ids = hosts.Select(h => h.Id).ToList();
        var snapshot = await ctx.Services.GetRequiredService<MonitoringReader>().SnapshotAsync(s => ids.Contains(s.ParentId), ct);
        ctx.Summary = $"{hosts.Count} hosts for \"{query}\"";
        return hosts.Select(h => new
        {
            h.Id, h.Name, h.Importance, h.LocationId, h.Location, h.Enabled,
            Failing = snapshot.Sensors.Values.Count(s => s.Info.HostId == h.Id && s.State == SensorState.Failing),
            Sensors = snapshot.Sensors.Values.Count(s => s.Info.HostId == h.Id && !s.IsContainer),
            Url = ctx.Link($"hosts/{h.Id}"),
        }).ToList();
    }

    private static async Task<object> HostAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var host = await db.Hosts.AsNoTracking().Where(h => h.Id == id)
            .Select(h => new { h.Id, h.Name, h.Importance, h.Enabled, h.LocationId, LocationName = h.AssignedLocation != null ? h.AssignedLocation.Name : null, h.Location, h.Notes })
            .SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("host");
        var snapshot = await ctx.Services.GetRequiredService<MonitoringReader>().SnapshotAsync(s => s.ParentId == id, ct);
        var notes = await db.Sensors.AsNoTracking().Where(s => s.ParentId == id && s.Notes != null).ToDictionaryAsync(s => s.Id, s => s.Notes, ct);
        ctx.Target("Host", host.Id, host.Name);
        return new
        {
            host.Id, host.Name, host.Importance, host.Enabled, host.LocationId, Location = host.LocationName ?? host.Location,
            Notes = string.IsNullOrWhiteSpace(host.Notes) ? null : ctx.Untrusted("host notes", host.Notes, 1500),
            // Cookie's own notes (docs/cookie-object-notes-plan.md §3.3): within the caller's rights, Cookie's words.
            CookieNotes = await CookieObjectNotes.ForToolAsync(db, ctx, id, ct),
            Sensors = snapshot.Sensors.Values.Where(s => !s.IsContainer).OrderByDescending(s => s.State == SensorState.Failing)
                .ThenBy(s => s.Info.Name).Take(60).Select(s => new
                {
                    s.Info.Id, s.Info.Name, Type = s.Info.Type.ToString(), Importance = s.Info.EffectiveImportance, s.State, s.LastCode, s.LastEventAt, s.FailingSince,
                    LastMessage = string.IsNullOrWhiteSpace(s.LastMessage) ? null : ctx.Untrusted("sensor message", s.LastMessage, 300),
                    Notes = notes.TryGetValue(s.Info.Id, out var n) && !string.IsNullOrWhiteSpace(n) ? ctx.Untrusted("sensor notes", n, 500) : null,
                }),
            Url = ctx.Link($"hosts/{host.Id}"),
        };
    }

    private static async Task<object> EventsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var limit = Math.Clamp(IntOf(args, "limit", 20), 1, 50);
        var sensor = await db.Sensors.AsNoTracking().Where(s => s.Id == id).Select(s => new { s.Name, Host = s.Parent.Name }).SingleOrDefaultAsync(ct)
            ?? throw ToolException.NotFound("sensor");
        var events = await db.Events.AsNoTracking().Where(e => e.SensorId == id).OrderByDescending(e => e.Created).Take(limit)
            .Select(e => new { e.Created, e.Code, e.Message }).ToListAsync(ct);
        ctx.Target("Sensor", id, $"{sensor.Host} / {sensor.Name}");
        ctx.Summary = $"{events.Count} events of {sensor.Host} / {sensor.Name}";
        return events.Select(e => new
        {
            At = Clock.AsUtc(e.Created), e.Code, Message = string.IsNullOrWhiteSpace(e.Message) ? null : ctx.Untrusted("sensor message", e.Message, 300),
        }).ToList();
    }

    // --- MCP's -----------------------------------------------------------------------------------------------------------

    private static async Task<object> SearchAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var query = TextOf(args, "query", 1, 200);
        var limit = Math.Clamp(IntOf(args, "limit", 10), 1, 25);
        var found = await ctx.Services.GetRequiredService<SearchService>().SearchAsync(query, limit, ct);
        var pages = ctx.Rights.Has(Area.KnowledgeBase, Level.View) ? found.Pages.Where(p => !p.Sensitive).ToList() : [];
        var locations = ctx.Rights.Has(Area.Locations, Level.View) ? found.Locations : [];
        ctx.Summary = $"\"{query}\": {found.Hosts.Count} hosts, {found.Sensors.Count} sensors, {locations.Count} locations, {pages.Count} pages, " +
            $"{found.Tickets.Count} tickets, {found.ZabbixHosts.Count} Zabbix hosts";
        return new
        {
            Hosts = found.Hosts.Select(h => new { h.Id, h.Name, h.Location, h.Enabled, Url = ctx.Link($"hosts/{h.Id}") }),
            Sensors = found.Sensors.Select(s => new { s.Id, s.Name, Type = s.Type.ToString(), s.HostId, s.HostName, Url = ctx.Link($"sensors/{s.Id}") }),
            Locations = locations.Select(l => new { l.Id, l.Name, l.Path, Url = ctx.Link($"locations/{l.Id}") }),
            Pages = pages.Select(p => new
            {
                p.Id, p.Number, Title = ctx.Untrusted("page title", p.Title, 200), p.Tags, p.Status, Url = ctx.Link($"kb/{p.Number}"),
            }),
            Tickets = found.Tickets.Select(t => new { Number = t.Id, Subject = ctx.Untrusted($"ticket #{t.Id}", t.Subject, 300), t.Status, t.IsClosed, Url = ctx.Link($"redmine/issues/{t.Id}") }),
            ZabbixHosts = found.ZabbixHosts.Select(z => new { z.Id, z.Name, z.TechnicalName, z.Address, Url = ctx.Link($"zabbix/hosts/{z.Id}") }),
        };
    }

    private static async Task<object> DashboardAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var hours = Math.Clamp(IntOf(args, "hours", 6), 1, 72);
        var snapshot = await ctx.Services.GetRequiredService<MonitoringReader>().SnapshotAsync(null, ct);
        var counts = MonitoringSnapshot.Count(snapshot.Sensors.Values);
        var failing = snapshot.Sensors.Values.Where(s => s.State == SensorState.Failing && !s.IsContainer)
            .OrderByDescending(s => s.Info.EffectiveImportance).ThenBy(s => s.FailingSince).Take(50).ToList();
        var changes = await ctx.Services.GetRequiredService<EventService>().StateChangesAsync(
            new StateChangeQuery { From = Clock.UtcNow.AddHours(-hours), Limit = 50 }, ct);
        ctx.Summary = $"{counts.Failing} failing, {counts.Ok} fine";
        return new
        {
            Counts = counts,
            Failing = failing.Select(s => new
            {
                s.Info.Id, s.Info.Name, Type = s.Info.Type.ToString(), s.Info.HostId, Host = s.Info.HostName, Importance = s.Info.EffectiveImportance,
                s.LastCode, s.FailingSince, LastMessage = ctx.UntrustedOrNull("sensor message", s.LastMessage, 300), Url = ctx.Link($"sensors/{s.Info.Id}"),
            }),
            Changes = changes.Select(c => new { c.At, c.SensorId, c.SensorName, c.HostId, c.HostName, c.PreviousCode, c.Code, c.Success }),
            Url = ctx.Link("dashboard"),
        };
    }

    private static async Task<object> SensorsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var hostId = OptionalId(args, "hostId");
        var type = OptionalText(args, "type", 32) is { } t
            ? Enum.TryParse<SensorType>(t, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw ToolException.Invalid($"No sensor type {t}.")
            : (SensorType?)null;
        var state = OptionalEnum<SensorState>(args, "state");
        var importance = OptionalEnum<Importance>(args, "importance");
        var query = OptionalText(args, "query", 200);
        var snapshot = await ctx.Services.GetRequiredService<MonitoringReader>().SnapshotAsync(
            s => (hostId == null || s.ParentId == hostId) && (type == null || s.Type == type), ct);
        var rows = snapshot.Sensors.Values.Where(s => !s.IsContainer
                && (state is null || s.State == state)
                && (importance is null || s.Info.EffectiveImportance >= importance)
                && (query is null || s.Info.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Info.HostName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.State == SensorState.Failing).ThenBy(s => s.Info.HostName).ThenBy(s => s.Info.Name).ToList();
        var page = paging.Slice(rows);
        ctx.Summary = $"{page.Items.Count} of {rows.Count} sensors";
        return page.Select(s => new
        {
            s.Info.Id, s.Info.Name, Type = s.Info.Type.ToString(), s.Info.HostId, Host = s.Info.HostName, s.State, Importance = s.Info.EffectiveImportance,
            s.LastCode, s.LastEventAt, s.FailingSince, LastMessage = ctx.UntrustedOrNull("sensor message", s.LastMessage, 200), Url = ctx.Link($"sensors/{s.Info.Id}"),
        });
    }

    private static async Task<object> SensorAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var s = await db.Sensors.AsNoTracking().Where(x => x.Id == id).Select(x => new
        {
            x.Id, x.Name, x.Type, x.ParentId, Host = x.Parent.Name, x.Enabled, x.Ignore, x.Interval, x.GracePeriod, x.Parameters, x.Notes, x.Importance,
            HostImportance = x.Parent.Importance, x.Created, x.OlderSiblingId,
            Rules = x.SubscribedByNotifications.Select(r => new { r.NotificationRule.Id, r.NotificationRule.Name, r.NotificationRule.Type, r.NotificationRule.Enabled }).ToList(),
        }).SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("sensor");
        var snapshot = await ctx.Services.GetRequiredService<MonitoringReader>().SnapshotAsync(x => x.Id == id, ct);
        var status = snapshot.Sensors.GetValueOrDefault(id);
        ctx.Target("Sensor", s.Id, $"{s.Host} / {s.Name}");
        return new
        {
            s.Id, s.Name, Type = s.Type.ToString(), HostId = s.ParentId, s.Host, s.Enabled, s.Ignore, IntervalSeconds = s.Interval, GracePeriodSeconds = s.GracePeriod,
            Parameters = ToolSafety.WithoutCredentials(s.Parameters),
            Importance = s.Importance ?? s.HostImportance, OwnImportance = s.Importance, s.Created, ParentSensorId = s.OlderSiblingId,
            State = status?.State, status?.LastCode, status?.LastEventAt, status?.FailingSince,
            LastMessage = ctx.UntrustedOrNull("sensor message", status?.LastMessage, 500),
            Notes = ctx.UntrustedOrNull("sensor notes", s.Notes, 1500),
            CookieNotes = await CookieObjectNotes.ForToolAsync(db, ctx, id, ct),
            NotificationRules = s.Rules.Select(r => new { r.Id, r.Name, Type = r.Type.ToString(), r.Enabled }),
            Url = ctx.Link($"sensors/{s.Id}"),
        };
    }

    private static async Task<object> StateChangesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var changes = await ctx.Services.GetRequiredService<EventService>().StateChangesAsync(new StateChangeQuery
        {
            HostId = OptionalId(args, "hostId"),
            SensorId = OptionalId(args, "sensorId"),
            From = OptionalTime(args, "from"),
            To = OptionalTime(args, "to"),
            Limit = Math.Clamp(IntOf(args, "limit", 50), 1, 200),
        }, ct);
        ctx.Summary = $"{changes.Count} state changes";
        return changes.Select(c => new
        {
            c.At, c.SensorId, c.SensorName, c.HostId, c.HostName, c.PreviousCode, c.Code, c.Success, Url = ctx.Link($"sensors/{c.SensorId}"),
        }).ToList();
    }

    /// <summary>
    /// An Advanced sensor for a machine (docs/advanced-sensor-plan.md §11): everything the app sent is untrusted text, stripped of
    /// credentials; the values and the report only when the data doesn't name people, or the caller has Monitoring: Details.
    /// </summary>
    private static async Task<object> AdvancedAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var name = await db.Sensors.AsNoTracking().Where(s => s.Id == id && s.Type == SensorType.ADVANCED).Select(s => s.Parent.Name + " / " + s.Name)
            .SingleOrDefaultAsync(ct) ?? throw ToolException.Invalid("No such Advanced sensor.");
        ctx.Target("Sensor", id, name);
        var details = ctx.Rights.Has(Area.Monitoring, Level.Details);
        var advanced = ctx.Services.GetRequiredService<PNMTD.Advanced.AdvancedSensorService>();
        var d = await advanced.DetailAsync(id, ct, canSeeDetails: details) ?? throw ToolException.Invalid("No such Advanced sensor.");
        string? Untrusted(string source, string? text, int max = 500) => ctx.UntrustedOrNull(source, ToolSafety.WithoutCredentials(text), max);
        string? body = null;
        if (d.CanSeeData && (args.TryGetProperty("withReport", out var w) ? w.ValueKind != JsonValueKind.False : true))
        {
            var raw = await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == id).OrderByDescending(r => r.LastReceivedAt).Select(r => r.Body)
                .FirstOrDefaultAsync(ct);
            if (raw is not null)
            {
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    raw = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (JsonException)
                {
                }
                body = Untrusted("advanced sensor report", raw, 8000);
            }
        }
        ctx.Summary = $"advanced sensor {name}";
        return AdvancedView(ctx, id, name, d, body);
    }

    /// <summary>
    /// What the tools say of an Advanced sensor (docs/advanced-sensor-plan.md §11): the verdict, each rule and field with its id and latest
    /// outcome, the latest report's headline. Also the answer of every change to its rules and fields (docs/mcp-extensions-plan.md §7).
    /// </summary>
    internal static object AdvancedView(ToolContext ctx, Guid id, string name, PNMTD.Api.V1.Contracts.AdvancedSensorDetail d, string? body)
    {
        string? Untrusted(string source, string? text, int max = 500) => ctx.UntrustedOrNull(source, ToolSafety.WithoutCredentials(text), max);
        return new
        {
            SensorId = id,
            Sensor = name,
            Verdict = d.Verdict is { } v ? new { v.Code, Message = Untrusted("advanced sensor verdict", v.Message, 1000), v.Failed, v.Warnings, v.EvaluatedAt } : null,
            d.LastReportAt,
            d.Stale,
            d.StaleAt,
            d.NamesPeople,
            DataShown = d.CanSeeData,
            Pending = d.Pending,
            Rules = d.Rules.Select(r => new
            {
                r.Id, r.Name, Condition = Untrusted("advanced sensor rule", r.Condition), r.Severity, r.Enabled, Outcome = r.Last?.Outcome, Reason = r.Last?.Reason,
                Actual = r.Last is { Actual.Count: > 0 } last
                    ? Untrusted("advanced sensor value", string.Join("; ", last.Actual.Select(a => $"{a.Path} = {a.Value}")), 1000)
                    : null,
                r.FailingSince, Note = Untrusted("advanced sensor rule note", r.Note),
            }).ToList(),
            Fields = d.Fields.Select(f => new { f.Id, f.Name, f.Path, f.Unit, Value = Untrusted("advanced sensor value", f.Value?.GetRawText(), 300) }).ToList(),
            Latest = d.Latest is { } l ? new
            {
                l.ReceivedAt, l.LastReceivedAt, l.Repeats, l.Valid, Error = Untrusted("advanced sensor report", l.Error),
                Status = Untrusted("advanced sensor report", l.Status, 50), Message = Untrusted("advanced sensor report", l.Message, 1000), l.MeasuredAt,
            } : null,
            Report = body,
            Url = ctx.Link($"sensors/{id}/data"),
        };
    }
}
