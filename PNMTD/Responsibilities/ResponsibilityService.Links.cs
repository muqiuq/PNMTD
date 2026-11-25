using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Responsibilities;

/// <summary>
/// Changing many links at once, from the Links tab (docs/responsibilities-extensions-plan.md §5): linking objects to a responsibility,
/// unlinking, and moving or copying links to another responsibility. As the single link and unlink: a history row per link, the limit
/// of <see cref="MaxLinks"/> per responsibility, and one audit entry per responsibility, with every object as a subject so that its own
/// History shows it.
/// </summary>
public sealed partial class ResponsibilityService
{
    /// <summary>The most a bulk change takes at once (§5.1).</summary>
    public const int MaxBulk = 500;

    /// <summary>Links pages, locations and hosts to one responsibility; those linked already or gone meanwhile are skipped.</summary>
    public async Task<ResponsibilityBulkResult> LinkManyAsync(ResponsibilityBulkLinkRequest request, CancellationToken ct)
    {
        var targets = request.Targets ?? [];
        new ApiValidator()
            .Check(request.ResponsibilityId is not null, "responsibilityId", "Which responsibility.")
            .Check(targets.Count is >= 1 and <= MaxBulk, "targets", $"1 to {MaxBulk} pages, locations and hosts.")
            .Check(targets.All(t => (t.Kind is KbLinkKind.Page or KbLinkKind.Location or KbLinkKind.Host) && t.Id is not null), "targets",
                "Pages, locations and hosts, each with its id.")
            .ThrowIfInvalid();
        var r = await db.Responsibilities.SingleOrDefaultAsync(x => x.Id == request.ResponsibilityId, ct) ?? throw ApiException.NotFound("Responsibility");
        var refs = targets.Select(t => new KbLinkRef(t.Kind!.Value, t.Id!.Value)).Distinct().ToList();
        var names = await NamesAsync(refs, ct);
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var (added, skipped) = await AddLinksAsync(r, refs, names, null, now, by, ct);
        if (added.Count > 0)
        {
            Touch(r, now, by);
            RecordLinks(AuditEvents.ResponsibilityLinked, r, "linked", added);
        }
        else
        {
            AuditLog.Target(Target(r));
            AuditLog.Summary($"{r.Name}: nothing linked, {Count(skipped.Count, "object")} skipped");
        }
        await db.SaveChangesAsync(ct);
        return new ResponsibilityBulkResult(added.Count, 0, skipped);
    }

    /// <summary>Unlinks links of any responsibilities; links gone meanwhile are left out.</summary>
    public async Task<ResponsibilityBulkResult> UnlinkManyAsync(ResponsibilityBulkUnlinkRequest request, CancellationToken ct)
    {
        var ids = ValidLinkIds(request.LinkIds);
        var links = await db.ResponsibilityLinks.Where(l => ids.Contains(l.Id)).ToListAsync(ct);
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        var removed = await RemoveLinksAsync(links, null, now, by, ct);
        if (removed == 0)
        {
            AuditLog.Summary("Nothing unlinked: the links were gone already");
        }
        await db.SaveChangesAsync(ct);
        return new ResponsibilityBulkResult(0, removed, []);
    }

