using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Responsibilities;
using PNMTD.Services.Domain;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// Responsibilities for MCP and Cookie (docs/responsibilities-plan.md §7), read-only: who owns which area of work, with a deputy,
/// what they link and which Redmine projects and trackers they list. Through <see cref="ResponsibilityQueries"/>, as the page reads
/// them; linked pages through <see cref="MachineAccess"/>, so a sensitive page is never named; people's names through the
/// masker, as Cookie knows people. Both say whom to ask in order (the escalation order, docs/responsibilities-mcp-and-map-plan.md §3);
/// MCP also gets the colour, <i>needs attention</i> and who changed it when (§6.2).
/// </summary>
public static class ResponsibilityReadTools
{
    /// <summary>What the word means, in each description: so that an AI doesn't take it for the availability plan's.</summary>
    private const string Meaning = "Responsibilities say who owns an area of work, such as Network or Backup, with a deputy; they are "
        + "information only, and not the availability plan's responsible of the day.";

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("responsibilities_list", "Responsibilities",
            $"{Meaning} The responsibilities with their short description, responsible and deputy, or the global responsible standing in, "
            + "and the escalation order: whom to ask, one after another. Given a host, a location or a knowledge base page, those that link it "
            + "(nothing is inherited from a location above); given a Redmine project, those that list it. Paged.",
            Schema([
                Text("query", "Words in the name, the descriptions or the people's names"),
                Bool("attention", "true: only those that need attention (nobody set, a person inactive, a Redmine combination nobody handles)"),
                Id("hostId", "Only those that link this host", required: false),
                Id("locationId", "Only those that link this location", required: false),
                Int("pageNumber", "Only those that link this knowledge base page (its number)"),
                Int("redmineProject", "Only those that list this Redmine project (its id)"),
                .. PageArgs,
            ]),
            Area.Responsibilities, Level.View, ToolKind.Read, ListAsync) { ForCookie = true },
        new("responsibility_get", "A responsibility",
            $"{Meaning} One responsibility in full: its description, its responsible and deputy, the escalation order (whom to ask, one "
            + "after another: the responsible, the deputy, the global responsible, the global deputy, skipping whoever is unset or inactive), "
            + "the pages, locations and hosts it links, the Redmine projects and trackers it lists with who handles each, and the global "
            + "responsible and deputy. Pages marked sensitive are never named.",
            Schema(Id("id", "The responsibility's id", required: false), Text("name", "Its name, when the id isn't known")),
            Area.Responsibilities, Level.View, ToolKind.Read, GetAsync) { ForCookie = true },
        // docs/responsibilities-mcp-and-map-plan.md §6.4: the history, the Links tab and the Overview's notes, for MCP only.
        new("responsibility_history", "A responsibility's history",
            "A responsibility's changes, newest first: when, by whom, what, and whether over MCP. With rowId, one row with the description " +
            "before and after it; responsibility_update's restoreFrom puts that description back. Paged.",
            Schema([Id("id", "The responsibility"), Id("rowId", "One row, with the description's texts", required: false), .. PageArgs]),
            Area.Responsibilities, Level.View, ToolKind.Read, HistoryAsync),
        new("responsibility_links_list", "Links between responsibilities and pages, locations, hosts",
            "The Links tab: one row per link (view links, the default), filtered by responsibilities, a person (their responsible or deputy), " +
            "kinds and a location (with what lies below unless below is false). view unlinkedHosts or unlinkedLocations: the hosts or " +
            "locations no responsibility links itself; covered true leaves out those whose location or a parent is linked. Those views need " +
            "Monitoring: View or Locations: View besides. Pages marked sensitive are never named. Paged.",
            Schema([
                Choice("view", "links (the default), unlinkedHosts or unlinkedLocations", ["links", "unlinkedHosts", "unlinkedLocations"]),
                Ids("responsibilityIds", "links: only these responsibilities' links", required: false),
                Id("personId", "links: the links of the responsibilities this user is the responsible or deputy of", required: false),
                Choices("kinds", "links: only these kinds", ["Page", "Location", "Host"]),
                Id("locationId", "Hosts in this location and the location itself", required: false),
                Bool("below", "With locationId: its sub-locations too (true when left out)"),
                Bool("covered", "unlinked views: leave out those whose location or a parent of it is linked"),
                Text("query", "Words in the object's name"),
                .. PageArgs,
            ]),
            Area.Responsibilities, Level.View, ToolKind.Read, LinksListAsync),
        new("responsibility_notes", "Notes on the responsibilities' overview",
            "The notes on Admin → Responsibilities → Overview, free on the map: their Markdown, colour, width, place (the top-left corner, in " +
            "the map's units) and who wrote and changed them. responsibility_note writes them.",
            Schema(), Area.Responsibilities, Level.View, ToolKind.Read, NotesAsync),
    ];

    private static async Task<object> ListAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var queries = ctx.Services.GetRequiredService<ResponsibilityQueries>();
        var host = OptionalId(args, "hostId");
        var location = OptionalId(args, "locationId");
        int? page = Has(args, "pageNumber") ? IntOf(args, "pageNumber") : null;
        int? project = Has(args, "redmineProject") ? IntOf(args, "redmineProject") : null;
        if (new object?[] { host, location, page, project }.Count(x => x is not null) > 1)
        {
            throw ToolException.Invalid("At most one of hostId, locationId, pageNumber and redmineProject.");
        }
        var list = await queries.ListAsync(OptionalText(args, "query", 200), OptionalBool(args, "attention") == true, ct);
        IEnumerable<ResponsibilityItem> items = list.Items;
        string? note = null;
        if (host is not null || location is not null || page is not null)
        {
            var (kind, target) = host is { } h ? ("host", h)
                : location is { } l ? ("location", l)
                : ("page", await MachineAccess.Pages(db).Where(p => p.Number == page).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct)
                    ?? throw ToolException.NotFound("page"));
            var linked = await db.ResponsibilityLinks.AsNoTracking()
                .Where(x => x.HostId == target || x.LocationId == target || x.PageId == target).Select(x => x.ResponsibilityId).ToListAsync(ct);
            items = items.Where(i => linked.Contains(i.Id));
            if (linked.Count == 0)
            {
                note = $"No responsibility links this {kind}. What no responsibility covers is the global responsible's: "
                    + $"{Name(ctx, list.Global.Responsible) ?? "nobody"}, global deputy {Name(ctx, list.Global.Deputy) ?? "nobody"}.";
            }
        }
        else if (project is { } p)
        {
            if (!queries.RedmineConfigured)
            {
                throw ToolException.Invalid("Redmine isn't configured in PNMT.");
            }
            var listing = await db.ResponsibilityRedmine.AsNoTracking().Where(c => c.ProjectId == p).Select(c => c.ResponsibilityId).ToListAsync(ct);
            items = items.Where(i => listing.Contains(i.Id));
        }
        var rows = items.ToList();
        var slice = paging.Slice(rows);
        ctx.Summary = $"{slice.Items.Count} of {rows.Count} responsibilities";
        var mcp = ctx.Caller == ToolCaller.Mcp;
        return new
        {
            Items = slice.Items.Select(r => new
            {
                r.Id, Name = ctx.Untrusted("responsibility name", r.Name, 100), Summary = ctx.UntrustedOrNull("responsibility summary", r.Summary, 300),
                Color = mcp ? r.Color : null, ColorName = mcp ? Colours.NameOf(r.Color) : null, Attention = mcp ? r.Attention : (bool?)null,
                Responsible = Person(ctx, r.Responsible), Deputy = Person(ctx, r.Deputy), r.GlobalStandsIn, Escalation = Escalation(ctx, r.Escalation),
                r.Links, RedmineCombinations = r.Combinations, UpdatedAt = mcp ? r.UpdatedAt : (DateTime?)null,
                Url = ctx.Link($"admin/responsibilities/{r.Id}"),
            }),
            slice.NextCursor, slice.Total,
            Global = new { Responsible = Person(ctx, list.Global.Responsible), Deputy = Person(ctx, list.Global.Deputy) },
            Note = note,
        };
    }

    private static async Task<object> GetAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var queries = ctx.Services.GetRequiredService<ResponsibilityQueries>();
        Guid id;
        if (OptionalId(args, "id") is { } given)
        {
            id = given;
        }
        else if (OptionalText(args, "name", 100) is { Length: > 0 } name)
        {
            var normalized = ResponsibilityService.Normalize(name);
            id = await db.Responsibilities.AsNoTracking().Where(r => r.NormalizedName == normalized).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(ct)
                ?? throw ToolException.NotFound("responsibility");
        }
        else
        {
            throw ToolException.Invalid("The responsibility's id or its name.");
        }
        ResponsibilityDetail r;
        try
        {
            r = await queries.GetAsync(id, ct);
        }
        catch (ApiException e) when (e.Status == StatusCodes.Status404NotFound)
        {
            throw ToolException.NotFound("responsibility");
        }
        ctx.Target("Responsibility", r.Id, r.Name);
        ctx.Summary = $"responsibility {r.Name}";
        // Only pages a machine may read (docs/locations-knowledge-plan.md §7.4): a sensitive one is left out, and counted.
        var pageIds = r.Links.Where(l => l.Kind == KbLinkKind.Page).Select(l => l.Id).ToList();
        var readable = pageIds.Count == 0 ? [] : await MachineAccess.Pages(db).Where(p => pageIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        var hidden = pageIds.Count - readable.Count;
        var mcp = ctx.Caller == ToolCaller.Mcp;
        return new
        {
            r.Id, Name = ctx.Untrusted("responsibility name", r.Name, 100), Summary = ctx.UntrustedOrNull("responsibility summary", r.Summary, 300),
            Color = mcp ? r.Color : null, ColorName = mcp ? Colours.NameOf(r.Color) : null, Attention = mcp ? r.Attention : (bool?)null,
            Description = ctx.UntrustedOrNull("responsibility description", MarkdownText.Read(r.Description).PlainText, 6000),
            Responsible = Person(ctx, r.Responsible), Deputy = Person(ctx, r.Deputy), r.GlobalStandsIn, Escalation = Escalation(ctx, r.Escalation),
            Global = new { Responsible = Person(ctx, r.Global.Responsible), Deputy = Person(ctx, r.Global.Deputy) },
            Pages = r.Links.Where(l => l.Kind == KbLinkKind.Page && readable.Contains(l.Id)).Select(l => new
            {
                l.Number, Title = ctx.Untrusted("page title", l.Name, 200), l.Archived, Url = ctx.Link($"kb/{l.Number}"),
            }),
            PagesNotShown = hidden > 0 ? $"{hidden} linked {(hidden == 1 ? "page is" : "pages are")} not shown" : null,
            Locations = r.Links.Where(l => l.Kind == KbLinkKind.Location && l.Exists)
                .Select(l => new { l.Id, Path = l.Detail ?? l.Name, Url = ctx.Link($"locations/{l.Id}") }),
            Hosts = r.Links.Where(l => l.Kind == KbLinkKind.Host && l.Exists)
                .Select(l => new { l.Id, l.Name, Location = l.Detail, Url = ctx.Link($"hosts/{l.Id}") }),
            // Without Redmine configured, the page shows none either (§6.1).
            Redmine = r.RedmineConfigured
                ? r.Redmine.Select(c => new
                {
                    c.ProjectId, Project = c.ProjectPath, c.TrackerId, Tracker = c.TrackerId is null ? "every tracker" : c.TrackerName,
                    HandledBy = Person(ctx, c.User), Note = ctx.UntrustedOrNull("note", c.Note, 200),
                })
                : null,
            // The description's files, by name: a token can't fetch them, and uploading stays in the web UI.
            Files = mcp ? r.Files.Select(f => new { Name = ctx.Untrusted("file name", f.Name, 200), Kind = f.Kind.ToString(), f.Size }) : null,
            CreatedAt = mcp ? r.CreatedAt : (DateTime?)null, CreatedByName = mcp ? r.CreatedByName : null,
            r.UpdatedAt, UpdatedByName = mcp ? r.UpdatedByName : null, Url = ctx.Link($"admin/responsibilities/{r.Id}"),
            // Over MCP, what changing it needs (docs/mcp-extensions-plan.md §8.2); Cookie only reads, so its answer stays as it was.
            Editing = ctx.Caller == ToolCaller.Mcp ? new
            {
                r.Revision,
                DescriptionMarkdown = ctx.UntrustedOrNull("responsibility description", r.Description, 20_000),
                ResponsibleId = r.Responsible?.Id, DeputyId = r.Deputy?.Id,
                Links = r.Links.Where(l => l.Kind != KbLinkKind.Page || readable.Contains(l.Id)).Select(l => new { l.LinkId, Kind = l.Kind.ToString(), l.Id }),
                RedmineCombinations = r.RedmineConfigured ? r.Redmine.Select(c => new { c.Id, c.ProjectId, c.TrackerId, UserId = c.User?.Id }) : null,
            } : null,
        };
    }

    private static async Task<object> HistoryAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var service = ctx.Services.GetRequiredService<ResponsibilityService>();
        try
        {
            if (OptionalId(args, "rowId") is { } rowId)
            {
                var row = await service.HistoryRowAsync(id, rowId, ct);
                ctx.Target("Responsibility", id, null);
                ctx.Summary = "a history row of a responsibility";
                return new
                {
                    row.Id, row.At, By = ctx.Privacy.Mask(row.ByName), Kind = KindText(row.Kind), Detail = ctx.UntrustedOrNull("history detail", row.Detail, 500),
                    Before = ctx.UntrustedOrNull("responsibility description", row.OldValue, 20_000),
                    After = ctx.UntrustedOrNull("responsibility description", row.NewValue, 20_000), ViaMcp = row.Via == "mcp",
                };
            }
            var paging = Paging.From(args);
            var page = await service.HistoryAsync(id, paging.Query(new PageQuery()), ct);
            ctx.Target("Responsibility", id, null);
            ctx.Summary = $"{page.Items.Count} of {page.TotalCount} history rows";
            return Paging.Of(page, h => new
            {
                h.Id, h.At, By = ctx.Privacy.Mask(h.ByName), Kind = KindText(h.Kind), Detail = ctx.UntrustedOrNull("history detail", h.Detail, 500),
                ViaMcp = h.Via == "mcp", Restorable = h.Kind == Models.Db.ResponsibilityHistoryKind.DescriptionChanged,
            });
        }
        catch (ApiException e) when (e.Status == StatusCodes.Status404NotFound)
        {
            throw ToolException.NotFound(OptionalId(args, "rowId") is null ? "responsibility" : "history row");
        }
    }

    /// <summary>A history row's kind as the page labels it.</summary>
    internal static string KindText(Models.Db.ResponsibilityHistoryKind kind) => kind switch
    {
        Models.Db.ResponsibilityHistoryKind.Created => "created",
        Models.Db.ResponsibilityHistoryKind.Renamed => "renamed",
        Models.Db.ResponsibilityHistoryKind.SummaryChanged => "short description changed",
        Models.Db.ResponsibilityHistoryKind.PeopleChanged => "people changed",
        Models.Db.ResponsibilityHistoryKind.DescriptionChanged => "description changed",
        Models.Db.ResponsibilityHistoryKind.LinkAdded => "linked",
        Models.Db.ResponsibilityHistoryKind.LinkRemoved => "unlinked",
        Models.Db.ResponsibilityHistoryKind.RedmineAdded => "Redmine combination added",
        Models.Db.ResponsibilityHistoryKind.RedmineChanged => "Redmine combination changed",
        Models.Db.ResponsibilityHistoryKind.RedmineRemoved => "Redmine combination removed",
        Models.Db.ResponsibilityHistoryKind.ColorChanged => "colour changed",
        _ => kind.ToString(),
    };

    private static async Task<object> LinksListAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var queries = ctx.Services.GetRequiredService<ResponsibilityQueries>();
        var paging = Paging.From(args);
        var location = OptionalId(args, "locationId");
        var below = OptionalBool(args, "below") ?? true;
        var search = OptionalText(args, "query", 200);
        var view = OptionalText(args, "view", 32) ?? "links";
        if (view is "unlinkedHosts" or "unlinkedLocations")
        {
            // As the endpoints: listing every host or location needs the right that lists them.
            var (kind, area, label) = view == "unlinkedHosts" ? (KbLinkKind.Host, Area.Monitoring, "Monitoring: View")
                : (KbLinkKind.Location, Area.Locations, "Locations: View");
            if (!ctx.Rights.Has(area, Level.View))
            {
                throw new ToolException("missing_right", $"{view} lists every {(kind == KbLinkKind.Host ? "host" : "location")}, so it needs {label} besides Responsibilities: View.");
            }
            var unlinked = await queries.UnlinkedAsync(kind, paging.Query(new ResponsibilityUnlinkedQuery
            {
                Location = location, Below = below, Covered = OptionalBool(args, "covered") == true, Search = search,
            }), ct);
            ctx.Summary = $"{unlinked.Items.Count} of {unlinked.TotalCount} {(kind == KbLinkKind.Host ? "hosts" : "locations")} without a responsibility";
            return Paging.Of(unlinked, u => new
            {
                u.Id, Kind = u.Kind.ToString(), Name = ctx.Untrusted("name", u.Name, 200), Where = ctx.UntrustedOrNull("location", u.Detail, 500),
                ThroughLocation = u.ThroughLocation.Select(b => new { b.Id, Name = ctx.Untrusted("responsibility name", b.Name, 100), b.Color }),
            });
        }
        if (view != "links")
        {
            throw ToolException.Invalid("view is links, unlinkedHosts or unlinkedLocations.");
        }
        var page = await queries.LinkRowsAsync(paging.Query(new ResponsibilityLinksQuery
        {
            Responsibility = OptionalIds(args, "responsibilityIds", 100)?.ToList(),
            Person = OptionalId(args, "personId"),
            Kind = OptionalEnums<KbLinkKind>(args, "kinds")?.ToList(),
            Location = location, Below = below, Search = search,
        }), ct);
        // Only pages a machine may read (docs/locations-knowledge-plan.md §7.4): a sensitive one is left out of the page, and counted.
        var pageIds = page.Items.Where(l => l.Kind == KbLinkKind.Page).Select(l => l.TargetId).Distinct().ToList();
        var readable = pageIds.Count == 0 ? [] : await MachineAccess.Pages(db).Where(p => pageIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(ct);
        var hidden = page.Items.Count(l => l.Kind == KbLinkKind.Page && !readable.Contains(l.TargetId));
        ctx.Summary = $"{page.Items.Count} of {page.TotalCount} links";
        var result = Paging.Of(page, l => l);
        return new
        {
            Items = result.Items.Where(l => l.Kind != KbLinkKind.Page || readable.Contains(l.TargetId)).Select(l => new
            {
                LinkId = l.Id, Responsibility = new { l.Responsibility.Id, Name = ctx.Untrusted("responsibility name", l.Responsibility.Name, 100), l.Responsibility.Color },
                Kind = l.Kind.ToString(), l.TargetId, Name = ctx.Untrusted("name", l.Name, 200), l.Number, Where = ctx.UntrustedOrNull("location", l.Detail, 500),
                l.Archived, l.CreatedAt, CreatedBy = l.CreatedByName is null ? null : ctx.Privacy.Mask(l.CreatedByName),
            }),
            result.NextCursor, result.Total,
            PagesNotShown = hidden > 0 ? $"{hidden} linked {(hidden == 1 ? "page is" : "pages are")} not shown" : null,
        };
    }

    private static async Task<object> NotesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var notes = await db.ResponsibilityMapNotes.AsNoTracking().OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToListAsync(ct);
        ctx.Summary = $"{notes.Count} notes on the overview";
        return new
        {
            Notes = notes.Select(n => new
            {
                n.Id, Text = ctx.Untrusted("overview note", n.Text, ResponsibilityMapService.MaxNoteLength), n.Colour, n.Width, n.X, n.Y,
                n.CreatedAt, CreatedBy = n.CreatedByName is null ? null : ctx.Privacy.Mask(n.CreatedByName), n.UpdatedAt,
                UpdatedBy = n.UpdatedByName is null ? null : ctx.Privacy.Mask(n.UpdatedByName),
            }),
            Url = ctx.Link("admin/responsibilities/overview"),
        };
    }

    /// <summary>The escalation order: each step's role and person, masked for Cookie as it knows people; never contact details.</summary>
    internal static IEnumerable<object> Escalation(ToolContext ctx, IEnumerable<ResponsibilityStep> steps) =>
        steps.Select(s => new { s.Step, Role = RoleText(s.Role), Name = ctx.Privacy.Mask(s.Person.Name) });

    internal static string RoleText(ResponsibilityRole role) => role switch
    {
        ResponsibilityRole.Responsible => "responsible",
        ResponsibilityRole.Deputy => "deputy",
        ResponsibilityRole.GlobalResponsible => "global responsible",
        _ => "global deputy",
    };

    /// <summary>A person by name, masked for Cookie as it knows people, with whether they are still active; never contact details.</summary>
    private static object? Person(ToolContext ctx, ResponsibilityPerson? person) =>
        person is null ? null : new { Name = ctx.Privacy.Mask(person.Name), person.Active };

    private static string? Name(ToolContext ctx, ResponsibilityPerson? person) => person is null ? null : ctx.Privacy.Mask(person.Name);
}
