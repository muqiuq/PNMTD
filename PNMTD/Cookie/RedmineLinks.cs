using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Cookie;

/// <summary>How a PNMT user's Redmine user stands (docs/cookie-redmine-tab-plan.md §4.3).</summary>
public enum RedmineLinkState
{
    /// <summary>Linked by id, and Redmine knows the id.</summary>
    Linked,

    /// <summary>Linked by id, but no membership or ticket names it (left the projects, deleted): the name is tried instead.</summary>
    LinkedUnknown,

    /// <summary>No id; the name is exactly one Redmine user's. Works by name; linking pins it.</summary>
    Matches,

    /// <summary>No id; the name is two or more Redmine users': which one counts is chance.</summary>
    Several,

    /// <summary>No id; the name is no Redmine user's: the reminders go to the escalation group or whoever is responsible.</summary>
    NoMatch,

    /// <summary>No id and no name.</summary>
    NotSet,
}

/// <summary>A PNMT user as the check sees them.</summary>
public sealed record RedmineLinkUser(Guid Id, string Name, bool InPlan, int? RedmineUserId, string? RedmineName);

/// <summary>A Redmine user PNMT knows of: a member of a project it shows, or an assignee or author of a watched ticket.</summary>
public sealed record KnownRedmineUser(int Id, string Name, bool Member, int OpenTickets, bool Staff);

