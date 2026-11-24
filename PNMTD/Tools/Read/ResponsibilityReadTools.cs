using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Responsibilities;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// Responsibilities for MCP and Cookie (docs/responsibilities-plan.md §7), read-only: who owns which area of work, with a deputy,
/// what they link and which Redmine projects and trackers they list. Through <see cref="ResponsibilityQueries"/>, as the page reads
/// them; linked pages through <see cref="MachineAccess"/>, so a sensitive page is never named; people's names through the
/// masker, as Cookie knows people.
/// </summary>
public static class ResponsibilityReadTools
{
    /// <summary>What the word means, in each description: so that an AI doesn't take it for the availability plan's.</summary>
    private const string Meaning = "Responsibilities say who owns an area of work, such as Network or Backup, with a deputy; they are "
        + "information only, and not the availability plan's responsible of the day.";

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("responsibilities_list", "Responsibilities",
            $"{Meaning} The responsibilities with their short description, responsible and deputy, or the global responsible standing in. "
            + "Given a host, a location or a knowledge base page, those that link it (nothing is inherited from a location above); given a "
            + "Redmine project, those that list it. Paged.",
            Schema([
                Text("query", "Words in the name, the descriptions or the people's names"),
                Id("hostId", "Only those that link this host", required: false),
                Id("locationId", "Only those that link this location", required: false),
                Int("pageNumber", "Only those that link this knowledge base page (its number)"),
                Int("redmineProject", "Only those that list this Redmine project (its id)"),
                .. PageArgs,
            ]),
            Area.Responsibilities, Level.View, ToolKind.Read, ListAsync) { ForCookie = true },
        new("responsibility_get", "A responsibility",
            $"{Meaning} One responsibility in full: its description, its responsible and deputy, the pages, locations and hosts it links, "
            + "the Redmine projects and trackers it lists with who handles each, and the global responsible and deputy. Pages marked "
            + "sensitive are never named.",
            Schema(Id("id", "The responsibility's id", required: false), Text("name", "Its name, when the id isn't known")),
            Area.Responsibilities, Level.View, ToolKind.Read, GetAsync) { ForCookie = true },
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
        var list = await queries.ListAsync(OptionalText(args, "query", 200), false, ct);
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
        return new
        {
            Items = slice.Items.Select(r => new
            {
                r.Id, Name = ctx.Untrusted("responsibility name", r.Name, 100), Summary = ctx.UntrustedOrNull("responsibility summary", r.Summary, 300),
                Responsible = Person(ctx, r.Responsible), Deputy = Person(ctx, r.Deputy), r.GlobalStandsIn, r.Links,
                RedmineCombinations = r.Combinations, Url = ctx.Link($"admin/responsibilities/{r.Id}"),
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
        return new
        {
            r.Id, Name = ctx.Untrusted("responsibility name", r.Name, 100), Summary = ctx.UntrustedOrNull("responsibility summary", r.Summary, 300),
            Description = ctx.UntrustedOrNull("responsibility description", MarkdownText.Read(r.Description).PlainText, 6000),
            Responsible = Person(ctx, r.Responsible), Deputy = Person(ctx, r.Deputy), r.GlobalStandsIn,
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
            r.UpdatedAt, Url = ctx.Link($"admin/responsibilities/{r.Id}"),
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

    /// <summary>A person by name, masked for Cookie as it knows people, with whether they are still active; never contact details.</summary>
    private static object? Person(ToolContext ctx, ResponsibilityPerson? person) =>
        person is null ? null : new { Name = ctx.Privacy.Mask(person.Name), person.Active };

    private static string? Name(ToolContext ctx, ResponsibilityPerson? person) => person is null ? null : ctx.Privacy.Mask(person.Name);
}
