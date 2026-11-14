using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Knowledge;

/// <summary>
/// The knowledge base's lists (docs/locations-knowledge-plan.md §6.6) and its content search (§6.7): the orders, the
/// periods of <i>most viewed</i> and <i>most edited</i>, the filters (a location with its sub-locations), and the
/// snippets for the page of results only. Also the panels of locations, hosts and sensors (§6.5). Archived pages are
/// left out, unless a list asks for them.
/// </summary>
public sealed class KbPageList(PnmtdDbContext db, LocationTree tree)
{
    public static readonly string[] Sorts = ["newest", "updated", "views", "edits", "title", "relevance"];

    /// <summary>The recent period of <i>most viewed</i> and <i>most edited</i>.</summary>
    public const int RecentDays = 30;

    public async Task<Paged<KbPageListItem>> ListAsync(KbPageQuery query, CancellationToken ct)
    {
        var parsed = KbSearch.Parse(query.Q);
        var q = parsed.Query;
        var sort = (Text.Trimmed(query.Sort) ?? (q is not null ? "relevance" : "newest")).ToLowerInvariant();
        var period = (Text.Trimmed(query.Period) ?? "30d").ToLowerInvariant();
        new ApiValidator()
            .Check(Sorts.Contains(sort), "sort", $"One of {string.Join(", ", Sorts)}.")
            .Check(sort != "relevance" || q is not null, "sort", "Relevance needs words to search for.")
            .Check(period is "30d" or "all", "period", "30d or all.")
            .Check(query.Status is null || Enum.IsDefined(query.Status.Value), "status", "Draft, Published or NeedsReview.")
            .ThrowIfInvalid();
        var all = period == "all";
        var pages = db.KbPages.AsNoTracking();
        if (!query.Archived)
        {
            pages = pages.Where(p => p.ArchivedAt == null);
        }
        if (query.Status is { } status)
        {
            pages = pages.Where(p => p.Status == status);
        }
        var tags = (query.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(KbTags.Normalize).OfType<string>().Concat(parsed.Tags).Distinct().ToList();
        foreach (var tag in tags)
        {
            pages = pages.Where(p => p.Tags.Any(t => t.Tag!.NormalizedName == tag));
        }
        if (query.Location is { } location)
        {
            var within = (await tree.GetAsync(ct)).SelfAndDescendants(location).ToList();
            pages = pages.Where(p => p.Links.Any(l => l.LocationId != null && within.Contains(l.LocationId.Value)));
        }
        if (query.Host is { } host)
        {
            pages = pages.Where(p => p.Links.Any(l => l.HostId == host));
        }
        if (query.Sensor is { } sensor)
        {
            pages = pages.Where(p => p.Links.Any(l => l.SensorId == sensor));
        }
        if (q is not null)
        {
            pages = pages.Where(p => p.SearchVector.Matches(EF.Functions.ToTsQuery(KbSearch.Configuration, q)));
        }
        var (day, instant) = Recent();
        var ordered = sort switch
        {
            "relevance" => pages.OrderByDescending(p => p.SearchVector.RankCoverDensity(EF.Functions.ToTsQuery(KbSearch.Configuration, q!))),
            "updated" => pages.OrderByDescending(p => p.UpdatedAt),
            "views" when all => pages.OrderByDescending(p => p.Views),
            "views" => pages.OrderByDescending(p => db.KbPageViews.Where(v => v.PageId == p.Id && v.Day >= day).Sum(v => (int?)v.Views) ?? 0),
            "edits" when all => pages.OrderByDescending(p => db.KbRevisions.Count(r => r.PageId == p.Id && r.IsEdit)),
            "edits" => pages.OrderByDescending(p => db.KbRevisions.Count(r => r.PageId == p.Id && r.IsEdit && r.At >= instant)),
            "title" => pages.OrderBy(p => p.Title),
            _ => pages.OrderByDescending(p => p.CreatedAt),
        };
        var (number, size) = Paging.Normalize(new PageQuery { Page = query.Page, PageSize = query.PageSize });
        var total = await pages.CountAsync(ct);
        var items = await Project(ordered.ThenByDescending(p => p.Number).Skip((number - 1) * size).Take(size)).ToListAsync(ct);
        if (q is not null && items.Count > 0)
        {
            var snippets = await SnippetsAsync(items.Select(i => i.Id).ToList(), q, ct);
            items = items.Select(i => i with { Snippet = snippets.GetValueOrDefault(i.Id) }).ToList();
        }
        return new Paged<KbPageListItem>(items, number, size, total);
    }

    /// <summary>
    /// ts_headline for the pages shown, never for every match: the plain text around the matches, which are enclosed in
    /// <see cref="KbSearch.MarkStart"/> and <see cref="KbSearch.MarkEnd"/>. Null where the plain text has no match (a
    /// title or tag matched): the list shows the summary instead.
    /// </summary>
    private async Task<Dictionary<Guid, string?>> SnippetsAsync(List<Guid> ids, string q, CancellationToken ct)
    {
        var options = KbSearch.HeadlineOptions;
        var rows = await db.KbPages.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                Snippet = EF.Functions.ToTsQuery(KbSearch.Configuration, q)
                    .GetResultHeadline(KbSearch.Configuration, p.PlainText.Substring(0, KbSearch.HeadlineChars), options),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Snippet is { } s && s.Contains(KbSearch.MarkStart) ? s.Trim() : null);
    }

