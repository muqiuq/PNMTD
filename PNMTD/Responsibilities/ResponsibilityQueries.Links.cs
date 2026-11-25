using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Locations;
using PNMTD.Services.Domain;

namespace PNMTD.Responsibilities;

/// <summary>
/// The Links tab's reads (docs/responsibilities-extensions-plan.md §5): every link of every responsibility, and the hosts and locations
/// no responsibility links itself. Filtered in the database where it can be, then named, searched, sorted and paged in memory: a
/// responsibility links at most <see cref="ResponsibilityService.MaxLinks"/> objects, and the location paths come from the tree.
/// </summary>
public sealed partial class ResponsibilityQueries
{
    /// <summary>Every link, one row each: by responsibility, person, kind and location; searched in the names and the <i>Where</i>.</summary>
    public async Task<Paged<ResponsibilityLinkRow>> LinkRowsAsync(ResponsibilityLinksQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "responsibility", "responsibility", "object", "kind", "linked");
        var snapshot = await tree.GetAsync(ct);
        var links = db.ResponsibilityLinks.AsNoTracking();
        if (query.Responsibility is { Count: > 0 } wanted)
        {
            links = links.Where(l => wanted.Contains(l.ResponsibilityId));
        }
        if (query.Person is { } person)
        {
            links = links.Where(l => db.Responsibilities.Any(r => r.Id == l.ResponsibilityId && (r.ResponsibleId == person || r.DeputyId == person)));
        }
        if (query.Kind is { Count: > 0 } kinds)
        {
            var pages = kinds.Contains(KbLinkKind.Page);
            var locations = kinds.Contains(KbLinkKind.Location);
            var hosts = kinds.Contains(KbLinkKind.Host);
            links = links.Where(l => (pages && l.PageId != null) || (locations && l.LocationId != null) || (hosts && l.HostId != null));
        }
        if (Within(snapshot, query.Location, query.Below) is { } within)
        {
            // Pages have no location: a location filter keeps hosts in it and the locations themselves.
            links = links.Where(l => (l.LocationId != null && within.Contains(l.LocationId.Value))
                || (l.HostId != null && db.Hosts.Any(h => h.Id == l.HostId && h.LocationId != null && within.Contains(h.LocationId.Value))));
        }
        var rows = await (from l in links
                          join r in db.Responsibilities on l.ResponsibilityId equals r.Id
                          select new { Link = l, r.Name, r.Color }).ToListAsync(ct);
        var hostIds = rows.Where(x => x.Link.HostId != null).Select(x => x.Link.HostId!.Value).Distinct().ToList();
        var pageIds = rows.Where(x => x.Link.PageId != null).Select(x => x.Link.PageId!.Value).Distinct().ToList();
        var hostNames = await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, h => new { h.Name, h.LocationId, h.Location }, ct);
        var pageNames = await db.KbPages.AsNoTracking().Where(p => pageIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => new { p.Number, p.Title, Archived = p.ArchivedAt != null }, ct);

        var items = rows.Select(x =>
            {
                var l = x.Link;
                var who = new ResponsibilityBrief(l.ResponsibilityId, x.Name, x.Color);
                if (l.HostId is { } host)
                {
                    var h = hostNames.GetValueOrDefault(host);
                    return new ResponsibilityLinkRow(l.Id, who, KbLinkKind.Host, host, h?.Name ?? "deleted host", h is null ? null : HostWhere(snapshot, h.LocationId, h.Location),
                        null, false, l.CreatedAt, l.CreatedByName);
                }
                if (l.PageId is { } page)
                {
                    var p = pageNames.GetValueOrDefault(page);
                    return new ResponsibilityLinkRow(l.Id, who, KbLinkKind.Page, page, p?.Title ?? "deleted page", null, p?.Number, p?.Archived ?? false,
                        l.CreatedAt, l.CreatedByName);
                }
                var location = l.LocationId!.Value;
                return new ResponsibilityLinkRow(l.Id, who, KbLinkKind.Location, location, snapshot.Find(location)?.Name ?? "deleted location",
                    ParentWhere(snapshot, location), null, false, l.CreatedAt, l.CreatedByName);
            })
            .Where(i => string.IsNullOrWhiteSpace(query.Search) || Text.Contains(i.Name, query.Search) || Text.Contains(i.Detail, query.Search)
                || Text.Contains(i.Responsibility.Name, query.Search) || (i.Number is { } n && Text.Contains($"KB-{n}", query.Search)));

        var sorted = field switch
        {
            "object" => (descending ? items.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase) : items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                .ThenBy(i => i.Responsibility.Name, StringComparer.OrdinalIgnoreCase),
            "kind" => (descending ? items.OrderByDescending(i => i.Kind) : items.OrderBy(i => i.Kind)).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Responsibility.Name, StringComparer.OrdinalIgnoreCase),
            "linked" => (descending ? items.OrderByDescending(i => i.CreatedAt) : items.OrderBy(i => i.CreatedAt)).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => (descending ? items.OrderByDescending(i => i.Responsibility.Name, StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(i => i.Responsibility.Name, StringComparer.OrdinalIgnoreCase))
                .ThenBy(i => i.Responsibility.Id).ThenBy(i => i.Kind).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        return Paging.Page(sorted.ThenBy(i => i.Id), query);
    }

    /// <summary>
    /// The hosts or the locations no responsibility links itself (§5.1), each with the responsibilities that link its location or a parent
    /// of it. With <c>covered</c>, those are left out: they count as covered in this view, and nowhere else (first plan, decision 6).
    /// </summary>
    public async Task<Paged<ResponsibilityUnlinkedRow>> UnlinkedAsync(KbLinkKind kind, ResponsibilityUnlinkedQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "object", "object", "where");
        var snapshot = await tree.GetAsync(ct);
        var locationLinks = await (from l in db.ResponsibilityLinks.AsNoTracking()
                                   where l.LocationId != null
                                   join r in db.Responsibilities on l.ResponsibilityId equals r.Id
                                   select new { Location = l.LocationId!.Value, r.Id, r.Name, r.Color }).ToListAsync(ct);
        var byLocation = locationLinks.GroupBy(x => x.Location)
            .ToDictionary(g => g.Key, g => g.Select(x => new ResponsibilityBrief(x.Id, x.Name, x.Color)).ToList());
        IReadOnlyList<ResponsibilityBrief> Through(Guid? location, bool self) =>
            location is not { } id || !snapshot.Contains(id) ? []
            : (self ? snapshot.Lineage(id) : snapshot.Ancestors(id)).SelectMany(n => byLocation.GetValueOrDefault(n.Id) ?? [])
                .DistinctBy(b => b.Id).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var within = Within(snapshot, query.Location, query.Below);

        List<ResponsibilityUnlinkedRow> rows;
        if (kind == KbLinkKind.Host)
        {
            var hosts = db.Hosts.AsNoTracking().Where(h => !db.ResponsibilityLinks.Any(l => l.HostId == h.Id));
            if (within is not null)
            {
                hosts = hosts.Where(h => h.LocationId != null && within.Contains(h.LocationId.Value));
            }
            rows = (await hosts.Select(h => new { h.Id, h.Name, h.LocationId, h.Location }).ToListAsync(ct))
                .Select(h => new ResponsibilityUnlinkedRow(h.Id, KbLinkKind.Host, h.Name, HostWhere(snapshot, h.LocationId, h.Location), Through(h.LocationId, true)))
                .ToList();
        }
        else
        {
            var linked = locationLinks.Select(x => x.Location).ToHashSet();
            rows = snapshot.All.Where(n => !linked.Contains(n.Id) && (within is null || within.Contains(n.Id)))
                .Select(n => new ResponsibilityUnlinkedRow(n.Id, KbLinkKind.Location, n.Name, ParentWhere(snapshot, n.Id), Through(n.Id, false)))
                .ToList();
        }
        var shown = rows.Where(r => !query.Covered || r.ThroughLocation.Count == 0)
            .Where(r => string.IsNullOrWhiteSpace(query.Search) || Text.Contains(r.Name, query.Search) || Text.Contains(r.Detail, query.Search));
        var sorted = field == "where"
            ? (descending ? shown.OrderByDescending(r => r.Detail ?? "", StringComparer.OrdinalIgnoreCase) : shown.OrderBy(r => r.Detail ?? "", StringComparer.OrdinalIgnoreCase))
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            : (descending ? shown.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase) : shown.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                .ThenBy(r => r.Detail ?? "", StringComparer.OrdinalIgnoreCase);
        return Paging.Page(sorted.ThenBy(r => r.Id), query);
    }

    /// <summary>The location and, with <paramref name="below"/>, its sub-locations; null without a location filter.</summary>
    private static List<Guid>? Within(LocationTreeSnapshot snapshot, Guid? location, bool below) =>
        location is not { } id ? null : below ? [.. snapshot.SelfAndDescendants(id)] : snapshot.Contains(id) ? [id] : [];

    /// <summary>Where a host is: its location's path, or the text of a host without one.</summary>
    private static string? HostWhere(LocationTreeSnapshot snapshot, Guid? locationId, string? location) =>
        locationId is { } l && snapshot.Contains(l) ? snapshot.Path(l) : location;

    /// <summary>Where a location is: its parent's path; null at the top.</summary>
    private static string? ParentWhere(LocationTreeSnapshot snapshot, Guid location) =>
        snapshot.Find(location)?.ParentId is { } parent && snapshot.Contains(parent) ? snapshot.Path(parent) : null;
}
