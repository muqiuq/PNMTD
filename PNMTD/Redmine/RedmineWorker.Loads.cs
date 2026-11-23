using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Redmine;

/// <summary>The overview's Redmine part (§6.3): the counts and lists, the stale tickets; PNMT's links are added when it is read.</summary>
internal sealed record OverviewData(int Open, RmIssues Urgent, RmIssues Unassigned, RmIssues Overdue, StaleTickets Stale, RmIssues Recent);

/// <summary>A ticket in full, and the tickets it relates to that PNMT may show.</summary>
internal sealed record IssueData(RmIssue Issue, IReadOnlyDictionary<int, RmIssue> Related);

/// <summary>One page of a search; <c>LastChange</c> for the stale filter (Cookie's notes not counted).</summary>
internal sealed record SearchData(IReadOnlyList<RmIssue> Issues, int Page, int PageSize, int Total, IReadOnlyDictionary<int, DateTime>? LastChange);

/// <summary>
/// The open tickets nobody but Cookie changed for <c>Redmine__StaleDays</c>, the oldest change first: how many, the first
/// <see cref="RedmineWorker.MaxStaleTickets"/> as Redmine listed them, and each one's last change that counts.
/// </summary>
public sealed record StaleTickets(int Total, IReadOnlyList<RmIssue> Issues, IReadOnlyDictionary<int, DateTime> LastChange);

/// <summary>
/// The worker's reads (docs/redmine-queue-outbound-plan.md §3.2): what each snapshot is and how it is read. Calls that don't
/// depend on each other run at once; the slots keep them within <c>Redmine__Parallelism</c>. Only configured projects: a
/// ticket of any other is "not found", whatever the key could read. Filters use Redmine's query syntax (f[], op[], v[]).
/// </summary>
public sealed partial class RedmineWorker
{
    public const string LookupsKey = "lookups";

    /// <summary>At most this many tickets make the list of tickets without a change, for the search.</summary>
    public const int MaxStaleTickets = 500;

    public static readonly TimeSpan LookupsFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (DateTime Value, DateTime ReadAt)> lastChanges = new();
    private (int? Id, DateTime ReadAt) writer;

    public static string OverviewKey(int? project) => $"overview:{project}";

    public static string StaleKey(int? project) => $"stale:{project}";

    public static string IssueKey(int id) => $"issue:{id}";

    /// <summary>A search's key: every filter, the text, the order and the page (§3.2).</summary>
    public static string SearchKey(RedmineQuery q) => "search:" + string.Join("|",
        $"project={q.Project}", $"status={q.Status ?? "open"}", $"tracker={q.Tracker}", $"priority={q.Priority}", $"assignee={q.Assignee}",
        $"overdue={q.Overdue}", $"linked={q.Linked}", $"stale={q.Stale}", $"sort={q.Sort}", $"page={Math.Max(1, q.Page)}",
        $"size={Math.Clamp(q.PageSize, 1, 100)}", $"q={q.Search?.Trim()}");

    internal RedmineSnapshot LookupsSnapshot => Define(LookupsKey, "lookups", async calls => await LoadLookupsAsync(calls), pinned: true, maxAge: LookupsFor);

    public const string PeopleKey = "people";

    /// <summary>Redmine's users from the memberships (docs/cookie-redmine-tab-plan.md §4.2): read when the Redmine tab asks, kept an hour.</summary>
    internal RedmineSnapshot PeopleSnapshot => Define(PeopleKey, "members of every project", async calls => await LoadPeopleAsync(calls),
        maxAge: TimeSpan.FromHours(1));

    /// <summary>Redmine's users, and how fresh they are; read through the queue if there are none yet.</summary>
    public async Task<(RedminePeople Value, RedmineFreshness Freshness)> PeopleAsync(RedmineLane lane, string askedBy, CancellationToken ct) =>
        await ReadAsync<RedminePeople>(PeopleSnapshot, lane, askedBy, ct);

    /// <summary>The two snapshots that are always kept: the lookups and the overview of all projects (§4.1).</summary>
    private void Pinned()
    {
        _ = LookupsSnapshot;
        _ = Overview(null);
    }