    /// <summary>
    /// The panels (§6.5): the pages linked to the object, then <i>via</i> what it belongs to, nearest first. A location:
    /// its ancestors, and its hosts (its sub-locations' included). A host: its location and that one's ancestors. A sensor:
    /// its host, then the host's location and its ancestors. A page shows once, where it is nearest.
    /// </summary>
    public async Task<KbRelated> RelatedAsync(Guid? locationId, Guid? hostId, Guid? sensorId, CancellationToken ct)
    {
        if (new[] { locationId, hostId, sensorId }.Count(i => i is not null) != 1)
        {
            throw ApiException.Invalid("locationId", "Exactly one of locationId, hostId and sensorId.");
        }
        var snapshot = await tree.GetAsync(ct);
        var shown = new HashSet<Guid>();
        var groups = new List<KbRelatedGroup>();
        IReadOnlyList<KbPageListItem> own;
        Guid? locationOfHost = null;
        if (sensorId is { } sensor)
        {
            var s = await db.Sensors.AsNoTracking().Where(x => x.Id == sensor).Select(x => new { x.ParentId, Host = x.Parent.Name, x.Parent.LocationId })
                .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Sensor");
            own = await LinkedAsync(l => l.SensorId == sensor, shown, ct);
            await GroupAsync(groups, KbLinkKind.Host, s.ParentId, s.Host, l => l.HostId == s.ParentId, shown, ct);
            locationOfHost = s.LocationId;
        }
        else if (hostId is { } host)
        {
            var h = await db.Hosts.AsNoTracking().Where(x => x.Id == host).Select(x => new { x.LocationId }).SingleOrDefaultAsync(ct)
                ?? throw ApiException.NotFound("Host");
            own = await LinkedAsync(l => l.HostId == host, shown, ct);
            locationOfHost = h.LocationId;
        }
        else
        {
            var location = locationId!.Value;
            if (!snapshot.Contains(location))
            {
                throw ApiException.NotFound("Location");
            }
            own = await LinkedAsync(l => l.LocationId == location, shown, ct);
            await AncestorsAsync(groups, snapshot, location, shown, ct);
            var within = snapshot.SelfAndDescendants(location).ToList();
            var hosts = await db.Hosts.AsNoTracking().Where(h => h.LocationId != null && within.Contains(h.LocationId.Value)).Select(h => h.Id).ToListAsync(ct);
            if (hosts.Count > 0)
            {
                await GroupAsync(groups, KbLinkKind.Host, null, "its hosts", l => l.HostId != null && hosts.Contains(l.HostId.Value), shown, ct);
            }
        }
        if (locationOfHost is { } assigned && snapshot.Find(assigned) is { } node)
        {
            await GroupAsync(groups, KbLinkKind.Location, node.Id, node.Name, l => l.LocationId == node.Id, shown, ct);
            await AncestorsAsync(groups, snapshot, node.Id, shown, ct);
        }
        return new KbRelated(own, groups);
    }

    private async Task AncestorsAsync(List<KbRelatedGroup> groups, LocationTreeSnapshot snapshot, Guid location, HashSet<Guid> shown, CancellationToken ct)
    {
        foreach (var ancestor in snapshot.Ancestors(location))
        {
            await GroupAsync(groups, KbLinkKind.Location, ancestor.Id, ancestor.Name, l => l.LocationId == ancestor.Id, shown, ct);
        }
    }

    private async Task GroupAsync(List<KbRelatedGroup> groups, KbLinkKind kind, Guid? id, string name,
        System.Linq.Expressions.Expression<Func<KbLinkEntity, bool>> link, HashSet<Guid> shown, CancellationToken ct)
    {
        var pages = await LinkedAsync(link, shown, ct);
        if (pages.Count > 0)
        {
            groups.Add(new KbRelatedGroup(kind, id, name, pages));
        }
    }

