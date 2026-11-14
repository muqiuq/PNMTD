using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Redmine;

/// <summary>What PNMT knows of Redmine's setup: the projects it shows (with their subprojects), and the names it needs.</summary>
public sealed record RedmineLookups(IReadOnlyList<RmProject> Projects, IReadOnlySet<int> Allowed, IReadOnlyList<string> NotFound,
    IReadOnlyList<RmStatus> Statuses, IReadOnlyList<RmRef> Trackers, IReadOnlyList<RmPriority> Priorities, IReadOnlyList<RmRef> Members);

/// <summary>
/// Redmine in PNMT (docs/audit-redmine-zabbix-plan.md §6): the overview, the search, a ticket, PNMT's links and its copy of
/// linked tickets. <b>Only the configured projects:</b> a ticket of any other is "not found", whatever the key could read.
/// Filters use Redmine's query syntax (f[], op[], v[]), the one that takes several projects and priorities (R0).
/// </summary>
public sealed class RedmineService(PnmtdDbContext db, RedmineClient client, IOptionsMonitor<RedmineOptions> options, IMemoryCache cache,
    LocationTree tree, DashboardCache dashboard, IHttpContextAccessor accessor)
{
    public static readonly TimeSpan LookupsFor = TimeSpan.FromHours(1);

    public static readonly TimeSpan OverviewFor = TimeSpan.FromMinutes(1);

    private const string LookupsKey = "redmine:lookups";

    private RedmineOptions O => options.CurrentValue;

    public string IssueUrl(int id) => $"{O.BrowserUrl}/issues/{id}";

    // --- status and lookups -------------------------------------------------------------------------------------------

    public async Task<RedmineStatus> StatusAsync(CancellationToken ct)
    {
        if (!O.Enabled)
        {
            return new RedmineStatus(false, null, [], null);
        }
        try
        {
            var lookups = await LookupsAsync(ct);
            return new RedmineStatus(true, O.BrowserUrl, Projects(lookups), lookups.NotFound.Count > 0
                ? $"Not found in Redmine: {string.Join(", ", lookups.NotFound)} (Redmine__Projects)." : null);
        }
        catch (RedmineException e)
        {
            return new RedmineStatus(true, O.BrowserUrl, [], e.Message);
        }
    }

    public async Task<RedmineLookups> LookupsAsync(CancellationToken ct) =>
        (await cache.GetOrCreateAsync(LookupsKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = LookupsFor;
            return await LoadLookupsAsync(ct);
        }))!;

    public void ForgetLookups() => cache.Remove(LookupsKey);

    private async Task<RedmineLookups> LoadLookupsAsync(CancellationToken ct)
    {
        var all = new List<RmProject>();
        for (var offset = 0; ; offset += 100)
        {
            var page = await client.GetAsync<RmProjects>($"/projects.json?limit=100&offset={offset}", ct);
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
        var statuses = (await client.GetAsync<RmStatuses>("/issue_statuses.json", ct)).IssueStatuses;
        var trackers = (await client.GetAsync<RmTrackers>("/trackers.json", ct)).Trackers;
        var priorities = (await client.GetAsync<RmPriorities>("/enumerations/issue_priorities.json", ct)).IssuePriorities;
        var members = new Dictionary<int, RmRef>();
        foreach (var project in chosen)
        {
            try
            {
                var page = await client.GetAsync<RmMemberships>($"/projects/{project.Id}/memberships.json?limit=100", ct);
                foreach (var m in page.Memberships.Where(m => m.User is not null))
                {
                    members[m.User!.Id] = m.User;
                }
            }
            catch (RedmineException e) when (e.Failure == RedmineFailure.Forbidden)
            {
                // The account may not see the members: the filter offers none.
            }
        }
        var projects = all.Where(p => allowed.Contains(p.Id)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new RedmineLookups(projects, allowed, notFound, statuses, trackers, priorities,
            members.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<RedmineLookupsItem> LookupsItemAsync(CancellationToken ct)
    {
        var l = await LookupsAsync(ct);
        return new RedmineLookupsItem(Projects(l), l.Trackers.Select(t => new RedmineNamed(t.Id, t.Name ?? $"#{t.Id}")).ToList(),
            l.Statuses.Select(s => new RedmineStatusItem(s.Id, s.Name, s.IsClosed)).ToList(),
            l.Priorities.Select(p => new RedminePriorityItem(p.Id, p.Name, p.IsDefault)).ToList(),
            l.Members.Select(m => new RedmineNamed(m.Id, m.Name ?? $"#{m.Id}")).ToList());
    }

    private List<RedmineProjectItem> Projects(RedmineLookups l) =>
        l.Projects.Select(p => new RedmineProjectItem(p.Id, p.Identifier, p.Name, p.Parent?.Id, $"{O.BrowserUrl}/projects/{p.Identifier}")).ToList();

    // --- the search and the overview -----------------------------------------------------------------------------------

    /// <summary>Redmine's issue list with PNMT's filters (§6.3): always within the configured projects.</summary>
    public async Task<Paged<RedmineIssueItem>> SearchAsync(RedmineQuery query, CancellationToken ct)
    {
        var lookups = await LookupsAsync(ct);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var page = Math.Max(1, query.Page);
        // A number (1234, #1234) is the ticket itself.
        var text = query.Search?.Trim();
        if (text is { Length: > 0 } && int.TryParse(text.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            var found = await client.GetAsync<RmIssues>($"/issues.json?issue_id={number}&status_id=*", ct);
            var items = found.Issues.Where(i => lookups.Allowed.Contains(i.Project.Id)).ToList();
            return new Paged<RedmineIssueItem>(await ItemsAsync(items, ct), 1, size, items.Count);
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
            var linked = await db.RedmineLinks.AsNoTracking().Select(l => l.IssueId).Distinct().Take(500).ToListAsync(ct);
            if (linked.Count == 0)
            {
                return new Paged<RedmineIssueItem>([], page, size, 0);
            }
            q.Plain("issue_id", string.Join(",", linked));
        }
        q.Plain("sort", Sort(query.Sort));
        q.Plain("offset", ((page - 1) * size).ToString(CultureInfo.InvariantCulture));
        q.Plain("limit", size.ToString(CultureInfo.InvariantCulture));
        var result = await client.GetAsync<RmIssues>("/issues.json?" + q, ct);
        return new Paged<RedmineIssueItem>(await ItemsAsync(result.Issues, ct), page, size, result.TotalCount);
    }

    /// <summary>The most important tickets (§6.3): six calls to Redmine, cached a minute for everyone.</summary>
    public async Task<RedmineOverview> OverviewAsync(int? project, CancellationToken ct) =>
        (await cache.GetOrCreateAsync($"redmine:overview:{project}", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = OverviewFor;
            return await LoadOverviewAsync(project, ct);
        }))!;

    private async Task<RedmineOverview> LoadOverviewAsync(int? project, CancellationToken ct)
    {
        var lookups = await LookupsAsync(ct);
        async Task<RmIssues> ListAsync(Action<RedmineQueryString> filter, string sort, int limit = 10)
        {
            var q = Filters(lookups, project, "open");
            filter(q);
            q.Plain("sort", sort);
            q.Plain("limit", limit.ToString(CultureInfo.InvariantCulture));
            return await client.GetAsync<RmIssues>("/issues.json?" + q, ct);
        }
        // Above Redmine's default priority (High, Urgent, Immediate in a standard Redmine): nothing to configure.
        var defaultIndex = lookups.Priorities.ToList().FindIndex(p => p.IsDefault);
        var urgentIds = lookups.Priorities.Skip(defaultIndex + 1).Select(p => p.Id).ToList();
        var open = await ListAsync(_ => { }, "updated_on:desc", 1);
        var urgent = urgentIds.Count == 0 ? new RmIssues([], 0, 0, 0)
            : await ListAsync(q => q.Equal("priority_id", [.. urgentIds]), "priority:desc,created_on");
        var unassigned = await ListAsync(q => q.Filter("assigned_to_id", "!*"), "created_on");
        var overdue = await ListAsync(q => q.Filter("due_date", "<=", Yesterday()), "due_date");
        var stale = await ListAsync(q => q.Filter("updated_on", "<=", DateOnly.FromDateTime(Clock.UtcNow).AddDays(-Math.Max(1, O.StaleDays))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "updated_on");
        var recentQuery = Filters(lookups, project, "all");
        recentQuery.Plain("sort", "updated_on:desc");
        recentQuery.Plain("limit", "10");
        var recent = await client.GetAsync<RmIssues>("/issues.json?" + recentQuery, ct);
        return new RedmineOverview(Clock.UtcNow, O.StaleDays,
            new RedmineOverviewTiles(open.TotalCount, urgent.TotalCount, unassigned.TotalCount, overdue.TotalCount, stale.TotalCount),
            await ItemsAsync(urgent.Issues, ct), await ItemsAsync(unassigned.Issues, ct), await ItemsAsync(overdue.Issues, ct),
            await ProblemTicketsAsync(project, lookups, ct), await ItemsAsync(stale.Issues, ct), await ItemsAsync(recent.Issues, ct));
    }

    /// <summary>Open tickets linked to a failing sensor, its host or the host's location, from PNMT's copy (§6.3).</summary>
    private async Task<IReadOnlyList<RedmineIssueItem>> ProblemTicketsAsync(int? project, RedmineLookups lookups, CancellationToken ct)
    {
        var problems = (await ProblemsAsync(ct)).Select(p => p.Ticket).Where(t => project is null || t.ProjectId == project || lookups.Allowed.Contains(t.ProjectId))
            .DistinctBy(t => t.Id).Take(10).ToList();
        return problems;
    }

    /// <summary>For every failing sensor (the dashboard's problems), its first open linked ticket: the sensor's, its host's, its location's.</summary>
    public async Task<IReadOnlyList<RedmineProblemTicket>> ProblemsAsync(CancellationToken ct)
    {
        var failing = (await dashboard.GetAsync()).Problems.SelectMany(h => h.Sensors.Select(s => (Host: h.Id, Sensor: s.Id))).ToList();
        if (failing.Count == 0)
        {
            return [];
        }
        var sensorIds = failing.Select(f => f.Sensor).ToList();
        var hostIds = failing.Select(f => f.Host).Distinct().ToList();
        var hostLocations = await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id) && h.LocationId != null)
            .ToDictionaryAsync(h => h.Id, h => h.LocationId!.Value, ct);
        var locationIds = hostLocations.Values.Distinct().ToList();
        var links = await db.RedmineLinks.AsNoTracking()
            .Where(l => (l.SensorId != null && sensorIds.Contains(l.SensorId.Value)) || (l.HostId != null && hostIds.Contains(l.HostId.Value))
                || (l.LocationId != null && locationIds.Contains(l.LocationId.Value)))
            .Join(db.RedmineIssues.AsNoTracking().Where(i => !i.IsClosed && !i.Missing), l => l.IssueId, i => i.IssueId, (l, i) => new { Link = l, Issue = i })
            .ToListAsync(ct);
        var counts = await LinkCountsAsync(links.Select(x => x.Issue.IssueId).Distinct().ToList(), ct);
        var result = new List<RedmineProblemTicket>();
        foreach (var (host, sensor) in failing)
        {
            var match = links.Where(x => x.Link.SensorId == sensor).Concat(links.Where(x => x.Link.HostId == host))
                .Concat(hostLocations.TryGetValue(host, out var location) ? links.Where(x => x.Link.LocationId == location) : [])
                .OrderByDescending(x => x.Issue.UpdatedOn).FirstOrDefault();
            if (match is not null)
            {
                result.Add(new RedmineProblemTicket(sensor, Item(match.Issue, counts.GetValueOrDefault(match.Issue.IssueId))));
            }
        }
        return result;
    }

    // --- a ticket --------------------------------------------------------------------------------------------------------

    /// <summary>A ticket in full (§6.4), if it is in the configured projects; PNMT's copy is refreshed if it is linked.</summary>
    public async Task<RedmineIssueDetail> IssueAsync(int id, CancellationToken ct)
    {
        var lookups = await LookupsAsync(ct);
        RmIssue issue;
        try
        {
            issue = (await client.GetAsync<RmIssueEnvelope>($"/issues/{id}.json?include=journals,attachments,relations,children", ct)).Issue;
        }
        catch (RedmineException e) when (e.Failure is RedmineFailure.NotFound or RedmineFailure.Forbidden)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "redmine_not_found", $"#{id} is not in Redmine, or not for PNMT's account.");
        }
        if (!lookups.Allowed.Contains(issue.Project.Id))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "redmine_other_project", $"#{id} is not in PNMT's Redmine projects.");
        }
        if (await db.RedmineLinks.AnyAsync(l => l.IssueId == id, ct))
        {
            await UpsertCopyAsync([issue], ct);
            await db.SaveChangesAsync(ct);
        }
        var project = lookups.Projects.First(p => p.Id == issue.Project.Id);
        var attachments = issue.Attachments ?? [];
        var journals = (issue.Journals ?? []).Where(j => O.ShowPrivateNotes || !j.PrivateNotes)
            .Select(j => new RedmineJournalItem(j.Id, j.User?.Name, j.CreatedOn, string.IsNullOrWhiteSpace(j.Notes) ? null : Text(j.Notes!, attachments),
                (j.Details ?? []).Select(d => Change(d, lookups)).ToList(), j.PrivateNotes))
            .Where(j => j.Notes is not null || j.Changes.Count > 0)
            .ToList();
        var relatedIds = (issue.Relations ?? []).Select(r => r.IssueId == id ? r.IssueToId : r.IssueId)
            .Concat((issue.Children ?? []).Select(c => c.Id)).Concat(issue.Parent is { } parent ? [parent.Id] : []).Distinct().ToList();
        var related = relatedIds.Count == 0 ? [] : (await client.GetAsync<RmIssues>($"/issues.json?issue_id={string.Join(",", relatedIds)}&status_id=*&limit=100", ct))
            .Issues.Where(i => lookups.Allowed.Contains(i.Project.Id)).ToDictionary(i => i.Id);
        RedmineRelatedItem Related(int other, string relation) => related.TryGetValue(other, out var r)
            ? new RedmineRelatedItem(other, relation, r.Subject, r.Status.Name, r.Status.IsClosed)
            : new RedmineRelatedItem(other, relation, null, null, null);
        var relations = (issue.Parent is { } p ? [Related(p.Id, "parent")] : new List<RedmineRelatedItem>())
            .Concat((issue.Children ?? []).Select(c => Related(c.Id, "child")))
            .Concat((issue.Relations ?? []).Select(r => r.IssueId == id ? Related(r.IssueToId, Relation(r.RelationType, outgoing: true))
                : Related(r.IssueId, Relation(r.RelationType, outgoing: false))))
            .ToList();
        var fields = (issue.CustomFields ?? []).Select(f => new RedmineFieldItem(f.Name, FieldValue(f.Value))).Where(f => f.Value.Length > 0).ToList();
        return new RedmineIssueDetail(issue.Id, new RedmineProjectItem(project.Id, project.Identifier, project.Name, project.Parent?.Id,
                $"{O.BrowserUrl}/projects/{project.Identifier}"),
            issue.Tracker?.Name, issue.Status.Name, issue.Status.IsClosed, issue.Priority?.Name, issue.Author?.Name, issue.AssignedTo?.Name, issue.Subject,
            string.IsNullOrWhiteSpace(issue.Description) ? null : Text(issue.Description!, attachments), issue.StartDate, issue.DueDate, issue.DoneRatio,
            issue.EstimatedHours, issue.CreatedOn, issue.UpdatedOn, issue.ClosedOn, issue.IsPrivate, fields, journals,
            attachments.Select(a => new RedmineAttachmentItem(a.Id, a.Filename, a.Filesize, a.ContentType, a.Author?.Name, a.CreatedOn,
                AttachmentUrl(a))).ToList(),
            relations, await LinksOfAsync(id, ct), IssueUrl(issue.Id));
    }

    private string AttachmentUrl(RmAttachment a) => $"{O.BrowserUrl}/attachments/{a.Id}";

    private static string Relation(string type, bool outgoing) => (type, outgoing) switch
    {
        ("relates", _) => "related to",
        ("duplicates", true) => "duplicates",
        ("duplicates", false) => "duplicated by",
        ("blocks", true) => "blocks",
        ("blocks", false) => "blocked by",
        ("precedes", true) => "precedes",
        ("precedes", false) => "follows",
        ("copied_to", true) => "copied to",
        ("copied_to", false) => "copied from",
        _ => type.Replace('_', ' '),
    };

    private static string FieldValue(System.Text.Json.JsonElement? value) => value switch
    {
        null => "",
        { ValueKind: System.Text.Json.JsonValueKind.Array } a => string.Join(", ", a.EnumerateArray().Select(v => v.ToString())),
        { ValueKind: System.Text.Json.JsonValueKind.Null } => "",
        { } v => v.ToString(),
    };

    /// <summary>A line of a ticket's history: "Status: New → In progress", with names for Redmine's ids where PNMT knows them.</summary>
    private static RedmineChange Change(RmDetail d, RedmineLookups l)
    {
        string? Name(string? value, Func<int, string?> lookup) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? lookup(id) ?? $"#{id}" : value;
        return (d.Property, d.Name) switch
        {
            ("attr", "status_id") => new RedmineChange("Status", Name(d.OldValue, i => l.Statuses.FirstOrDefault(s => s.Id == i)?.Name),
                Name(d.NewValue, i => l.Statuses.FirstOrDefault(s => s.Id == i)?.Name)),
            ("attr", "priority_id") => new RedmineChange("Priority", Name(d.OldValue, i => l.Priorities.FirstOrDefault(p => p.Id == i)?.Name),
                Name(d.NewValue, i => l.Priorities.FirstOrDefault(p => p.Id == i)?.Name)),
            ("attr", "tracker_id") => new RedmineChange("Tracker", Name(d.OldValue, i => l.Trackers.FirstOrDefault(t => t.Id == i)?.Name),
                Name(d.NewValue, i => l.Trackers.FirstOrDefault(t => t.Id == i)?.Name)),
            ("attr", "assigned_to_id") => new RedmineChange("Assignee", Name(d.OldValue, i => l.Members.FirstOrDefault(m => m.Id == i)?.Name),
                Name(d.NewValue, i => l.Members.FirstOrDefault(m => m.Id == i)?.Name)),
            ("attr", "project_id") => new RedmineChange("Project", Name(d.OldValue, i => l.Projects.FirstOrDefault(p => p.Id == i)?.Name),
                Name(d.NewValue, i => l.Projects.FirstOrDefault(p => p.Id == i)?.Name)),
            ("attr", "description") => new RedmineChange("Description", null, "changed"),
            ("attr", var name) => new RedmineChange(Label(name), d.OldValue, d.NewValue),
            ("attachment", _) => new RedmineChange("File", d.OldValue, d.NewValue),
            ("relation", var type) => new RedmineChange($"Relation ({Relation(type, true)})", d.OldValue is null ? null : $"#{d.OldValue}",
                d.NewValue is null ? null : $"#{d.NewValue}"),
            ("cf", _) => new RedmineChange("Custom field", d.OldValue, d.NewValue),
            _ => new RedmineChange(d.Name, d.OldValue, d.NewValue),
        };

        static string Label(string name) => name switch
        {
            "subject" => "Subject",
            "done_ratio" => "Done %",
            "due_date" => "Due date",
            "start_date" => "Start date",
            "estimated_hours" => "Estimated time",
            "is_private" => "Private",
            "parent_id" => "Parent",
            _ => name.Replace('_', ' '),
        };
    }

    /// <summary>A text of Redmine's for the UI (§6.8): Markdown with Redmine's own references as links, or plain text.</summary>
    private RedmineText Text(string text, IReadOnlyList<RmAttachment> attachments)
    {
        if (O.TextFormatting is not ("common_mark" or "markdown"))
        {
            return new RedmineText("text", text);
        }
        return new RedmineText("markdown", RedmineMarkdown.Prepare(text, O.BrowserUrl,
            attachments.Select(a => (a.Filename, a.Id)).ToList()));
    }

    // --- links -----------------------------------------------------------------------------------------------------------

    public async Task<RedmineLinkItem> AddLinkAsync(RedmineLinkRequest request, CancellationToken ct)
    {
        var targets = new[] { request.PageId, request.LocationId, request.HostId, request.SensorId };
        new ApiValidator()
            .Check(targets.Count(t => t is not null) == 1, "target", "Exactly one of pageId, locationId, hostId and sensorId.")
            .Check(request.IssueId > 0, "issueId", "A ticket's number.")
            .Check(request.Note is null || request.Note.Trim().Length <= 500, "note", "At most 500 characters.")
            .ThrowIfInvalid();
        var (kind, targetId, name, subjects) = await TargetAsync(request, ct);
        var lookups = await LookupsAsync(ct);
        RmIssue issue;
        try
        {
            issue = (await client.GetAsync<RmIssueEnvelope>($"/issues/{request.IssueId}.json", ct)).Issue;
        }
        catch (RedmineException e) when (e.Failure is RedmineFailure.NotFound or RedmineFailure.Forbidden)
        {
            throw ApiException.Invalid("issueId", $"#{request.IssueId} is not in Redmine, or not for PNMT's account.");
        }
        if (!lookups.Allowed.Contains(issue.Project.Id))
        {
            throw ApiException.Invalid("issueId", $"#{request.IssueId} is not in PNMT's Redmine projects.");
        }
        var (by, byName) = UdpViews.User(accessor);
        var link = new RedmineLinkEntity
        {
            Id = Guid.NewGuid(), IssueId = issue.Id, PageId = request.PageId, LocationId = request.LocationId, HostId = request.HostId,
            SensorId = request.SensorId, Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(), CreatedAt = Clock.UtcNow,
            CreatedById = by, CreatedByName = byName,
        };
        db.RedmineLinks.Add(link);
        await UpsertCopyAsync([issue], ct);
        AuditLog.Record(db, AuditEvents.RedmineLinkAdded, $"#{issue.Id} {issue.Subject} → {kind.ToString().ToLowerInvariant()} {name}",
            AuditWho.From(accessor), new AuditTarget(AuditTargets.RedmineTicket, issue.Id.ToString(CultureInfo.InvariantCulture), issue.Subject, [targetId, .. subjects]));
        await db.SaveChangesAsync(ct);
        return new RedmineLinkItem(link.Id, link.IssueId, kind, targetId, name, link.Note, link.CreatedAt, link.CreatedByName);
    }

    public async Task<RedmineLinkItem> UpdateLinkAsync(Guid id, RedmineLinkNoteRequest request, CancellationToken ct)
    {
        new ApiValidator().Check(request.Note is null || request.Note.Trim().Length <= 500, "note", "At most 500 characters.").ThrowIfInvalid();
        var link = await db.RedmineLinks.SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw ApiException.NotFound("Link");
        var item = (await ItemsOfLinksAsync([link], ct)).Single();
        link.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        AuditLog.Record(db, AuditEvents.RedmineLinkChanged, $"#{link.IssueId} at {item.Kind.ToString().ToLowerInvariant()} {item.TargetName}: note changed",
            AuditWho.From(accessor), AuditTarget.Of(AuditTargets.RedmineTicket, link.IssueId.ToString(CultureInfo.InvariantCulture), null) with
            {
                Subjects = [item.TargetId],
            });
        await db.SaveChangesAsync(ct);
        return item with { Note = link.Note };
    }

    public async Task RemoveLinkAsync(Guid id, CancellationToken ct)
    {
        var link = await db.RedmineLinks.SingleOrDefaultAsync(l => l.Id == id, ct) ?? throw ApiException.NotFound("Link");
        var item = (await ItemsOfLinksAsync([link], ct)).Single();
        var subject = await db.RedmineIssues.AsNoTracking().Where(i => i.IssueId == link.IssueId).Select(i => i.Subject).FirstOrDefaultAsync(ct);
        db.RedmineLinks.Remove(link);
        AuditLog.Record(db, AuditEvents.RedmineLinkRemoved, $"#{link.IssueId} {subject} unlinked from {item.Kind.ToString().ToLowerInvariant()} {item.TargetName}",
            AuditWho.From(accessor), new AuditTarget(AuditTargets.RedmineTicket, link.IssueId.ToString(CultureInfo.InvariantCulture), subject, [item.TargetId]));
        await db.SaveChangesAsync(ct);
    }

    private async Task<(RedmineLinkKind Kind, Guid Id, string Name, IReadOnlyList<Guid> Subjects)> TargetAsync(RedmineLinkRequest r, CancellationToken ct)
    {
        if (r.PageId is { } page)
        {
            var p = await db.KbPages.AsNoTracking().Where(x => x.Id == page).Select(x => new { x.Number, x.Title }).SingleOrDefaultAsync(ct)
                ?? throw ApiException.NotFound("Page");
            return (RedmineLinkKind.Page, page, $"KB-{p.Number} {p.Title}", []);
        }
        if (r.LocationId is { } location)
        {
            var node = (await tree.GetAsync(ct)).Find(location) ?? throw ApiException.NotFound("Location");
            return (RedmineLinkKind.Location, location, node.Name, []);
        }
        if (r.HostId is { } host)
        {
            var h = await db.Hosts.AsNoTracking().Where(x => x.Id == host).Select(x => new { x.Name, x.LocationId }).SingleOrDefaultAsync(ct)
                ?? throw ApiException.NotFound("Host");
            return (RedmineLinkKind.Host, host, h.Name, h.LocationId is { } l ? [l] : []);
        }
        var sensor = r.SensorId!.Value;
        var s = await db.Sensors.AsNoTracking().Where(x => x.Id == sensor).Select(x => new { x.Name, Host = x.Parent.Name, x.ParentId })
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Sensor");
        return (RedmineLinkKind.Sensor, sensor, $"{s.Host} / {s.Name}", [s.ParentId]);
    }

    private async Task<IReadOnlyList<RedmineLinkItem>> LinksOfAsync(int issueId, CancellationToken ct) =>
        await ItemsOfLinksAsync(await db.RedmineLinks.AsNoTracking().Where(l => l.IssueId == issueId).OrderBy(l => l.CreatedAt).ToListAsync(ct), ct);

    /// <summary>The links with their objects' names now.</summary>
    private async Task<List<RedmineLinkItem>> ItemsOfLinksAsync(IReadOnlyList<RedmineLinkEntity> links, CancellationToken ct)
    {
        var pages = links.Where(l => l.PageId is not null).Select(l => l.PageId!.Value).ToList();
        var hosts = links.Where(l => l.HostId is not null).Select(l => l.HostId!.Value).ToList();
        var sensors = links.Where(l => l.SensorId is not null).Select(l => l.SensorId!.Value).ToList();
        var pageNames = await db.KbPages.AsNoTracking().Where(p => pages.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => $"KB-{p.Number} {p.Title}", ct);
        var hostNames = await db.Hosts.AsNoTracking().Where(h => hosts.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
        var sensorNames = await db.Sensors.AsNoTracking().Where(s => sensors.Contains(s.Id)).Select(s => new { s.Id, Name = s.Parent.Name + " / " + s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var snapshot = await tree.GetAsync(ct);
        return links.Select(l =>
        {
            var (kind, target, name) = l switch
            {
                { PageId: { } p } => (RedmineLinkKind.Page, p, pageNames.GetValueOrDefault(p)),
                { LocationId: { } x } => (RedmineLinkKind.Location, x, snapshot.Find(x)?.Name),
                { HostId: { } h } => (RedmineLinkKind.Host, h, hostNames.GetValueOrDefault(h)),
                _ => (RedmineLinkKind.Sensor, l.SensorId!.Value, sensorNames.GetValueOrDefault(l.SensorId!.Value)),
            };
            return new RedmineLinkItem(l.Id, l.IssueId, kind, target, name ?? "(gone)", l.Note, l.CreatedAt, l.CreatedByName);
        }).ToList();
    }

    // --- panels: an object's tickets ---------------------------------------------------------------------------------------

    /// <summary>
    /// An object's tickets (§6.6), from PNMT's copy: its own, then a sensor's host's, a host's location's and its parents'.
    /// A location with <paramref name="deep"/> also those of its sub-locations and their hosts (its Tickets tab).
    /// </summary>
    public async Task<RedmineRelated> RelatedAsync(Guid? pageId, Guid? locationId, Guid? hostId, Guid? sensorId, bool deep, CancellationToken ct)
    {
        if (new[] { pageId, locationId, hostId, sensorId }.Count(i => i is not null) != 1)
        {
            throw ApiException.Invalid("target", "Exactly one of pageId, locationId, hostId and sensorId.");
        }
        var snapshot = await tree.GetAsync(ct);
        var shown = new HashSet<Guid>();
        var groups = new List<RedmineRelatedGroup>();
        List<RedmineLinkedTicket> own;
        Guid? locationOfHost = null;
        if (pageId is { } page)
        {
            own = await LinkedAsync(l => l.PageId == page, shown, ct);
        }
        else if (sensorId is { } sensor)
        {
            var s = await db.Sensors.AsNoTracking().Where(x => x.Id == sensor).Select(x => new { x.ParentId, Host = x.Parent.Name, x.Parent.LocationId })
                .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Sensor");
            own = await LinkedAsync(l => l.SensorId == sensor, shown, ct);
            await GroupAsync(groups, RedmineLinkKind.Host, s.ParentId, s.Host, l => l.HostId == s.ParentId, shown, ct);
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
            if (deep)
            {
                var within = snapshot.SelfAndDescendants(location).Where(x => x != location).ToList();
                if (within.Count > 0)
                {
                    await GroupAsync(groups, RedmineLinkKind.Location, null, "its sub-locations", l => l.LocationId != null && within.Contains(l.LocationId.Value), shown, ct);
                }
                var all = snapshot.SelfAndDescendants(location).ToList();
                var hosts = await db.Hosts.AsNoTracking().Where(x => x.LocationId != null && all.Contains(x.LocationId.Value)).Select(x => x.Id).ToListAsync(ct);
                if (hosts.Count > 0)
                {
                    await GroupAsync(groups, RedmineLinkKind.Host, null, "its hosts", l => l.HostId != null && hosts.Contains(l.HostId.Value), shown, ct);
                    await GroupAsync(groups, RedmineLinkKind.Sensor, null, "their sensors",
                        l => l.SensorId != null && db.Sensors.Any(x => x.Id == l.SensorId && hosts.Contains(x.ParentId)), shown, ct);
                }
            }
            foreach (var ancestor in snapshot.Ancestors(location))
            {
                await GroupAsync(groups, RedmineLinkKind.Location, ancestor.Id, ancestor.Name, l => l.LocationId == ancestor.Id, shown, ct);
            }
        }
        if (locationOfHost is { } assigned && snapshot.Find(assigned) is { } node)
        {
            await GroupAsync(groups, RedmineLinkKind.Location, node.Id, node.Name, l => l.LocationId == node.Id, shown, ct);
            foreach (var ancestor in snapshot.Ancestors(node.Id))
            {
                await GroupAsync(groups, RedmineLinkKind.Location, ancestor.Id, ancestor.Name, l => l.LocationId == ancestor.Id, shown, ct);
            }
        }
        var asOf = await db.RedmineIssues.AsNoTracking().Select(i => (DateTime?)i.FetchedAt).MinAsync(ct);
        return new RedmineRelated(own, groups, asOf);
    }

    private async Task GroupAsync(List<RedmineRelatedGroup> groups, RedmineLinkKind kind, Guid? id, string name,
        System.Linq.Expressions.Expression<Func<RedmineLinkEntity, bool>> link, HashSet<Guid> shown, CancellationToken ct)
    {
        var tickets = await LinkedAsync(link, shown, ct);
        if (tickets.Count > 0)
        {
            groups.Add(new RedmineRelatedGroup(kind, id, name, tickets));
        }
    }

    /// <summary>The links that match, open tickets first, each ticket once across the groups.</summary>
    private async Task<List<RedmineLinkedTicket>> LinkedAsync(System.Linq.Expressions.Expression<Func<RedmineLinkEntity, bool>> link, HashSet<Guid> shown,
        CancellationToken ct)
    {
        var rows = await db.RedmineLinks.AsNoTracking().Where(link)
            .Join(db.RedmineIssues.AsNoTracking(), l => l.IssueId, i => i.IssueId, (l, i) => new { Link = l, Issue = i })
            .OrderBy(x => x.Issue.IsClosed).ThenByDescending(x => x.Issue.UpdatedOn).Take(200).ToListAsync(ct);
        var counts = await LinkCountsAsync(rows.Select(r => r.Issue.IssueId).Distinct().ToList(), ct);
        var seen = new HashSet<int>();
        var result = new List<RedmineLinkedTicket>();
        foreach (var r in rows)
        {
            if (shown.Contains(r.Link.Id) || !seen.Add(r.Issue.IssueId))
            {
                continue;
            }
            shown.Add(r.Link.Id);
            result.Add(new RedmineLinkedTicket(r.Link.Id, r.Link.Note, Item(r.Issue, counts.GetValueOrDefault(r.Issue.IssueId))));
        }
        return result;
    }

    // --- the copy ----------------------------------------------------------------------------------------------------------

    /// <summary>Writes the tickets' headers into PNMT's copy (added to the context; the caller saves).</summary>
    internal async Task UpsertCopyAsync(IReadOnlyList<RmIssue> issues, CancellationToken ct)
    {
        var ids = issues.Select(i => i.Id).ToList();
        var existing = await db.RedmineIssues.Where(i => ids.Contains(i.IssueId)).ToDictionaryAsync(i => i.IssueId, ct);
        foreach (var issue in issues)
        {
            if (!existing.TryGetValue(issue.Id, out var row))
            {
                row = new RedmineIssueEntity { IssueId = issue.Id };
                db.RedmineIssues.Add(row);
            }
            Copy(row, issue);
        }
    }

    internal static void Copy(RedmineIssueEntity row, RmIssue issue)
    {
        row.ProjectId = issue.Project.Id;
        row.ProjectName = Cut(issue.Project.Name ?? "", 200);
        row.TrackerName = issue.Tracker?.Name;
        row.StatusName = Cut(issue.Status.Name, 100);
        row.IsClosed = issue.Status.IsClosed;
        row.PriorityId = issue.Priority?.Id;
        row.PriorityName = issue.Priority?.Name;
        row.Subject = Cut(issue.Subject, 255);
        row.AssignedToName = issue.AssignedTo?.Name;
        row.UpdatedOn = Clock.AsUtc(issue.UpdatedOn);
        row.DueDate = issue.DueDate;
        row.FetchedAt = Clock.UtcNow;
        row.Missing = false;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    // --- shared ------------------------------------------------------------------------------------------------------------

    private async Task<List<RedmineIssueItem>> ItemsAsync(IReadOnlyList<RmIssue> issues, CancellationToken ct)
    {
        var counts = await LinkCountsAsync(issues.Select(i => i.Id).ToList(), ct);
        return issues.Select(i => new RedmineIssueItem(i.Id, i.Project.Id, i.Project.Name ?? "", i.Tracker?.Name, i.Status.Name, i.Status.IsClosed,
            i.Priority?.Name, i.Subject, i.AssignedTo?.Name, Clock.AsUtc(i.UpdatedOn), i.DueDate, IssueUrl(i.Id), counts.GetValueOrDefault(i.Id))).ToList();
    }

    private RedmineIssueItem Item(RedmineIssueEntity i, int links) => new(i.IssueId, i.ProjectId, i.ProjectName, i.TrackerName, i.StatusName, i.IsClosed,
        i.PriorityName, i.Subject, i.AssignedToName, Clock.AsUtc(i.UpdatedOn), i.DueDate, IssueUrl(i.IssueId), links);

    private async Task<Dictionary<int, int>> LinkCountsAsync(List<int> ids, CancellationToken ct) => ids.Count == 0 ? []
        : await db.RedmineLinks.AsNoTracking().Where(l => ids.Contains(l.IssueId)).GroupBy(l => l.IssueId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    /// <summary>The configured projects, or one of them (with its subprojects): always Redmine's query syntax.</summary>
    private RedmineQueryString Filters(RedmineLookups lookups, int? project, string status)
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