/// <summary>
/// Which Redmine user each PNMT user is (docs/cookie-redmine-tab-plan.md §4): the check of every active user against the Redmine
/// users PNMT knows, and the lookup by id first that reminders, Needs you and Cookie's people use. Pure, and tested.
/// </summary>
public static class RedmineLinks
{
    /// <summary>A name as Redmine names are compared: trimmed, ignoring case.</summary>
    public static string Key(string? name) => (name ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// The Redmine users PNMT knows: the members (with whether they hold a staff role in a watched project that has staff roles), and
    /// the ticket cases' assignees and authors, newest name first; the open cases count the tickets assigned to each.
    /// </summary>
    public static IReadOnlyDictionary<int, KnownRedmineUser> Known(RedminePeople? people, IReadOnlyList<WatchedProject> watched,
        IEnumerable<(int? AssigneeId, string? AssigneeName, int? AuthorId, string? AuthorName, bool Open)> cases)
    {
        var known = new Dictionary<int, KnownRedmineUser>();
        foreach (var p in people?.Users ?? [])
        {
            var staff = p.Memberships.Any(m => watched.Any(w => w.ProjectId == m.ProjectId && w.StaffRoleIds.Count > 0 && m.RoleIds.Any(w.StaffRoleIds.Contains)));
            known[p.Id] = new KnownRedmineUser(p.Id, p.Name, true, 0, staff);
        }
        var groups = (people?.Groups ?? []).Select(g => g.Id).ToHashSet();
        foreach (var c in cases)
        {
            foreach (var (id, name) in new[] { (c.AssigneeId, c.AssigneeName), (c.AuthorId, c.AuthorName) })
            {
                if (id is { } i && !groups.Contains(i) && !known.ContainsKey(i) && !string.IsNullOrWhiteSpace(name))
                {
                    known[i] = new KnownRedmineUser(i, name.Trim(), false, 0, false);
                }
            }
            if (c.Open && c.AssigneeId is { } assignee && known.TryGetValue(assignee, out var k))
            {
                known[assignee] = k with { OpenTickets = k.OpenTickets + 1 };
            }
        }
        return known;
    }

    /// <summary>The check of §4.3: each user's state, and the Redmine users with work in PNMT but no PNMT user.</summary>
    public static (IReadOnlyList<RedmineLinkRow> Rows, IReadOnlyList<RedmineUnlinkedRow> Unlinked) Check(IReadOnlyList<RedmineLinkUser> users,
        IReadOnlyDictionary<int, KnownRedmineUser> known)
    {
        var byName = known.Values.GroupBy(k => Key(k.Name)).ToDictionary(g => g.Key, g => g.OrderBy(k => k.Id).ToList());
        var rows = new List<RedmineLinkRow>();
        foreach (var u in users)
        {
            if (u.RedmineUserId is { } id)
            {
                var k = known.GetValueOrDefault(id);
                rows.Add(new RedmineLinkRow(u.Id, u.Name, u.InPlan, id, u.RedmineName, k is null ? RedmineLinkState.LinkedUnknown : RedmineLinkState.Linked,
                    k?.Name, [], k?.OpenTickets ?? 0, false));
                continue;
            }
            if (string.IsNullOrWhiteSpace(u.RedmineName))
            {
                rows.Add(new RedmineLinkRow(u.Id, u.Name, u.InPlan, null, null, RedmineLinkState.NotSet, null, [], 0, false));
                continue;
            }
            var matches = byName.GetValueOrDefault(Key(u.RedmineName)) ?? [];
            var state = matches.Count switch { 0 => RedmineLinkState.NoMatch, 1 => RedmineLinkState.Matches, _ => RedmineLinkState.Several };
            rows.Add(new RedmineLinkRow(u.Id, u.Name, u.InPlan, null, u.RedmineName, state, matches.Count == 1 ? matches[0].Name : null,
                [.. matches.Select(m => new RedmineNamed(m.Id, m.Name))], matches.Count == 1 ? matches[0].OpenTickets : 0, false));
        }
        // Two people with one Redmine user: by id, or by the same name without one (the database allows each id once only).
        var sharedIds = rows.Where(r => r.RedmineUserId is not null).GroupBy(r => r.RedmineUserId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var sharedNames = rows.Where(r => r.RedmineUserId is null && !string.IsNullOrWhiteSpace(r.RedmineName)).GroupBy(r => Key(r.RedmineName))
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        rows = [.. rows.Select(r => r with { Shared = r.RedmineUserId is { } i ? sharedIds.Contains(i) : r.RedmineName is { } n && sharedNames.Contains(Key(n)) })];

        // Whom no PNMT user is: linked by nobody, and nobody's single match by name.
        var taken = rows.Where(r => r.State is RedmineLinkState.Linked or RedmineLinkState.LinkedUnknown).Select(r => r.RedmineUserId!.Value)
            .Concat(rows.Where(r => r.State == RedmineLinkState.Matches).Select(r => r.Candidates[0].Id)).ToHashSet();
        var unlinked = known.Values.Where(k => !taken.Contains(k.Id) && (k.OpenTickets > 0 || k.Staff))
            .OrderByDescending(k => k.OpenTickets).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .Select(k => new RedmineUnlinkedRow(k.Id, k.Name, k.OpenTickets, k.Staff)).ToList();
        return (rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(), unlinked);
    }

    /// <summary>
    /// The PNMT user a ticket's assignee is (§4.4): the one linked to the Redmine user's id; without one, the one active user whose
    /// name is the assignee's and who isn't linked to someone else. Two such users by name: nobody, rather than either by chance.
    /// </summary>
    public static async Task<UserEntity?> UserOfAsync(PnmtdDbContext db, int? redmineUserId, string? name, CancellationToken ct)
    {
        if (redmineUserId is { } id && await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.IsActive && u.RedmineUserId == id, ct) is { } linked)
        {
            return linked;
        }
        var key = Key(name);
        if (key.Length == 0)
        {
            return null;
        }
        var byName = await db.Users.AsNoTracking().Where(u => u.IsActive && u.RedmineName != null && u.RedmineName.Trim().ToLower() == key
            && (u.RedmineUserId == null || u.RedmineUserId == redmineUserId)).Take(2).ToListAsync(ct);
        return byName.Count == 1 ? byName[0] : null;
    }
}

/// <summary>
/// Admin → Cookie → Redmine's links (docs/cookie-redmine-tab-plan.md §4, §8): the check, a link or unlink by a person with
/// <i>Redmine: Manage</i>, and every exact match at once. A link is picked from the Redmine users PNMT knows; never typed.
/// </summary>
public sealed class RedmineLinkService(PnmtdDbContext db, RedmineWorker worker, CookieSettings settings, IOptionsMonitor<RedmineOptions> redmineOptions)
{
    private sealed record Inputs(bool Configured, RedminePeople? People, RedmineFreshness? Freshness, string? Error,
        IReadOnlyDictionary<int, KnownRedmineUser> Known);

