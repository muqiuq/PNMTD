using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Knowledge;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Responsibilities;

/// <summary>
/// What responsibilities say (docs/responsibilities-plan.md §10.6): the list, one in full, and the global people. The page,
/// the panels of linked objects and the tools for MCP and Cookie read through here. Nothing here depends on HTTP.
/// </summary>
public sealed class ResponsibilityQueries(PnmtdDbContext db, LocationTree tree)
{
    public async Task<ResponsibilityGlobal> GlobalAsync(CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        var people = await PeopleAsync([settings.GlobalResponsibleId, settings.GlobalDeputyId], ct);
        return new ResponsibilityGlobal(Person(people, settings.GlobalResponsibleId), Person(people, settings.GlobalDeputyId), settings.UpdatedAt,
            settings.UpdatedByName);
    }

    /// <summary>
    /// Every responsibility by name, with the global people (§9.2). <paramref name="q"/> searches the name, the short and the
    /// long description and the people's names; <paramref name="attention"/> keeps those with nobody set or a person inactive.
    /// </summary>
    public async Task<ResponsibilityList> ListAsync(string? q, bool attention, CancellationToken ct)
    {
        var query = db.Responsibilities.AsNoTracking();
        if (Text.Pattern(q) is { } pattern)
        {
            query = query.Where(r => EF.Functions.ILike(r.Name, pattern, @"\")
                || (r.Summary != null && EF.Functions.ILike(r.Summary, pattern, @"\"))
                || EF.Functions.ILike(r.PlainText, pattern, @"\")
                || db.Users.Any(u => (u.Id == r.ResponsibleId || u.Id == r.DeputyId) && EF.Functions.ILike(u.DisplayName, pattern, @"\")));
        }
        var rows = await query.OrderBy(r => r.NormalizedName)
            .Select(r => new
            {
                r.Id, r.Name, r.Summary, r.ResponsibleId, r.DeputyId, r.UpdatedAt,
                Links = db.ResponsibilityLinks.Count(l => l.ResponsibilityId == r.Id),
            })
            .ToListAsync(ct);
        var people = await PeopleAsync(rows.SelectMany(r => new[] { r.ResponsibleId, r.DeputyId }), ct);
        var items = rows.Select(r =>
            {
                var responsible = Person(people, r.ResponsibleId);
                var deputy = Person(people, r.DeputyId);
                return new ResponsibilityItem(r.Id, r.Name, r.Summary, responsible, deputy, StandsIn(responsible, deputy),
                    NeedsAttention(responsible, deputy), r.Links, r.UpdatedAt);
            })
            .Where(i => !attention || i.Attention)
            .ToList();
        return new ResponsibilityList(await GlobalAsync(ct), items);
    }

    public async Task<ResponsibilityDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await db.Responsibilities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Responsibility");
        var people = await PeopleAsync([r.ResponsibleId, r.DeputyId], ct);
        var responsible = Person(people, r.ResponsibleId);
        var deputy = Person(people, r.DeputyId);
        return new ResponsibilityDetail(r.Id, r.Name, r.Summary, r.Description, r.Revision, responsible, deputy, StandsIn(responsible, deputy),
            NeedsAttention(responsible, deputy), await GlobalAsync(ct), await LinksAsync(id, ct), await FilesAsync(r.Description, ct), r.CreatedAt,
            r.CreatedByName, r.UpdatedAt, r.UpdatedByName);
    }

    /// <summary>The files the description references (§10.4), for attachment chips.</summary>
    private async Task<IReadOnlyList<FileItem>> FilesAsync(string description, CancellationToken ct)
    {
        var ids = MarkdownText.Read(description).FileIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }
        var files = await db.Files.AsNoTracking().Where(f => ids.Contains(f.Id) && f.DeletedAt == null).ToListAsync(ct);
        return files.Select(FileNames.Item).ToList();
    }

    /// <summary>Its links as a page's links show them (<see cref="KbLinkNames"/>): locations, hosts, then pages, each by name.</summary>
    public async Task<IReadOnlyList<ResponsibilityLinkItem>> LinksAsync(Guid id, CancellationToken ct)
    {
        var links = await db.ResponsibilityLinks.AsNoTracking().Where(l => l.ResponsibilityId == id).ToListAsync(ct);
        var byTarget = links.ToDictionary(RefOf);
        var names = await KbLinkNames.ResolveAsync(db, tree, [.. byTarget.Keys], ct);
        return names.Select(n => new ResponsibilityLinkItem(byTarget[new KbLinkRef(n.Kind, n.Id)].Id, n.Kind, n.Id, n.Name, n.Detail, n.Exists,
            n.Number, n.Archived)).ToList();
    }

    internal async Task<ResponsibilitySettingsEntity> SettingsAsync(CancellationToken ct) =>
        await db.ResponsibilitySettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == ResponsibilitySettingsEntity.SingletonId, ct)
        ?? new ResponsibilitySettingsEntity { Id = ResponsibilitySettingsEntity.SingletonId };

    /// <summary>The people these ids name, by id: their display name and whether they are active.</summary>
    internal async Task<IReadOnlyDictionary<Guid, ResponsibilityPerson>> PeopleAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<Guid, ResponsibilityPerson>();
        }
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => new ResponsibilityPerson(u.Id, u.DisplayName, u.IsActive), ct);
    }

    internal static ResponsibilityPerson? Person(IReadOnlyDictionary<Guid, ResponsibilityPerson> people, Guid? id) =>
        id is { } some && people.TryGetValue(some, out var person) ? person : null;

    /// <summary>Neither its responsible nor its deputy is set and active: the global people stand in (§5).</summary>
    public static bool StandsIn(ResponsibilityPerson? responsible, ResponsibilityPerson? deputy) =>
        responsible is not { Active: true } && deputy is not { Active: true };

    /// <summary>Nobody set, or a person inactive (§9.2, <i>Needs attention</i>).</summary>
    public static bool NeedsAttention(ResponsibilityPerson? responsible, ResponsibilityPerson? deputy) =>
        (responsible is null && deputy is null) || responsible is { Active: false } || deputy is { Active: false };

    internal static KbLinkRef RefOf(ResponsibilityLinkEntity link) =>
        link.PageId is { } page ? new KbLinkRef(KbLinkKind.Page, page)
        : link.LocationId is { } location ? new KbLinkRef(KbLinkKind.Location, location)
        : new KbLinkRef(KbLinkKind.Host, link.HostId!.Value);
}