    /// <summary>
    /// The links' objects linked to another responsibility (§5.1). Without <c>keep</c> they leave their own responsibilities (<i>Move
    /// to…</i>); with it they stay (<i>Also link to…</i>). An object the target links already is skipped, and on a move only unlinked
    /// from its source. In one transaction.
    /// </summary>
    public async Task<ResponsibilityBulkResult> MoveLinksAsync(ResponsibilityMoveLinksRequest request, CancellationToken ct)
    {
        new ApiValidator().Check(request.ResponsibilityId is not null, "responsibilityId", "Which responsibility.").ThrowIfInvalid();
        var ids = ValidLinkIds(request.LinkIds);
        var to = await db.Responsibilities.SingleOrDefaultAsync(x => x.Id == request.ResponsibilityId, ct) ?? throw ApiException.NotFound("Responsibility");
        var sources = (await db.ResponsibilityLinks.Where(l => ids.Contains(l.Id)).ToListAsync(ct)).Where(l => l.ResponsibilityId != to.Id).ToList();
        var refs = sources.Select(ResponsibilityQueries.RefOf).Distinct().ToList();
        var names = await NamesAsync(refs, ct);
        var from = await db.Responsibilities.AsNoTracking().Where(x => sources.Select(l => l.ResponsibilityId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var by = LocationActor.Of(accessor);
        var now = Clock.UtcNow;
        string? Origin(KbLinkRef target) => request.Keep ? null
            : string.Join(", ", sources.Where(l => ResponsibilityQueries.RefOf(l) == target).Select(l => from.GetValueOrDefault(l.ResponsibilityId)).OfType<string>()
                .Distinct().Order(StringComparer.OrdinalIgnoreCase)) is { Length: > 0 } owners ? $"moved from {owners}" : null;
        var (added, skipped) = await AddLinksAsync(to, refs, names, Origin, now, by, ct);
        if (added.Count > 0)
        {
            Touch(to, now, by);
            RecordLinks(AuditEvents.ResponsibilityLinked, to, request.Keep ? "also linked" : "moved here", added);
        }
        var removed = request.Keep ? 0 : await RemoveLinksAsync(sources, $"moved to {to.Name}", now, by, ct);
        if (added.Count == 0 && removed == 0)
        {
            AuditLog.Target(Target(to));
            AuditLog.Summary($"{to.Name}: nothing changed, {Count(skipped.Count, "object")} linked already");
        }
        await db.SaveChangesAsync(ct);
        return new ResponsibilityBulkResult(added.Count, removed, skipped);
    }

    private static List<Guid> ValidLinkIds(IReadOnlyList<Guid>? linkIds)
    {
        var ids = (linkIds ?? []).Distinct().ToList();
        new ApiValidator().Check(ids.Count is >= 1 and <= MaxBulk, "linkIds", $"1 to {MaxBulk} links.").ThrowIfInvalid();
        return ids;
    }

    /// <summary>
    /// New links of one responsibility, with their history rows: those it links already, and those that are gone, are skipped. The limit
    /// counts before anything is written: if they don't all fit, nothing is linked (409 <c>too_many_links</c>).
    /// </summary>
    private async Task<(List<(KbLinkRef Ref, string What)> Added, List<ResponsibilitySkipped> Skipped)> AddLinksAsync(ResponsibilityEntity r,
        IReadOnlyList<KbLinkRef> refs, IReadOnlyDictionary<KbLinkRef, KbLinkItem> names, Func<KbLinkRef, string?>? note, DateTime now,
        LocationActor by, CancellationToken ct)
    {
        var existing = (await db.ResponsibilityLinks.AsNoTracking().Where(l => l.ResponsibilityId == r.Id).ToListAsync(ct))
            .Select(ResponsibilityQueries.RefOf).ToHashSet();
        var skipped = new List<ResponsibilitySkipped>();
        var adding = new List<(KbLinkRef Ref, string What)>();
        foreach (var target in refs)
        {
            if (!names.TryGetValue(target, out var item) || !item.Exists)
            {
                skipped.Add(new ResponsibilitySkipped(target.Kind, target.Id, item?.Name ?? $"a deleted {Kind(target.Kind)}", ResponsibilityErrors.NotFound));
            }
            else if (existing.Contains(target))
            {
                skipped.Add(new ResponsibilitySkipped(target.Kind, target.Id, item.Name, ResponsibilityErrors.AlreadyLinked));
            }
            else
            {
                adding.Add((target, What(item)));
            }
        }
        if (existing.Count + adding.Count > MaxLinks)
        {
            throw ApiException.Conflict(ResponsibilityErrors.TooManyLinks,
                $"{r.Name} links {existing.Count} of at most {MaxLinks}; {Math.Max(0, MaxLinks - existing.Count)} more fit.");
        }
        foreach (var (target, what) in adding)
        {
            db.ResponsibilityLinks.Add(new ResponsibilityLinkEntity
            {
                Id = Guid.NewGuid(), ResponsibilityId = r.Id, PageId = target.Kind == KbLinkKind.Page ? target.Id : null,
                LocationId = target.Kind == KbLinkKind.Location ? target.Id : null, HostId = target.Kind == KbLinkKind.Host ? target.Id : null,
                CreatedAt = now, CreatedBy = by.Id, CreatedByName = by.Name,
            });
            History(r.Id, now, by, ResponsibilityHistoryKind.LinkAdded, note?.Invoke(target) is { } n ? $"{what}, {n}" : what);
        }
        return (adding, skipped);
    }

    /// <summary>Removes links, each with a history row on its responsibility, and one audit entry per responsibility.</summary>
    private async Task<int> RemoveLinksAsync(IReadOnlyList<ResponsibilityLinkEntity> links, string? note, DateTime now, LocationActor by,
        CancellationToken ct)
    {
        if (links.Count == 0)
        {
            return 0;
        }
        var owners = links.Select(l => l.ResponsibilityId).Distinct().ToList();
        var responsibilities = await db.Responsibilities.Where(x => owners.Contains(x.Id)).ToListAsync(ct);
        var names = await NamesAsync(links.Select(ResponsibilityQueries.RefOf).Distinct().ToList(), ct);
        foreach (var r in responsibilities.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var removed = new List<(KbLinkRef Ref, string What)>();
            foreach (var link in links.Where(l => l.ResponsibilityId == r.Id))
            {
                var target = ResponsibilityQueries.RefOf(link);
                var what = names.TryGetValue(target, out var item) && item.Exists ? What(item) : $"a deleted {Kind(target.Kind)}";
                db.ResponsibilityLinks.Remove(link);
                History(r.Id, now, by, ResponsibilityHistoryKind.LinkRemoved, note is null ? what : $"{what}, {note}");
                removed.Add((target, what));
            }
            Touch(r, now, by);
            RecordLinks(AuditEvents.ResponsibilityUnlinked, r, note is null ? "unlinked" : $"unlinked, {note},", removed);
        }
        return links.Count;
    }

    /// <summary>"Network: linked host sw-core-01, host sw-core-02 and 10 more", with every object as a subject.</summary>
    private void RecordLinks(string eventType, ResponsibilityEntity r, string verb, IReadOnlyList<(KbLinkRef Ref, string What)> objects)
    {
        var names = objects.Select(o => o.What).ToList();
        var text = names.Count switch
        {
            1 => names[0],
            2 or 3 => $"{string.Join(", ", names[..^1])} and {names[^1]}",
            _ => $"{names[0]}, {names[1]} and {names.Count - 2} more",
        };
        AuditLog.Record(db, eventType, $"{r.Name}: {verb} {text}", AuditWho.From(accessor),
            AuditTarget.Of(AuditTargets.Responsibility, r.Id, r.Name, [.. objects.Select(o => (Guid?)o.Ref.Id)]));
    }

    /// <summary>The objects as a page's links name them, by reference.</summary>
    private async Task<IReadOnlyDictionary<KbLinkRef, KbLinkItem>> NamesAsync(IReadOnlyList<KbLinkRef> refs, CancellationToken ct) =>
        (await KbLinkNames.ResolveAsync(db, tree, refs, ct)).ToDictionary(n => new KbLinkRef(n.Kind, n.Id));

    /// <summary>"page KB-42 Replacing a UPS battery", "location Zurich › Building A", "host sw-core-01": as the single link says it.</summary>
    private static string What(KbLinkItem item) => item.Kind switch
    {
        KbLinkKind.Page => $"page KB-{item.Number} {item.Name}",
        KbLinkKind.Location => $"location {item.Detail ?? item.Name}",
        _ => $"host {item.Name}",
    };

    private static string Kind(KbLinkKind kind) => kind.ToString().ToLowerInvariant();
}