    private async Task<Inputs> InputsAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var configured = redmineOptions.CurrentValue.Enabled;
        RedminePeople? people = null;
        RedmineFreshness? freshness = null;
        string? error = null;
        if (configured)
        {
            try
            {
                // Through the worker's queue, in the interactive lane: someone waits on the tab (§4.2).
                (people, freshness) = await worker.PeopleAsync(RedmineLane.Interactive, user.Identity?.Name ?? "PNMT", ct);
            }
            catch (RedmineException e)
            {
                error = e.Message;
            }
            catch (ApiException e) when (e.Status == StatusCodes.Status504GatewayTimeout)
            {
                error = e.Message;
            }
        }
        var watched = (await settings.GetAsync<CookieRedmineSettings>(ct)).Projects;
        var cases = await db.CookieCases.AsNoTracking().Where(c => c.Kind == CookieCaseKind.Ticket)
            .OrderByDescending(c => c.ChangedAt).Select(c => new { c.AssigneeId, c.AssigneeName, c.AuthorId, c.AuthorName, c.Open }).ToListAsync(ct);
        return new Inputs(configured, people, freshness, error,
            RedmineLinks.Known(people, watched, cases.Select(c => (c.AssigneeId, c.AssigneeName, c.AuthorId, c.AuthorName, c.Open))));
    }

    private async Task<List<RedmineLinkUser>> UsersAsync(CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.IsActive)
            .Select(u => new RedmineLinkUser(u.Id, u.DisplayName != "" ? u.DisplayName : u.Username, u.InPlan, u.RedmineUserId, u.RedmineName))
            .ToListAsync(ct);