    /// <summary>The pages (not archived) with a link that matches, by title, without those shown already.</summary>
    private async Task<IReadOnlyList<KbPageListItem>> LinkedAsync(System.Linq.Expressions.Expression<Func<KbLinkEntity, bool>> link, HashSet<Guid> shown,
        CancellationToken ct)
    {
        var ids = await db.KbLinks.AsNoTracking().Where(link).Select(l => l.PageId).Distinct().ToListAsync(ct);
        ids.RemoveAll(shown.Contains);
        if (ids.Count == 0)
        {
            return [];
        }
        var pages = await Project(db.KbPages.AsNoTracking().Where(p => ids.Contains(p.Id) && p.ArchivedAt == null).OrderBy(p => p.Title).ThenBy(p => p.Number))
            .ToListAsync(ct);
        shown.UnionWith(pages.Select(p => p.Id));
        return pages;
    }

    /// <summary>A list's rows, with views and edits of all time and of the recent period.</summary>
    public IQueryable<KbPageListItem> Project(IQueryable<KbPageEntity> pages)
    {
        var (day, instant) = Recent();
        return pages.Select(p => new KbPageListItem(
            p.Id, p.Number, p.Title, p.Status, p.Summary, null,
            p.Tags.OrderBy(t => t.Tag!.Name).Select(t => new KbPageTag(t.Tag!.Name, t.Source)).ToList(),
            db.FileUses.Any(u => u.PageId == p.Id && db.Files.Any(f => f.Id == u.FileId && f.Kind == FileKind.Attachment && f.DeletedAt == null)),
            p.Sensitive, p.CreatedAt, p.CreatedByName, p.UpdatedAt, p.UpdatedByName, p.ArchivedAt, p.Views,
            db.KbPageViews.Where(v => v.PageId == p.Id && v.Day >= day).Sum(v => (int?)v.Views) ?? 0,
            db.KbRevisions.Count(r => r.PageId == p.Id && r.IsEdit),
            db.KbRevisions.Count(r => r.PageId == p.Id && r.IsEdit && r.At >= instant)));
    }

    /// <summary>The first day (PNMT's time zone) and the first instant of the last 30 days.</summary>
    private static (DateOnly Day, DateTime Instant) Recent() =>
        (DateOnly.FromDateTime(Clock.LocalNow).AddDays(-(RecentDays - 1)), Clock.UtcNow.AddDays(-RecentDays));
}

/// <summary>Links as the UI shows them (§6.5): a location's path, a sensor's host, a host's location.</summary>
public static class KbLinkNames
{
    public static async Task<IReadOnlyList<KbLinkItem>> ResolveAsync(PnmtdDbContext db, LocationTree tree, IReadOnlyList<KbLinkRef> links,
        CancellationToken ct)
    {
        if (links.Count == 0)
        {
            return [];
        }
        var snapshot = await tree.GetAsync(ct);
        var locationIds = links.Where(l => l.Kind == KbLinkKind.Location).Select(l => l.Id).ToList();
        var hostIds = links.Where(l => l.Kind == KbLinkKind.Host).Select(l => l.Id).ToList();
        var sensorIds = links.Where(l => l.Kind == KbLinkKind.Sensor).Select(l => l.Id).ToList();
        var hosts = hostIds.Count == 0 ? [] : await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id))
            .Select(h => new { h.Id, h.Name, h.LocationId, h.Location }).ToListAsync(ct);
        var sensors = sensorIds.Count == 0 ? [] : await db.Sensors.AsNoTracking().Where(s => sensorIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, Host = s.Parent.Name }).ToListAsync(ct);
        var result = new List<KbLinkItem>();
        foreach (var id in locationIds)
        {
            result.Add(snapshot.Find(id) is { } node
                ? new KbLinkItem(KbLinkKind.Location, id, node.Name, snapshot.Path(id))
                : new KbLinkItem(KbLinkKind.Location, id, "deleted location", null, false));
        }
        foreach (var id in hostIds)
        {
            result.Add(hosts.FirstOrDefault(h => h.Id == id) is { } h
                ? new KbLinkItem(KbLinkKind.Host, id, h.Name, h.LocationId is { } l && snapshot.Contains(l) ? snapshot.Path(l) : h.Location)
                : new KbLinkItem(KbLinkKind.Host, id, "deleted host", null, false));
        }
        foreach (var id in sensorIds)
        {
            result.Add(sensors.FirstOrDefault(s => s.Id == id) is { } s
                ? new KbLinkItem(KbLinkKind.Sensor, id, s.Name, s.Host)
                : new KbLinkItem(KbLinkKind.Sensor, id, "deleted sensor", null, false));
        }
        return result.OrderBy(l => l.Kind).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