    internal RedmineSnapshot Overview(int? project) =>
        Define(OverviewKey(project), project is null ? "overview (all projects)" : $"overview (project {project})",
            async calls => await LoadOverviewAsync(calls, project), pinned: project is null);

    internal RedmineSnapshot Stale(int? project) =>
        Define(StaleKey(project), project is null ? "stale tickets (all projects)" : $"stale tickets (project {project})",
            async calls => await LoadStaleAsync(calls, project));

    internal RedmineSnapshot Issue(int id) => Define(IssueKey(id), $"ticket #{id}", async calls => await LoadIssueAsync(calls, id));

    internal RedmineSnapshot Search(RedmineQuery query)
    {
        var q = Copy(query);
        var text = q.Search?.Trim();
        var purpose = text is { Length: > 0 } ? $"search \"{OutboundText(text)}\"" : "search";
        return Define(SearchKey(q), q.Page > 1 ? $"{purpose}, page {q.Page}" : purpose, async calls => await LoadSearchAsync(calls, q));
    }

    private static string OutboundText(string text) => text.Length <= 60 ? text : text[..59] + "…";

    private static RedmineQuery Copy(RedmineQuery q) => new()
    {
        Project = q.Project, Status = q.Status, Tracker = q.Tracker, Priority = q.Priority, Assignee = q.Assignee, Overdue = q.Overdue, Linked = q.Linked,
        Stale = q.Stale, Sort = q.Sort, Search = q.Search, Page = Math.Max(1, q.Page), PageSize = Math.Clamp(q.PageSize, 1, 100),
    };

    /// <summary>The lookups for a caller outside a job, read through the queue if there are none yet.</summary>
    public async Task<RedmineLookups> LookupsAsync(RedmineLane lane, string askedBy, CancellationToken ct) =>
        (await ReadAsync<RedmineLookups>(LookupsSnapshot, lane, askedBy, ct)).Value;

    /// <summary>The lookups inside a job: in its lane.</summary>
    internal Task<RedmineLookups> LookupsAsync(RedmineCalls calls) => LookupsAsync(calls.Job.Lane, calls.Job.AskedBy, calls.Token);

    public RedmineFreshness LookupsFreshness() => Freshness(LookupsSnapshot);

    // --- the lookups -------------------------------------------------------------------------------------------------------

