using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Knowledge;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Services.Domain;

namespace PNMTD.Responsibilities;

/// <summary>
/// What responsibilities say (docs/responsibilities-plan.md §10.6): the list, one in full, and the global people. The page,
/// the panels of linked objects and the tools for MCP and Cookie read through here. Nothing here depends on HTTP.
/// </summary>
public sealed class ResponsibilityQueries(PnmtdDbContext db, LocationTree tree, RedmineWorker redmine, IOptionsMonitor<RedmineOptions> redmineOptions)
{
    /// <summary>Whether the Redmine parts are shown at all (§6.1): without Redmine configured, combinations stay but aren't shown.</summary>
    public bool RedmineConfigured => redmineOptions.CurrentValue.Enabled;

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
                Combinations = db.ResponsibilityRedmine.Count(c => c.ResponsibilityId == r.Id),
                // A combination whose user was removed or deactivated (§6.5).
                Unhandled = db.ResponsibilityRedmine.Any(c => c.ResponsibilityId == r.Id
                    && (c.UserId == null || !db.Users.Any(u => u.Id == c.UserId && u.IsActive))),
            })
            .ToListAsync(ct);
        var people = await PeopleAsync(rows.SelectMany(r => new[] { r.ResponsibleId, r.DeputyId }), ct);
        var withRedmine = RedmineConfigured;
        var items = rows.Select(r =>
            {
                var responsible = Person(people, r.ResponsibleId);
                var deputy = Person(people, r.DeputyId);
                return new ResponsibilityItem(r.Id, r.Name, r.Summary, responsible, deputy, StandsIn(responsible, deputy),
                    NeedsAttention(responsible, deputy) || (withRedmine && r.Unhandled), r.Links, withRedmine ? r.Combinations : 0, r.UpdatedAt);
            })
            .Where(i => !attention || i.Attention)
            .ToList();
        return new ResponsibilityList(await GlobalAsync(ct), items, withRedmine);
    }

    public async Task<ResponsibilityDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var r = await db.Responsibilities.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound("Responsibility");
        var people = await PeopleAsync([r.ResponsibleId, r.DeputyId], ct);
        var responsible = Person(people, r.ResponsibleId);
        var deputy = Person(people, r.DeputyId);
        var combinations = RedmineConfigured ? await CombinationsAsync(id, null, null, ct) : [];
        return new ResponsibilityDetail(r.Id, r.Name, r.Summary, r.Description, r.Revision, responsible, deputy, StandsIn(responsible, deputy),
            NeedsAttention(responsible, deputy) || combinations.Any(c => c.User is not { Active: true }), await GlobalAsync(ct),
            await LinksAsync(id, ct), await FilesAsync(r.Description, ct), RedmineConfigured, combinations, r.CreatedAt, r.CreatedByName,
            r.UpdatedAt, r.UpdatedByName);
    }

    /// <summary>
    /// Redmine combinations (§6, §9.5): one responsibility's, or all of them, by project, responsibility or user. Names come from
    /// the worker's lookups where it has them (never asking Redmine), else the copies of the last save.
    /// </summary>
    public async Task<IReadOnlyList<RedmineCombinationItem>> CombinationsAsync(Guid? responsibilityId, int? project, Guid? user, CancellationToken ct)
    {
        var query = db.ResponsibilityRedmine.AsNoTracking();
        if (responsibilityId is { } r)
        {
            query = query.Where(c => c.ResponsibilityId == r);
        }
        if (project is { } p)
        {
            query = query.Where(c => c.ProjectId == p);
        }
        if (user is { } u)
        {
            query = query.Where(c => c.UserId == u);
        }
        var rows = await (from c in query join x in db.Responsibilities on c.ResponsibilityId equals x.Id select new { Row = c, x.Name }).ToListAsync(ct);
        var people = await PeopleAsync(rows.Select(x => x.Row.UserId), ct);
        var ids = people.Keys.ToList();
        var linked = ids.Count == 0 ? [] : await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id) && x.RedmineUserId != null).Select(x => x.Id).ToListAsync(ct);
        var lookups = redmine.CachedLookups();
        var projects = lookups is null ? new Dictionary<int, RedmineCombinationProject>() : ProjectsOf(lookups).ToDictionary(x => x.Id);
        return rows.Select(x =>
            {
                var c = x.Row;
                var known = projects.GetValueOrDefault(c.ProjectId);
                var tracker = c.TrackerId is { } t ? lookups?.Trackers.FirstOrDefault(k => k.Id == t) : null;
                var warning = lookups is null ? null
                    : known is null || !lookups.Allowed.Contains(c.ProjectId) ? "Not a project PNMT reads"
                    : c.TrackerId is { } missing && tracker is null ? $"Unknown tracker #{missing}"
                    : null;
                return new RedmineCombinationItem(c.Id, c.ResponsibilityId, x.Name, c.ProjectId, known?.Name ?? c.ProjectName,
                    known?.Path ?? c.ProjectName, c.TrackerId, tracker?.Name ?? c.TrackerName, Person(people, c.UserId),
                    c.UserId is { } id && linked.Contains(id), c.Note, warning, c.UpdatedAt);
            })
            .OrderBy(i => i.ProjectPath, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.ProjectId)
            .ThenBy(i => i.TrackerId is null ? 0 : 1).ThenBy(i => i.TrackerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The projects PNMT reads, each with its path through its parents among them.</summary>
    internal static IReadOnlyList<RedmineCombinationProject> ProjectsOf(RedmineLookups lookups)
    {
        var allowed = lookups.Projects.Where(p => lookups.Allowed.Contains(p.Id)).ToDictionary(p => p.Id);
        (string Path, int Depth) PathOf(RmProject p)
        {
            var names = new List<string> { p.Name };
            var seen = new HashSet<int> { p.Id };
            for (var parent = p.Parent?.Id; parent is { } id && allowed.TryGetValue(id, out var up) && seen.Add(id); parent = up.Parent?.Id)
            {
                names.Insert(0, up.Name);
            }
            return (string.Join(" › ", names), names.Count - 1);
        }
        return allowed.Values.Select(p =>
            {
                var (path, depth) = PathOf(p);
                return new RedmineCombinationProject(p.Id, p.Name, p.Parent?.Id, path, depth);
            })
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    /// <summary>
    /// The responsibilities that link this page, location or host (§8), by name: only its own links, nothing inherited
    /// (decision 6). Empty when none does, and the panel then shows nothing.
    /// </summary>
    public async Task<IReadOnlyList<ResponsibilityRef>> LinkedAsync(KbLinkKind kind, Guid id, CancellationToken ct)
    {
        var links = db.ResponsibilityLinks.AsNoTracking();
        var ids = kind switch
        {
            KbLinkKind.Page => links.Where(l => l.PageId == id),
            KbLinkKind.Location => links.Where(l => l.LocationId == id),
            _ => links.Where(l => l.HostId == id),
        };
        var rows = await db.Responsibilities.AsNoTracking().Where(r => ids.Any(l => l.ResponsibilityId == r.Id)).OrderBy(r => r.NormalizedName)
            .Select(r => new { r.Id, r.Name, r.Summary, r.ResponsibleId, r.DeputyId }).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }
        var global = await GlobalAsync(ct);
        var people = await PeopleAsync(rows.SelectMany(r => new[] { r.ResponsibleId, r.DeputyId }), ct);
        return rows.Select(r =>
            {
                var responsible = Person(people, r.ResponsibleId);
                var deputy = Person(people, r.DeputyId);
                return new ResponsibilityRef(r.Id, r.Name, r.Summary, responsible, deputy, StandsIn(responsible, deputy), global.Responsible, global.Deputy);
            })
            .ToList();
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