    /// <summary>The check (§4.3). A linked user whom Redmine renamed gets the new name stored, so the name shows what Redmine shows.</summary>
    public async Task<RedmineLinksView> ViewAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var inputs = await InputsAsync(user, ct);
        var renamed = (await UsersAsync(ct)).Where(u => u.RedmineUserId is { } id && inputs.Known.TryGetValue(id, out var k) && k.Name != u.RedmineName)
            .Select(u => (u.Id, inputs.Known[u.RedmineUserId!.Value].Name)).ToList();
        foreach (var (id, name) in renamed)
        {
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.RedmineName, name), ct);
        }
        var (rows, unlinked) = RedmineLinks.Check(await UsersAsync(ct), inputs.Known);
        return new RedmineLinksView(inputs.Configured, inputs.Freshness?.RefreshedAt, inputs.Freshness?.Refreshing ?? false,
            inputs.Error ?? inputs.Freshness?.Error, inputs.People?.Forbidden ?? [], rows, unlinked,
            [.. inputs.Known.Values.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).Select(k => new RedmineUserItem(k.Id, k.Name, k.Member, k.OpenTickets))],
            inputs.People?.Groups.Count ?? 0);
    }

    /// <summary>
    /// Links a user to a Redmine user PNMT knows, or unlinks them (null: the id and the name go). A Redmine user is one PNMT user's
    /// at most: an active one keeps theirs (unlink there first), an inactive one gives it up.
    /// </summary>
    public async Task<RedmineLinkRow> LinkAsync(Guid userId, int? redmineUserId, ClaimsPrincipal user, CancellationToken ct)
    {
        var u = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct) ?? throw ApiException.NotFound("User");
        var inputs = await InputsAsync(user, ct);
        if (redmineUserId is { } id)
        {
            var k = inputs.Known.GetValueOrDefault(id)
                ?? throw new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed, "Not a Redmine user PNMT knows.",
                    new Dictionary<string, string[]> { ["redmineUserId"] = ["Pick one of Redmine's users: a member of a project PNMT shows, or someone on a watched ticket."] });
            var holder = await db.Users.FirstOrDefaultAsync(x => x.RedmineUserId == id && x.Id != userId, ct);
            if (holder is { IsActive: true })
            {
                throw ApiException.Conflict("redmine_user_taken", $"{k.Name} is {holder.DisplayName}'s Redmine user: unlink them first.");
            }
            if (holder is not null)
            {
                holder.RedmineUserId = null;
                await db.SaveChangesAsync(ct);
            }
            AuditLog.Summary($"{u.Username}: Redmine user {k.Name} (#{id})");
            u.RedmineUserId = id;
            u.RedmineName = k.Name;
        }
        else
        {
            AuditLog.Summary($"{u.Username}: no Redmine user");
            u.RedmineUserId = null;
            u.RedmineName = null;
        }
        await db.SaveChangesAsync(ct);
        var (rows, _) = RedmineLinks.Check(await UsersAsync(ct), inputs.Known);
        return rows.FirstOrDefault(r => r.UserId == userId)
            ?? new RedmineLinkRow(u.Id, u.DisplayName, u.InPlan, u.RedmineUserId, u.RedmineName, RedmineLinkState.NotSet, null, [], 0, false);
    }

    /// <summary>The away watch's comments, newest first (§9): 50 a page.</summary>
    public async Task<Paged<CookieAwayCommentItem>> AwayAsync(int page, CancellationToken ct)
    {
        const int size = 50;
        page = Math.Max(1, page);
        var total = await db.CookieAwayComments.CountAsync(ct);
        var rows = await (from a in db.CookieAwayComments.AsNoTracking()
                          join c in db.CookieCases.AsNoTracking() on a.CaseId equals c.Id
                          join u in db.Users.AsNoTracking() on a.AssigneeUserId equals u.Id into assignees
                          from u in assignees.DefaultIfEmpty()
                          orderby a.Id descending
                          select new CookieAwayCommentItem(a.Id, a.CaseId, a.IssueId, c.Title, u != null ? u.DisplayName : null, a.AwayUntil, a.AuthorName, a.IsPrivate,
                              a.CommentAt, a.Text, a.State, a.Importance, a.Confidence, a.Reason, a.JudgedBy, a.NotifiedAt, a.Recipients, a.Detail))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<CookieAwayCommentItem>(rows, page, size, total);
    }

    /// <summary>Every <i>Matches</i> row linked by its one match's id, in one step (§4.3); each user's change is in the audit entry.</summary>
    public async Task<RedmineLinkedCount> LinkExactAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var inputs = await InputsAsync(user, ct);
        var (rows, _) = RedmineLinks.Check(await UsersAsync(ct), inputs.Known);
        var taken = (await db.Users.Where(u => u.RedmineUserId != null).Select(u => u.RedmineUserId!.Value).ToListAsync(ct)).ToHashSet();
        var exact = rows.Where(r => r.State == RedmineLinkState.Matches && !r.Shared && !taken.Contains(r.Candidates[0].Id)).ToList();
        foreach (var r in exact)
        {
            var u = await db.Users.SingleAsync(x => x.Id == r.UserId, ct);
            u.RedmineUserId = r.Candidates[0].Id;
            u.RedmineName = r.Candidates[0].Name;
        }
        await db.SaveChangesAsync(ct);
        AuditLog.Summary(exact.Count == 0 ? "No exact match to link" : $"Linked by exact match: {string.Join(", ", exact.Select(r => $"{r.Name} → #{r.Candidates[0].Id}"))}");
        return new RedmineLinkedCount(exact.Count);
    }
}