    private async Task<RedmineLookups> LoadLookupsAsync(RedmineCalls calls)
    {
        var all = new List<RmProject>();
        for (var offset = 0; ; offset += 100)
        {
            var page = await calls.GetAsync<RmProjects>($"/projects.json?limit=100&offset={offset}", "projects");
            all.AddRange(page.Projects);
            if (page.Projects.Count == 0 || all.Count >= page.TotalCount)
            {
                break;
            }
        }
        var chosen = new List<RmProject>();
        var notFound = new List<string>();
        foreach (var wanted in O.ProjectList)
        {
            var project = all.FirstOrDefault(p => p.Identifier.Equals(wanted, StringComparison.OrdinalIgnoreCase) || p.Id.ToString(CultureInfo.InvariantCulture) == wanted);
            if (project is null)
            {
                notFound.Add(wanted);
            }
            else if (chosen.All(c => c.Id != project.Id))
            {
                chosen.Add(project);
            }
        }
        var allowed = chosen.Select(p => p.Id).ToHashSet();
        if (O.IncludeSubprojects)
        {
            // Descendants of the chosen ones: a project whose parent chain reaches one of them.
            var byId = all.ToDictionary(p => p.Id);
            foreach (var p in all)
            {
                for (var parent = p.Parent?.Id; parent is { } id && byId.ContainsKey(id); parent = byId[id].Parent?.Id)
                {
                    if (chosen.Any(c => c.Id == id))
                    {
                        allowed.Add(p.Id);
                        break;
                    }
                }
            }
        }
        var statuses = calls.GetAsync<RmStatuses>("/issue_statuses.json", "statuses");
        var trackers = calls.GetAsync<RmTrackers>("/trackers.json", "trackers");
        var priorities = calls.GetAsync<RmPriorities>("/enumerations/issue_priorities.json", "priorities");
        var memberships = chosen.Select(async project =>
        {
            try
            {
                return (await calls.GetAsync<RmMemberships>($"/projects/{project.Id}/memberships.json?limit=100", $"members of {project.Identifier}")).Memberships;
            }
            catch (RedmineException e) when (e.Failure == RedmineFailure.Forbidden)
            {
                // The account may not see the members: the filter offers none.
                return (IReadOnlyList<RmMembership>)[];
            }
        }).ToList();
        await Task.WhenAll([statuses, trackers, priorities, .. memberships]);
        var members = new Dictionary<int, RmRef>();
        foreach (var m in memberships.SelectMany(t => t.Result).Where(m => m.User is not null))
        {
            members[m.User!.Id] = m.User;
        }
        var projects = all.Where(p => allowed.Contains(p.Id)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new RedmineLookups(projects, allowed, notFound, statuses.Result.IssueStatuses, trackers.Result.Trackers, priorities.Result.IssuePriorities,
            members.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    // --- Redmine's users --------------------------------------------------------------------------------------------------

    /// <summary>At most this many memberships per project: 20 pages.</summary>
    private const int MaxMembershipsPerProject = 2000;

    private async Task<RedminePeople> LoadPeopleAsync(RedmineCalls calls)
    {
        var lookups = await LookupsAsync(calls);
        var users = new Dictionary<int, (string Name, Dictionary<int, HashSet<int>> Roles)>();
        var groups = new Dictionary<int, RmRef>();
        var forbidden = new List<string>();
        foreach (var projectId in lookups.Allowed.Order())
        {
            for (var offset = 0; offset < MaxMembershipsPerProject; offset += 100)
            {
                RmMemberships page;
                try
                {
                    page = await calls.GetAsync<RmMemberships>($"/projects/{projectId}/memberships.json?limit=100&offset={offset}", $"members of project {projectId}");
                }
                catch (RedmineException e) when (e.Failure == RedmineFailure.Forbidden)
                {
                    forbidden.Add(lookups.Projects.FirstOrDefault(p => p.Id == projectId)?.Name ?? $"#{projectId}");
                    break;
                }
                foreach (var m in page.Memberships)
                {
                    if (m.User is { } u)
                    {
                        if (!users.TryGetValue(u.Id, out var known))
                        {
                            known = (u.Name ?? $"#{u.Id}", []);
                            users[u.Id] = known;
                        }
                        if (!known.Roles.TryGetValue(projectId, out var roles))
                        {
                            known.Roles[projectId] = roles = [];
                        }
                        roles.UnionWith((m.Roles ?? []).Select(r => r.Id));
                    }
                    else if (m.Group is { } g)
                    {
                        groups[g.Id] = g;
                    }
                }
                if (page.Memberships.Count < 100 || offset + 100 >= page.TotalCount)
                {
                    break;
                }
            }
        }
        return new RedminePeople(
            [.. users.Select(u => new RedminePerson(u.Key, u.Value.Name,
                    [.. u.Value.Roles.OrderBy(r => r.Key).Select(r => new RedmineMembershipOf(r.Key, [.. r.Value.Order()]))]))
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)],
            [.. groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)], forbidden);
    }

    // --- the overview ------------------------------------------------------------------------------------------------------

    private async Task<OverviewData> LoadOverviewAsync(RedmineCalls calls, int? project)
    {
        var lookups = await LookupsAsync(calls);
        Task<RmIssues> List(string what, Action<RedmineQueryString> filter, string sort, int limit = 10, string status = "open")
        {
            var q = Filters(lookups, project, status);
            filter(q);
            q.Plain("sort", sort);
            q.Plain("limit", limit.ToString(CultureInfo.InvariantCulture));
            return calls.GetAsync<RmIssues>("/issues.json?" + q, $"overview: {what}");
        }
        // Above Redmine's default priority (High, Urgent, Immediate in a standard Redmine): nothing to configure.
        var defaultIndex = lookups.Priorities.ToList().FindIndex(p => p.IsDefault);
        var urgentIds = lookups.Priorities.Skip(defaultIndex + 1).Select(p => p.Id).ToList();
        var open = List("open", _ => { }, "updated_on:desc", 1);
        var urgent = urgentIds.Count == 0 ? Task.FromResult(new RmIssues([], 0, 0, 0))
            : List("urgent", q => q.Equal("priority_id", [.. urgentIds]), "priority:desc,created_on");
        var unassigned = List("unassigned", q => q.Filter("assigned_to_id", "!*"), "created_on");
        var overdue = List("overdue", q => q.Filter("due_date", "<=", Yesterday()), "due_date");
        var recent = List("recently updated", _ => { }, "updated_on:desc", status: "all");
        var stale = LoadStaleAsync(calls, project);
        await Task.WhenAll(open, urgent, unassigned, overdue, recent, stale);
        Store(Stale(project), stale.Result);
        return new OverviewData(open.Result.TotalCount, urgent.Result, unassigned.Result, overdue.Result, stale.Result, recent.Result);
    }

    // --- no change for StaleDays, Cookie's notes not counted ----------------------------------------------------------------

    /// <summary>
    /// Any change counts (a note, a field, a status, an attachment, a relation), except Cookie's own notes. Redmine's
    /// <c>updated_on</c> counts Cookie's notes too, so two lists make the answer, read at once:
    /// <list type="number">
    /// <item>open tickets with no change at all since the cutoff, Cookie's included: Redmine filters and counts these itself;</item>
    /// <item>open tickets changed since, whose last change is Cookie's (<c>last_updated_by</c>): their journals say when anyone else
    /// last changed them (<see cref="LastChangeAsync"/>), read at once too.</item>
    /// </list>
    /// Without Cookie's write key, there are no notes of Cookie's, and the first list is the answer.
    /// </summary>
    private async Task<StaleTickets> LoadStaleAsync(RedmineCalls calls, int? project)
    {
        var lookups = await LookupsAsync(calls);
        var cutoff = DateOnly.FromDateTime(Clock.UtcNow).AddDays(-Math.Max(1, O.StaleDays));
        string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        async Task<(List<RmIssue> Issues, int Total)> AllAsync(string what, Action<RedmineQueryString> filter)
        {
            var issues = new List<RmIssue>();
            var total = 0;
            for (var offset = 0; offset < MaxStaleTickets; offset += 100)
            {
                var q = Filters(lookups, project, "open");
                filter(q);
                q.Plain("sort", "updated_on");
                q.Plain("offset", offset.ToString(CultureInfo.InvariantCulture));
                q.Plain("limit", "100");
                var page = await calls.GetAsync<RmIssues>("/issues.json?" + q, what);
                total = page.TotalCount;
                issues.AddRange(page.Issues);
                if (page.Issues.Count == 0 || issues.Count >= page.TotalCount)
                {
                    break;
                }
            }
            return (issues, total);
        }

        var untouched = AllAsync("stale: no change", q => q.Filter("updated_on", "<=", Day(cutoff)));
        var cookie = await CookieUserIdAsync(calls);
        var touched = cookie is { } c
            ? AllAsync("stale: Cookie's last", q =>
            {
                q.Filter("updated_on", ">=", Day(cutoff.AddDays(1)));
                q.Filter("last_updated_by", "=", c.ToString(CultureInfo.InvariantCulture));
            })
            : Task.FromResult<(List<RmIssue> Issues, int Total)>(([], 0));
        await Task.WhenAll(untouched, touched);
        var (issues, count) = untouched.Result;
        var lastChange = issues.ToDictionary(i => i.Id, i => Clock.AsUtc(i.UpdatedOn));
        if (cookie is { } writerId)
        {
            var candidates = touched.Result.Issues.Where(i => !lastChange.ContainsKey(i.Id)).ToList();
            var changes = await Task.WhenAll(candidates.Select(i => LastChangeAsync(calls, i, writerId)));
            for (var n = 0; n < candidates.Count; n++)
            {
                if (DateOnly.FromDateTime(changes[n]) <= cutoff)
                {
                    count++;
                    issues.Add(candidates[n]);
                    lastChange[candidates[n].Id] = changes[n];
                }
            }
        }
        return new StaleTickets(count, issues.OrderBy(i => lastChange[i.Id]).ThenBy(i => i.Id).Take(MaxStaleTickets).ToList(), lastChange);
    }

    /// <summary>The Redmine user Cookie writes as (<c>Redmine__WriteApiKey</c>), for an hour; null without a write key, or when Redmine doesn't say.</summary>
    internal async Task<int?> CookieUserIdAsync(RedmineCalls calls)
    {
        if (!O.CanWrite)
        {
            return null;
        }
        var known = writer;
        if (known.Id is { } id && Clock.UtcNow - known.ReadAt < LookupsFor)
        {
            return id;
        }
        try
        {
            var user = (await calls.GetAsync<RmUserEnvelope>("/users/current.json", "Cookie's Redmine user", asWriter: true)).User.Id;
            writer = (user, Clock.UtcNow);
            return user;
        }
        catch (RedmineException)
        {
            // Cookie's notes then count as changes, as Redmine's own date has them, until Redmine answers again.
            return null;
        }
    }

    /// <summary>A ticket's last change by anyone but Cookie, from its journals; kept until the ticket changes again, a day at most.</summary>
    private async Task<DateTime> LastChangeAsync(RedmineCalls calls, RmIssue listed, int cookie)
    {
        var key = $"{listed.Id}:{listed.UpdatedOn.Ticks}";
        if (lastChanges.TryGetValue(key, out var known))
        {
            return known.Value;
        }
        var issue = (await calls.GetAsync<RmIssueEnvelope>($"/issues/{listed.Id}.json?include=journals", $"stale: #{listed.Id}'s journals")).Issue;
        var last = RedmineService.LastChangeWithoutCookie(issue, cookie);
        lastChanges[key] = (last, Clock.UtcNow);
        return last;
    }

    // --- a ticket ------------------------------------------------------------------------------------------------------------

    /// <summary>A ticket in full (§6.4) and its related tickets' headers; PNMT's copy is refreshed if it is linked.</summary>
    private async Task<IssueData> LoadIssueAsync(RedmineCalls calls, int id)
    {
        var lookups = await LookupsAsync(calls);
        var issue = (await calls.GetAsync<RmIssueEnvelope>($"/issues/{id}.json?include=journals,attachments,relations,children", "the ticket")).Issue;
        if (!lookups.Allowed.Contains(issue.Project.Id))
        {
            // Another project's: nothing more is read, and the reader says so (RedmineService.IssueAsync).
            return new IssueData(issue, new Dictionary<int, RmIssue>());
        }
        var relatedIds = (issue.Relations ?? []).Select(r => r.IssueId == id ? r.IssueToId : r.IssueId)
            .Concat((issue.Children ?? []).Select(c => c.Id)).Concat(issue.Parent is { } parent ? [parent.Id] : []).Distinct().ToList();
        var related = relatedIds.Count == 0 ? new Dictionary<int, RmIssue>()
            : (await calls.GetAsync<RmIssues>($"/issues.json?issue_id={string.Join(",", relatedIds)}&status_id=*&limit=100", "related tickets"))
                .Issues.Where(i => lookups.Allowed.Contains(i.Project.Id)).ToDictionary(i => i.Id);
        await using var db = await dbs.CreateDbContextAsync(calls.Token);
        if (await db.RedmineLinks.AnyAsync(l => l.IssueId == id, calls.Token))
        {
            await RedmineService.UpsertCopyAsync(db, [issue], calls.Token);
            await db.SaveChangesAsync(calls.Token);
        }
        return new IssueData(issue, related);
    }

    // --- the search --------------------------------------------------------------------------------------------------------

    /// <summary>Redmine's issue list with PNMT's filters (§6.3): always within the configured projects.</summary>
    private async Task<SearchData> LoadSearchAsync(RedmineCalls calls, RedmineQuery query)
    {
        var lookups = await LookupsAsync(calls);
        var size = query.PageSize;
        var page = query.Page;
        // A number (1234, #1234) is the ticket itself.
        var text = query.Search?.Trim();
        if (text is { Length: > 0 } && int.TryParse(text.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            var found = await calls.GetAsync<RmIssues>($"/issues.json?issue_id={number}&status_id=*", $"ticket #{number}");
            var items = found.Issues.Where(i => lookups.Allowed.Contains(i.Project.Id)).ToList();
            return new SearchData(items, 1, size, items.Count, null);
        }
        var q = Filters(lookups, query.Project, query.Status ?? "open");
        if (query.Tracker is { } tracker)
        {
            q.Equal("tracker_id", tracker);
        }
        if (query.Priority is { } priority)
        {
            q.Equal("priority_id", priority);
        }
        if (query.Assignee is "none")
        {
            q.Filter("assigned_to_id", "!*");
        }
        else if (int.TryParse(query.Assignee, NumberStyles.None, CultureInfo.InvariantCulture, out var assignee))
        {
            q.Equal("assigned_to_id", assignee);
        }
        if (query.Overdue)
        {
            q.Filter("due_date", "<=", Yesterday());
        }
        if (text is { Length: > 0 })
        {
            q.Filter("any_searchable", "~", text);
        }
        if (query.Linked)
        {
            await using var db = await dbs.CreateDbContextAsync(calls.Token);
            var linked = await db.RedmineLinks.AsNoTracking().Select(l => l.IssueId).Distinct().Take(500).ToListAsync(calls.Token);
            if (linked.Count == 0)
            {
                return new SearchData([], page, size, 0, null);
            }
            if (!query.Stale)
            {
                q.Plain("issue_id", string.Join(",", linked));
            }
            else
            {
                return await LoadStaleSearchAsync(calls, q, lookups, query, linked.ToHashSet(), page, size);
            }
        }
        if (query.Stale)
        {
            return await LoadStaleSearchAsync(calls, q, lookups, query, null, page, size);
        }
        q.Plain("sort", Sort(query.Sort));
        q.Plain("offset", ((page - 1) * size).ToString(CultureInfo.InvariantCulture));
        q.Plain("limit", size.ToString(CultureInfo.InvariantCulture));
        var result = await calls.GetAsync<RmIssues>("/issues.json?" + q, $"page {page}");
        return new SearchData(result.Issues, page, size, result.TotalCount, null);
    }

    /// <summary>
    /// The search's tickets without a change (§6.3): Redmine applies the other filters to the stale tickets' numbers, and PNMT
    /// sorts and pages them, because Redmine's <c>updated_on</c> would put a ticket with Cookie's note among the fresh ones.
    /// </summary>
    private async Task<SearchData> LoadStaleSearchAsync(RedmineCalls calls, RedmineQueryString q, RedmineLookups lookups, RedmineQuery query,
        IReadOnlySet<int>? only, int page, int size)
    {
        var stale = (await ReadAsync<StaleTickets>(Stale(query.Project), calls.Job.Lane, calls.Job.AskedBy, calls.Token)).Value;
        var ids = stale.Issues.Select(i => i.Id).Where(id => only is null || only.Contains(id)).ToList();
        if (ids.Count == 0)
        {
            return new SearchData([], page, size, 0, stale.LastChange);
        }
        q.Plain("issue_id", string.Join(",", ids));
        var filters = q.ToString();
        var found = new List<RmIssue>();
        for (var offset = 0; ; offset += 100)
        {
            var result = await calls.GetAsync<RmIssues>($"/issues.json?{filters}&offset={offset}&limit=100", "stale tickets, filtered");
            found.AddRange(result.Issues);
            if (result.Issues.Count == 0 || found.Count >= result.TotalCount)
            {
                break;
            }
        }
        DateTime Changed(RmIssue i) => stale.LastChange.TryGetValue(i.Id, out var at) ? at : Clock.AsUtc(i.UpdatedOn);
        var priorities = lookups.Priorities.Select((p, index) => (p.Id, index)).ToDictionary(p => p.Id, p => p.index);
        int Priority(RmIssue i) => i.Priority is { } p ? priorities.GetValueOrDefault(p.Id, -1) : -1;
        IEnumerable<RmIssue> sorted = (query.Sort ?? "-updated") switch
        {
            "updated" => found.OrderBy(Changed).ThenBy(i => i.Id),
            "created" => found.OrderBy(i => i.CreatedOn).ThenBy(i => i.Id),
            "-created" => found.OrderByDescending(i => i.CreatedOn).ThenByDescending(i => i.Id),
            "priority" => found.OrderBy(Priority).ThenByDescending(i => i.Id),
            "-priority" => found.OrderByDescending(Priority).ThenByDescending(i => i.Id),
            "id" => found.OrderBy(i => i.Id),
            "-id" => found.OrderByDescending(i => i.Id),
            _ => found.OrderByDescending(Changed).ThenByDescending(i => i.Id),
        };
        return new SearchData(sorted.Skip((page - 1) * size).Take(size).ToList(), page, size, found.Count, stale.LastChange);
    }

    // --- the connection's test -------------------------------------------------------------------------------------------

    /// <summary><i>Test connection</i> (§8.5), in the button lane: whose key it is, then the lookups read again.</summary>
    public async Task<(string Account, RedmineLookups Lookups)> TestAsync(string askedBy, CancellationToken ct)
    {
        var user = await RunAsync(new RedmineJobSpec("test", RedmineLane.Button, "test connection", askedBy),
            async calls => (await calls.GetAsync<RmUserEnvelope>("/users/current.json", "the key's account")).User).WaitAsync(ct);
        var lookups = (RedmineLookups)await RefreshAsync(LookupsSnapshot, RedmineLane.Button, askedBy).WaitAsync(ct);
        return (user.Login, lookups);
    }

    // --- Redmine's query syntax ----------------------------------------------------------------------------------------------

    /// <summary>The configured projects, or one of them (with its subprojects): always Redmine's query syntax.</summary>
    internal RedmineQueryString Filters(RedmineLookups lookups, int? project, string status)
    {
        var q = new RedmineQueryString();
        IEnumerable<int> projects = lookups.Allowed;
        if (project is { } one)
        {
            if (!lookups.Allowed.Contains(one))
            {
                throw ApiException.Invalid("project", "Not one of PNMT's Redmine projects.");
            }
            projects = O.IncludeSubprojects ? lookups.Projects.Where(p => p.Id == one || IsWithin(lookups, p, one)).Select(p => p.Id) : [one];
        }
        q.Equal("project_id", [.. projects]);
        q.Filter("status_id", status switch { "closed" => "c", "all" => "*", _ => "o" });
        return q;
    }

    private static bool IsWithin(RedmineLookups l, RmProject p, int ancestor)
    {
        for (var parent = p.Parent?.Id; parent is { } id; parent = l.Projects.FirstOrDefault(x => x.Id == id)?.Parent?.Id)
        {
            if (id == ancestor)
            {
                return true;
            }
        }
        return false;
    }

    private static string Sort(string? sort) => (sort ?? "-updated") switch
    {
        "updated" => "updated_on",
        "created" => "created_on",
        "-created" => "created_on:desc",
        "priority" => "priority,id:desc",
        "-priority" => "priority:desc,id:desc",
        "id" => "id",
        "-id" => "id:desc",
        _ => "updated_on:desc",
    };

    private static string Yesterday() => DateOnly.FromDateTime(Clock.UtcNow).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Redmine's query string, its filter syntax included (f[]=status_id&amp;op[status_id]=o&amp;v[status_id][]=…).</summary>
internal sealed class RedmineQueryString
{
    private readonly List<(string Key, string Value)> parts = [];

    public void Plain(string key, string value) => parts.Add((key, value));

    public void Filter(string field, string op, params string[] values)
    {
        parts.Add(("f[]", field));
        parts.Add(($"op[{field}]", op));
        foreach (var v in values)
        {
            parts.Add(($"v[{field}][]", v));
        }
    }

    public void Equal(string field, params int[] values) => Filter(field, "=", [.. values.Select(v => v.ToString(CultureInfo.InvariantCulture))]);

    public override string ToString() => string.Join("&", parts.Select(p => $"{WebUtility.UrlEncode(p.Key)}={WebUtility.UrlEncode(p.Value)}"));
}
