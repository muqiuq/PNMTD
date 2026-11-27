using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Auth;

/// <summary>
/// Groups and their rights (docs/audit-redmine-zabbix-plan.md §5.1, §5.5), for admins only. A change drops its members'
/// cached sessions, so it applies from their next request; nobody is signed out.
/// </summary>
public sealed class GroupService(PnmtdDbContext db, SessionCache sessions, IHttpContextAccessor accessor, IConfiguration configuration)
{
    public async Task<IReadOnlyList<GroupItem>> ListAsync(CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().Include(g => g.Rights).OrderBy(g => g.NormalizedName)
            .Select(g => new { Group = g, Members = g.Members.Count }).ToListAsync(ct);
        return groups.Select(g => new GroupItem(g.Group.Id, g.Group.Name, g.Group.Description, VisibleLevels(g.Group), g.Members, g.Group.CreatedAt,
            g.Group.UpdatedAt)).ToList();
    }

    public async Task<GroupDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().Include(g => g.Rights).SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw ApiException.NotFound("Group");
        var members = await db.UserGroups.AsNoTracking().Where(m => m.GroupId == id)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u).OrderBy(u => u.Username).Select(u => new Ref(u.Id, u.Username)).ToListAsync(ct);
        return new GroupDetail(group.Id, group.Name, group.Description, VisibleLevels(group), members, group.CreatedAt, group.UpdatedAt);
    }

    public async Task<GroupDetail> CreateAsync(GroupRequest request, CancellationToken ct)
    {
        var (name, description, rights) = await ValidateAsync(request, null, ct);
        var now = Clock.UtcNow;
        var group = new GroupEntity
        {
            Id = Guid.NewGuid(), Name = name, NormalizedName = Normalize(name), Description = description, CreatedAt = now, UpdatedAt = now,
            Rights = rights.Select(kv => new GroupRightEntity { Area = kv.Key, Level = kv.Value }).ToList(),
        };
        db.Groups.Add(group);
        AuditLog.Record(db, AuditEvents.GroupCreated, $"{name}: {Summary(rights)}", AuditWho.From(accessor), Target(group));
        await db.SaveChangesAsync(ct);
        return await GetAsync(group.Id, ct);
    }

    public async Task<GroupDetail> UpdateAsync(Guid id, GroupRequest request, CancellationToken ct)
    {
        var group = await db.Groups.Include(g => g.Rights).SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw ApiException.NotFound("Group");
        var (name, description, rights) = await ValidateAsync(request, id, ct);
        var who = AuditWho.From(accessor);
        if (group.Name != name || group.Description != description)
        {
            AuditLog.Record(db, AuditEvents.GroupUpdated, group.Name != name ? $"{group.Name} renamed {name}" : $"{name}: description changed", who, Target(group));
            group.Name = name;
            group.NormalizedName = Normalize(name);
            group.Description = description;
        }
        var before = Levels(group);
        // An area the deployment hides (docs/licences-plan.md §9, §13.2) keeps its stored row: the form doesn't show it.
        foreach (var hidden in before.Where(kv => !RightsCatalog.IsVisible(kv.Key, configuration)))
        {
            rights[hidden.Key] = hidden.Value;
        }
        var changes = RightsCatalog.Areas.Select(a => a.Area)
            .Where(a => before.GetValueOrDefault(a, Level.None) != rights.GetValueOrDefault(a, Level.None))
            .Select(a => $"{RightsCatalog.Of(a).Label}: {Show(before.GetValueOrDefault(a, Level.None))} → {Show(rights.GetValueOrDefault(a, Level.None))}")
            .ToList();
        if (changes.Count > 0)
        {
            AuditLog.Record(db, AuditEvents.GroupRightsChanged, $"{name}: {string.Join(", ", changes)}", who, Target(group));
            db.GroupRights.RemoveRange(group.Rights);
            group.Rights = rights.Select(kv => new GroupRightEntity { GroupId = group.Id, Area = kv.Key, Level = kv.Value }).ToList();
        }
        group.UpdatedAt = Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await ForgetMembersAsync(id, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Its members lose what it gave them; their accounts stay.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw ApiException.NotFound("Group");
        var members = await MemberNamesAsync(id, ct);
        var memberIds = await db.UserGroups.Where(m => m.GroupId == id).Select(m => m.UserId).ToListAsync(ct);
        AuditLog.Record(db, AuditEvents.GroupDeleted, $"{group.Name} deleted; members then: {(members.Count == 0 ? "none" : string.Join(", ", members))}",
            AuditWho.From(accessor), Target(group));
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        memberIds.ForEach(sessions.Forget);
    }

    public async Task<GroupDetail> SetMembersAsync(Guid id, GroupMembersRequest request, CancellationToken ct)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw ApiException.NotFound("Group");
        var wanted = (request.UserIds ?? []).Distinct().ToList();
        var users = await db.Users.Where(u => wanted.Contains(u.Id)).Select(u => new { u.Id, u.Username }).ToListAsync(ct);
        new ApiValidator().Check(users.Count == wanted.Count, "userIds", "Unknown user.").ThrowIfInvalid();
        var current = await db.UserGroups.Where(m => m.GroupId == id).ToListAsync(ct);
        var removed = current.Where(m => !wanted.Contains(m.UserId)).ToList();
        var added = wanted.Where(u => current.All(m => m.UserId != u)).ToList();
        if (removed.Count + added.Count > 0)
        {
            var names = await db.Users.Where(u => removed.Select(m => m.UserId).Contains(u.Id)).Select(u => u.Username).ToListAsync(ct);
            var parts = new List<string>();
            if (added.Count > 0)
            {
                parts.Add("added " + string.Join(", ", users.Where(u => added.Contains(u.Id)).Select(u => u.Username).Order()));
            }
            if (removed.Count > 0)
            {
                parts.Add("removed " + string.Join(", ", names.Order()));
            }
            AuditLog.Record(db, AuditEvents.GroupMembersChanged, $"{group.Name}: {string.Join("; ", parts)}", AuditWho.From(accessor), Target(group));
            db.UserGroups.RemoveRange(removed);
            db.UserGroups.AddRange(added.Select(u => new UserGroupEntity { UserId = u, GroupId = id }));
            group.UpdatedAt = Clock.UtcNow;
            await db.SaveChangesAsync(ct);
            removed.Select(m => m.UserId).Concat(added).ToList().ForEach(sessions.Forget);
        }
        return await GetAsync(id, ct);
    }

    /// <summary>A user's effective rights, and which groups give each level (§5.5).</summary>
    public async Task<AccessItem> AccessAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.IsAdmin }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("User");
        var groups = await db.Groups.AsNoTracking().Include(g => g.Rights).Where(g => g.Members.Any(m => m.UserId == userId))
            .OrderBy(g => g.NormalizedName).ToListAsync(ct);
        var combined = UserRights.Combine(groups.SelectMany(g => g.Rights.Select(r => (r.Area, r.Level))));
        var rights = RightsCatalog.Visible(configuration).Where(a => combined.ContainsKey(a.Area)).Select(a => new AccessLevel(a.Area, combined[a.Area],
            groups.Where(g => g.Rights.Any(r => r.Area == a.Area && r.Level == combined[a.Area])).Select(g => g.Name).ToList())).ToList();
        return new AccessItem(user.IsAdmin, groups.Select(g => new Ref(g.Id, g.Name)).ToList(), rights);
    }

    /// <summary>The areas a group can be given, those the deployment hides left out (docs/licences-plan.md §13.2).</summary>
    public static IReadOnlyList<RightsAreaItem> Catalog(IConfiguration configuration) => RightsCatalog.Visible(configuration)
        .Select(a => new RightsAreaItem(a.Area, a.Label, a.Levels.Select(l => new RightsLevelItem(l.Level, l.Allows)).ToList())).ToList();

    private async Task<(string Name, string? Description, Dictionary<Area, Level> Rights)> ValidateAsync(GroupRequest request, Guid? id, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        var v = new ApiValidator()
            .Check(name.Length is >= 1 and <= 100, "name", "1 to 100 characters.")
            .Check(description is null || description.Length <= 500, "description", "At most 500 characters.");
        var rights = new Dictionary<Area, Level>();
        foreach (var (area, level) in request.Rights ?? [])
        {
            if (level == Level.None)
            {
                continue;
            }
            if (!RightsCatalog.IsVisible(area, configuration))
            {
                // A hidden area's stored row stays as it is (UpdateAsync); the request can't change it.
                continue;
            }
            if (RightsCatalog.Of(area).Rank(level) == 0)
            {
                v.Add("rights", $"{RightsCatalog.Of(area).Label} has no level {level}: {string.Join(", ", RightsCatalog.Of(area).Levels.Select(l => l.Level))}.");
                continue;
            }
            rights[area] = level;
        }
        v.ThrowIfInvalid();
        var normalized = Normalize(name);
        if (await db.Groups.AnyAsync(g => g.NormalizedName == normalized && g.Id != id, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"A group named {name} exists already.");
        }
        return (name, description, rights);
    }

    private async Task ForgetMembersAsync(Guid id, CancellationToken ct) =>
        (await db.UserGroups.Where(m => m.GroupId == id).Select(m => m.UserId).ToListAsync(ct)).ForEach(sessions.Forget);

    private Task<List<string>> MemberNamesAsync(Guid id, CancellationToken ct) =>
        db.UserGroups.Where(m => m.GroupId == id).Join(db.Users, m => m.UserId, u => u.Id, (m, u) => u.Username).OrderBy(n => n).ToListAsync(ct);

    private static IReadOnlyDictionary<Area, Level> Levels(GroupEntity group) => group.Rights.ToDictionary(r => r.Area, r => r.Level);

    /// <summary>What the group page shows: the stored rows of hidden areas stay out of sight (docs/licences-plan.md §13.2).</summary>
    private IReadOnlyDictionary<Area, Level> VisibleLevels(GroupEntity group) =>
        group.Rights.Where(r => RightsCatalog.IsVisible(r.Area, configuration)).ToDictionary(r => r.Area, r => r.Level);

    private static string Summary(IReadOnlyDictionary<Area, Level> rights) => rights.Count == 0 ? "no rights"
        : string.Join(", ", RightsCatalog.Areas.Where(a => rights.ContainsKey(a.Area)).Select(a => $"{a.Label} {rights[a.Area]}"));

    private static string Show(Level level) => level == Level.None ? "none" : level.ToString();

    private static AuditTarget Target(GroupEntity group) => AuditTarget.Of(AuditTargets.Group, group.Id, group.Name);

    public static string Normalize(string name) => name.Trim().ToLowerInvariant();
}
