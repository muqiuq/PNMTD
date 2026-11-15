using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Knowledge;
using PNMTD.Locations;
using PNMTD.Models.Db;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// Locations and the knowledge base (docs/mcp-plan.md §7.2). <b>Only what <see cref="MachineAccess"/> lets through</b>
/// (docs/locations-knowledge-plan.md §7.4): a page or a field marked sensitive doesn't exist for a machine.
/// </summary>
public static class KnowledgeReadTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("location_get", "A location", "A location: its name, its parent, and its hosts.",
            Schema(Id("id", "The location's id")), Area.Locations, Level.View, ToolKind.Read, LocationAsync) { ForCookie = true },
        new("kb_search", "Search the knowledge base", "Knowledge base pages that contain the text: id, number, title, summary. Pages marked sensitive never appear.",
            Schema(Text("query", "Words to look for", true)), Area.KnowledgeBase, Level.View, ToolKind.Read, KbSearchAsync) { ForCookie = true },
        new("kb_page", "A knowledge base page", "A knowledge base page's text. Pages marked sensitive are never given.",
            Schema(Int("number", "The page's number", true)), Area.KnowledgeBase, Level.View, ToolKind.Read, KbPageAsync) { ForCookie = true },
        new("locations_list", "List locations",
            "Locations with their place in the tree (site, building, room …): id, name, path, parent, how many hosts. Filter by name or by parent. Paged.",
            Schema([Text("query", "Part of the location's name"), Id("parentId", "Only the direct children of this location", required: false), .. PageArgs]),
            Area.Locations, Level.View, ToolKind.Read, LocationsAsync),
        new("location_details", "A location's information",
            "A location's information fields (contacts, access, notes …), its own and those inherited from above, its child locations, and the pages " +
            "linked to it. Fields and pages marked sensitive never appear.",
            Schema(Id("id", "The location's id")), Area.Locations, Level.View, ToolKind.Read, LocationDetailsAsync),
    ];

    private static async Task<object> LocationAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var location = await db.Locations.AsNoTracking().Where(l => l.Id == id).Select(l => new { l.Id, l.Name, l.ParentId }).SingleOrDefaultAsync(ct)
            ?? throw ToolException.NotFound("location");
        var parent = location.ParentId is { } p ? await db.Locations.AsNoTracking().Where(l => l.Id == p).Select(l => l.Name).FirstOrDefaultAsync(ct) : null;
        var hosts = await db.Hosts.AsNoTracking().Where(h => h.LocationId == id).OrderBy(h => h.Name).Take(100).Select(h => new { h.Id, h.Name, h.Importance })
            .ToListAsync(ct);
        ctx.Target("Location", location.Id, location.Name);
        return new { location.Id, location.Name, location.ParentId, Parent = parent, Hosts = hosts, Url = ctx.Link($"locations/{location.Id}") };
    }

    private static async Task<object> KbSearchAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var query = TextOf(args, "query");
        var pattern = $"%{query.Replace("%", "").Replace("_", "")}%";
        var pages = await MachineAccess.Pages(db).Where(p => p.Status == KbPageStatus.Published && p.ArchivedAt == null
                && (EF.Functions.ILike(p.Title, pattern) || EF.Functions.ILike(p.PlainText, pattern) || EF.Functions.ILike(p.TagsText, pattern)))
            .OrderByDescending(p => p.UpdatedAt).Take(10).Select(p => new { p.Id, p.Number, p.Title, p.Summary, p.UpdatedAt }).ToListAsync(ct);
        ctx.Summary = $"{pages.Count} pages for \"{query}\"";
        return pages.Select(p => new
        {
            p.Id, p.Number, Title = ctx.Untrusted("page title", p.Title, 200), Summary = p.Summary is null ? null : ctx.Untrusted("page summary", p.Summary, 400),
            p.UpdatedAt, Url = ctx.Link($"kb/{p.Number}"),
        }).ToList();
    }

    private static async Task<object> KbPageAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var number = IntOf(args, "number");
        var page = await MachineAccess.Pages(db).Where(p => p.Number == number && p.ArchivedAt == null)
            .Select(p => new { p.Id, p.Number, p.Title, p.PlainText, p.UpdatedAt }).SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("page");
        ctx.Target("KbPage", page.Id, $"KB-{page.Number}");
        return new
        {
            page.Id, page.Number, Title = ctx.Untrusted("page title", page.Title, 200), Text = ctx.Untrusted("page", page.PlainText, 6000), page.UpdatedAt,
            Url = ctx.Link($"kb/{page.Number}"),
        };
    }

    private static async Task<object> LocationsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var query = OptionalText(args, "query", 200);
        var parentId = OptionalId(args, "parentId");
        var tree = await ctx.Services.GetRequiredService<LocationTree>().GetAsync(ct);
        var hostCounts = await db.Hosts.AsNoTracking().Where(h => h.LocationId != null).GroupBy(h => h.LocationId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var rows = tree.All
            .Where(l => (query is null || l.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) && (parentId is null || l.ParentId == parentId))
            .OrderBy(l => tree.Path(l.Id), StringComparer.OrdinalIgnoreCase).ToList();
        var page = paging.Slice(rows);
        ctx.Summary = $"{page.Items.Count} of {rows.Count} locations";
        return page.Select(l => new
        {
            l.Id, l.Name, Path = tree.Path(l.Id), l.ParentId, Hosts = hostCounts.GetValueOrDefault(l.Id), Url = ctx.Link($"locations/{l.Id}"),
        });
    }

    private static async Task<object> LocationDetailsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var tree = await ctx.Services.GetRequiredService<LocationTree>().GetAsync(ct);
        var location = tree.All.FirstOrDefault(l => l.Id == id) ?? throw ToolException.NotFound("location");
        var values = await MachineAccess.LocationValuesAsync(db, tree, id, ct);
        var pages = await MachineAccess.Pages(db).Where(p => p.ArchivedAt == null && db.KbLinks.Any(k => k.PageId == p.Id && k.LocationId == id))
            .OrderBy(p => p.Title).Take(50).Select(p => new { p.Id, p.Number, p.Title }).ToListAsync(ct);
        ctx.Target("Location", location.Id, location.Name);
        return new
        {
            location.Id, location.Name, Path = tree.Path(id), location.ParentId,
            Fields = values.Select(v => new { v.Label, v.Key, Value = ctx.Untrusted($"location field {v.Key}", v.Value, 3000), v.InheritedFrom }),
            Children = tree.All.Where(l => l.ParentId == id).OrderBy(l => l.Name).Select(l => new { l.Id, l.Name }),
            Pages = pages.Select(p => new { p.Id, p.Number, Title = ctx.Untrusted("page title", p.Title, 200), Url = ctx.Link($"kb/{p.Number}") }),
            Url = ctx.Link($"locations/{location.Id}/information"),
        };
    }
}
