using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Redmine;
using PNMTD.Services.Domain;
using PNMTD.Zabbix;

namespace PNMTD.Cookie;

/// <summary>What a run of the case book did, for the tests and the log.</summary>
public sealed record CaseBookResult(int Opened, int Changed, int Closed, int OpenCases, bool TicketsRead, string? Error);

/// <summary>
/// The case book (docs/cookie-plan.md §4.3, §7, §19.3), in code: a case per host for its bad sensors and Zabbix problems, a
/// case per watched ticket with its clock, progress, the duties that each case has now, and the code's own status. It runs
/// whether Cookie is started or not; it only needs <c>Cookie__Enabled=true</c>.
/// </summary>
public sealed class CaseBook(IDbContextFactory<PnmtdDbContext> dbs, IServiceScopeFactory scopes, CookieSettings settings, ZabbixSnapshot zabbix,
    RedmineClient redmine, IOptionsMonitor<RedmineOptions> redmineOptions, IMemoryCache cache, CookieSignals signals,
    PNMTD.Availability.AvailabilityPlan plan, ILogger<CaseBook> logger)
{
    public const int MaxTicketPages = 10;
    public static readonly TimeSpan StaffFor = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = CookieSettings.Json;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<CaseBookResult> RunAsync(bool readTickets, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await RunLockedAsync(readTickets, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CaseBookResult> RunLockedAsync(bool readTickets, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var counts = new Counts();
        var caseSettings = await settings.GetAsync<CaseSettings>(ct);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();
        // Duties that fell due since the last run wake Cookie; one waiting for working hours doesn't, again and again.
        var lastRun = (await RuntimeAsync(db, ct)).CaseBookAt ?? now.AddMinutes(-1);

        await ProblemsAsync(db, scope.ServiceProvider.GetRequiredService<MonitoringReader>(), caseSettings, now, counts, ct);
        string? error = null;
        var ticketsRead = false;
        if (readTickets && redmineOptions.CurrentValue.Enabled)
        {
            try
            {
                var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
                await TicketsAsync(db, scope.ServiceProvider.GetRequiredService<RedmineService>(), redmineSettings, now, counts, ct);
                ticketsRead = true;
            }
            catch (RedmineException e)
            {
                // The ticket cases stay as they were: an unreachable Redmine closes nothing.
                error = $"Redmine: {e.Message}";
                logger.LogWarning("The case book couldn't read the watched tickets: {Error}", e.Message);
            }
        }
        await db.SaveChangesAsync(ct);
        await DutiesAsync(db, caseSettings, now, lastRun, counts, ct);
        await StatusAsync(db, now, ticketsRead, error, ct);
        db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_COOKIE_CASEBOOK);
        await db.SaveChangesAsync(ct);
        if (counts.Opened + counts.Changed + counts.Closed + counts.DutiesDue > 0)
        {
            signals.Raise($"case book: {counts.Opened} opened, {counts.Changed} changed, {counts.Closed} closed, {counts.DutiesDue} due");
        }
        var open = await db.CookieCases.CountAsync(c => c.Open, ct);
        return new CaseBookResult(counts.Opened, counts.Changed, counts.Closed, open, ticketsRead, error);
    }

    private sealed class Counts
    {
        public int Opened;
        public int Changed;
        public int Closed;
        public int DutiesDue;
    }

    // --- Problems: a case per host -----------------------------------------------------------------------------------

    private sealed record ProblemGroup(string Key, Guid? HostId, long? ZabbixHostId, string Name, List<CaseMember> Members);

    private async Task ProblemsAsync(PnmtdDbContext db, MonitoringReader reader, CaseSettings caseSettings, DateTime now, Counts counts,
        CancellationToken ct)
    {
        var snapshot = await reader.SnapshotAsync(null, ct);
        var hosts = await db.Hosts.AsNoTracking().Select(h => new { h.Id, h.Name, h.LocationId, h.Importance, h.Notes }).ToDictionaryAsync(h => h.Id, ct);
        var groups = new Dictionary<string, ProblemGroup>();
        ProblemGroup Group(string key, Guid? hostId, long? zabbixHostId, string name) =>
            groups.TryGetValue(key, out var g) ? g : groups[key] = new ProblemGroup(key, hostId, zabbixHostId, name, []);

        foreach (var s in snapshot.Sensors.Values.Where(s => !s.IsContainer && s.State == SensorState.Failing))
        {
            Group($"host:{s.Info.HostId}", s.Info.HostId, null, s.Info.HostName).Members.Add(new CaseMember(CaseMember.Sensor, s.Info.Id.ToString(),
                s.Info.Name, Clock.AsUtc(s.FailingSince ?? s.LastEventAt ?? now), s.LastCode, Cut(s.LastMessage, 300), null, false,
                s.Info.EffectiveImportance.ToString(), SensorType: s.Info.Type.ToString()));
        }

        var problems = zabbix.Current.Problems.Where(p => p.Severity >= caseSettings.ZabbixMinSeverity && !p.Suppressed && p.Hosts.Count > 0).ToList();
        if (problems.Count > 0)
        {
            var attached = (await db.ZabbixSensors.AsNoTracking().Select(z => new { z.ZabbixHostId, z.Sensor.ParentId }).ToListAsync(ct))
                .GroupBy(z => z.ZabbixHostId).ToDictionary(g => g.Key, g => g.First().ParentId);
            foreach (var p in problems)
            {
                var zh = p.Hosts[0];
                var zabbixHostId = ZabbixPoller.Int(zh.Hostid);
                var group = attached.TryGetValue(zabbixHostId, out var hostId) && hosts.TryGetValue(hostId, out var host)
                    ? Group($"host:{hostId}", hostId, null, host.Name)
                    : Group($"zabbix:{zabbixHostId}", null, zabbixHostId, zh.Name);
                group.Members.Add(new CaseMember(CaseMember.Zabbix, p.EventId, p.Name, Clock.AsUtc(p.Since), null, Cut(p.OperationalData, 300),
                    p.Severity.ToString(), p.Acknowledged));
            }
        }

        var open = await db.CookieCases.Where(c => c.Open && c.Kind == CookieCaseKind.Problem).ToListAsync(ct);
        var byKey = open.ToDictionary(c => c.Key);
        var sensorIds = groups.Values.SelectMany(g => g.Members).Where(m => m.Type == CaseMember.Sensor).Select(m => Guid.Parse(m.Id)).ToList();
        var sensorNotes = await db.Sensors.AsNoTracking().Where(s => sensorIds.Contains(s.Id)).Select(s => new { s.Id, s.Notes })
            .ToDictionaryAsync(s => s.Id, s => s.Notes, ct);
        var linked = await LinkedTicketsAsync(db, groups.Values.Where(g => g.HostId is not null).Select(g => g.HostId!.Value).ToList(), sensorIds, ct);

        foreach (var group in groups.Values)
        {
            // Each sensor's notes as a short hash, and the host's in the case: a change of either is progress (§7.1).
            var members = group.Members.OrderBy(m => m.Since)
                .Select(m => m.Type == CaseMember.Sensor ? m with { NotesHash = Hash(sensorNotes.GetValueOrDefault(Guid.Parse(m.Id)) ?? "")[..16] } : m)
                .ToList();
            var host = group.HostId is { } id ? hosts.GetValueOrDefault(id) : null;
            var notes = Hash(host?.Notes ?? "");
            // A sensor weighs by its own importance (or its host's); a Zabbix problem by its severity.
            var importance = members.Select(ImportanceOf).Max();
            var title = members.Count == 1 ? $"{group.Name}: {members[0].Name}" : $"{group.Name}: {members.Count} problems";
            var areas = new List<string>();
            if (members.Any(m => m.Type == CaseMember.Sensor))
            {
                areas.Add("Monitoring");
            }
            if (members.Any(m => m.Type == CaseMember.Zabbix))
            {
                areas.Add("Zabbix");
            }

            if (!byKey.TryGetValue(group.Key, out var c))
            {
                var opened = members.Min(m => m.Since);
                c = new CookieCaseEntity
                {
                    Id = Guid.NewGuid(), Kind = CookieCaseKind.Problem, Key = group.Key, Title = Cut(title, 300)!, HostId = group.HostId,
                    LocationId = host?.LocationId, ZabbixHostId = group.ZabbixHostId, Importance = importance, OpenedAt = opened < now ? opened : now,
                    ChangedAt = now, ProgressAt = opened < now ? opened : now, Members = JsonSerializer.Serialize(members, Json), NotesHash = notes,
                    Areas = areas,
                };
                // An acknowledged problem, or a ticket already moving, is someone taking care.
                if (members.Any(m => m.Acknowledged))
                {
                    c.ProgressAt = now;
                }
                db.CookieCases.Add(c);
                Entry(db, c.Id, now, "opened", string.Join("; ", members.Select(Describe)));
                counts.Opened++;
                continue;
            }

            var before = JsonSerializer.Deserialize<List<CaseMember>>(c.Members, Json) ?? [];
            var changed = false;
            foreach (var m in members.Where(m => before.All(b => b.Ref != m.Ref)))
            {
                Entry(db, c.Id, now, "bad", Describe(m));
                changed = true;
            }
            // A stopped problem that grows goes on by itself (§4.3): something new is failing that the stop didn't cover.
            if (c.StoppedAt is not null && members.Any(m => before.All(b => b.Ref != m.Ref)))
            {
                Entry(db, c.Id, now, "go-on", $"Cookie goes on with it: something new is failing (stopped by {c.StoppedByName} on " +
                    $"{Clock.ToLocal(c.StoppedAt.Value):d MMM})");
                c.StoppedAt = null;
                c.StoppedById = null;
                c.StoppedByName = null;
                c.StopComment = null;
            }
            foreach (var b in before.Where(b => members.All(m => m.Ref != b.Ref)))
            {
                Entry(db, c.Id, now, "ok", $"{b.Name} is fine again.");
                changed = true;
            }
            var progress = new List<string>();
            foreach (var m in members.Where(m => m.Acknowledged && !before.Any(b => b.Ref == m.Ref && b.Acknowledged)))
            {
                progress.Add($"acknowledged in Zabbix: {m.Name}");
            }
            if (c.NotesHash != notes)
            {
                progress.Add("the host's notes changed");
                c.NotesHash = notes;
            }
            foreach (var m in members.Where(m => before.Any(b => b.Ref == m.Ref && b.NotesHash is not null && b.NotesHash != m.NotesHash)))
            {
                progress.Add($"a note on {m.Name}");
            }
            var moved = linked.Where(t => (t.HostId == c.HostId || members.Any(m => m.Id == t.SensorId?.ToString())) && t.UpdatedOn > c.ProgressAt)
                .Select(t => t.IssueId).Distinct().ToList();
            if (moved.Count > 0)
            {
                progress.Add($"ticket {string.Join(", ", moved.Select(i => $"#{i}"))} moved");
            }
            if (progress.Count > 0)
            {
                c.ProgressAt = now;
                Entry(db, c.Id, now, "progress", Cut(string.Join("; ", progress), 2000)!);
                changed = true;
            }
            if (changed)
            {
                c.ChangedAt = now;
                counts.Changed++;
            }
            c.Members = JsonSerializer.Serialize(members, Json);
            c.Title = Cut(title, 300)!;
            c.Importance = importance;
            c.LocationId = host?.LocationId;
            c.Areas = areas;
        }

        foreach (var c in open.Where(c => !groups.ContainsKey(c.Key)))
        {
            c.Open = false;
            c.ClosedAt = now;
            c.ChangedAt = now;
            Entry(db, c.Id, now, "closed", "Everything is fine again.");
            counts.Closed++;
        }
    }

    private sealed record LinkedTicket(int IssueId, Guid? HostId, Guid? SensorId, bool IsClosed, DateTime UpdatedOn);

    /// <summary>The tickets linked to these hosts or sensors, from PNMT's copy (audit plan §6.7): their state and last update.</summary>
    private static async Task<List<LinkedTicket>> LinkedTicketsAsync(PnmtdDbContext db, List<Guid> hostIds, List<Guid> sensorIds, CancellationToken ct) =>
        hostIds.Count + sensorIds.Count == 0 ? [] : await db.RedmineLinks.AsNoTracking()
            .Where(l => (l.HostId != null && hostIds.Contains(l.HostId.Value)) || (l.SensorId != null && sensorIds.Contains(l.SensorId.Value)))
            .Join(db.RedmineIssues.AsNoTracking().Where(i => !i.Missing), l => l.IssueId, i => i.IssueId,
                (l, i) => new LinkedTicket(i.IssueId, l.HostId, l.SensorId, i.IsClosed, i.UpdatedOn))
            .ToListAsync(ct);

    private static Importance ImportanceOf(CaseMember m) => m.Type == CaseMember.Zabbix
        ? m.Severity switch
        {
            nameof(ZabbixSeverity.High) or nameof(ZabbixSeverity.Disaster) => Importance.High,
            nameof(ZabbixSeverity.Average) or nameof(ZabbixSeverity.Warning) => Importance.Normal,
            _ => Importance.Low,
        }
        : Enum.TryParse<Importance>(m.Importance, out var i) ? i : Importance.Normal;

    private static string Describe(CaseMember m) => m.Type == CaseMember.Zabbix
        ? $"Zabbix: {m.Name} ({m.Severity}{(m.Acknowledged ? ", acknowledged" : "")})"
        : $"{m.Name}: {(m.Code is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "failing")}{(string.IsNullOrWhiteSpace(m.Message) ? "" : " " + m.Message)}";

    // --- Tickets: a case per watched ticket ---------------------------------------------------------------------------

    private async Task TicketsAsync(PnmtdDbContext db, RedmineService service, CookieRedmineSettings redmineSettings, DateTime now, Counts counts,
        CancellationToken ct)
    {
        var lookups = await service.LookupsAsync(ct);
        var watched = redmineSettings.Projects.Where(p => lookups.Allowed.Contains(p.ProjectId)).ToDictionary(p => p.ProjectId);
        var open = await db.CookieCases.Where(c => c.Open && c.Kind == CookieCaseKind.Ticket).ToListAsync(ct);
        var byIssue = open.Where(c => c.IssueId is not null).ToDictionary(c => c.IssueId!.Value);
        var seen = new HashSet<int>();
        if (watched.Count > 0)
        {
            var issues = new List<RmIssue>();
            for (var page = 0; page < MaxTicketPages; page++)
            {
                var q = new RedmineQueryString();
                q.Equal("project_id", [.. watched.Keys]);
                q.Filter("status_id", "o");
                q.Plain("sort", "updated_on:desc");
                q.Plain("limit", "100");
                q.Plain("offset", (page * 100).ToString(System.Globalization.CultureInfo.InvariantCulture));
                var result = await redmine.GetAsync<RmIssues>($"/issues.json?{q}", ct);
                issues.AddRange(result.Issues);
                if (result.Issues.Count < 100 || issues.Count >= result.TotalCount)
                {
                    break;
                }
            }
            foreach (var issue in issues.Where(i => watched.ContainsKey(i.Project.Id)))
            {
                seen.Add(issue.Id);
                var project = watched[issue.Project.Id];
                var updated = Clock.AsUtc(issue.UpdatedOn);
                var isNew = !byIssue.TryGetValue(issue.Id, out var c);
                if (!isNew && c!.TicketUpdatedOn is { } known && known >= updated)
                {
                    continue;
                }
                var full = (await redmine.GetAsync<RmIssueEnvelope>($"/issues/{issue.Id}.json?include=journals", ct)).Issue;
                var staff = await StaffAsync(project, ct);
                var clock = CaseRules.Clock(full, staff);
                if (isNew)
                {
                    c = new CookieCaseEntity
                    {
                        Id = Guid.NewGuid(), Kind = CookieCaseKind.Ticket, Key = $"ticket:{issue.Id}", IssueId = issue.Id, ProjectId = issue.Project.Id,
                        OpenedAt = Clock.AsUtc(full.CreatedOn), ProgressAt = Clock.AsUtc(full.CreatedOn), ChangedAt = now, Areas = ["Redmine"],
                    };
                    db.CookieCases.Add(c);
                    Entry(db, c.Id, now, "opened", $"#{issue.Id} in {issue.Project.Name}, by {full.Author?.Name ?? "someone"}.");
                    counts.Opened++;
                }
                else
                {
                    var news = new List<string>();
                    if (clock.FirstAnswerAt is not null && c!.FirstAnswerAt is null)
                    {
                        news.Add("first answer by staff");
                    }
                    else if (clock.LastStaffUpdateAt > c!.LastStaffUpdateAt)
                    {
                        news.Add("updated by staff");
                    }
                    if (full.AssignedTo?.Id != c.AssigneeId)
                    {
                        news.Add($"assigned to {full.AssignedTo?.Name ?? "nobody"}");
                    }
                    if (full.Status.Id != c.StatusId)
                    {
                        news.Add($"status {full.Status.Name}");
                    }
                    Entry(db, c.Id, now, news.Count > 0 ? "progress" : "updated", news.Count > 0 ? string.Join("; ", news) : "Updated in Redmine.");
                    if (news.Count > 0)
                    {
                        c.ProgressAt = now;
                    }
                    counts.Changed++;
                }
                c.Title = Cut($"#{issue.Id} {full.Subject}", 300)!;
                c.ProjectId = full.Project.Id;
                c.AuthorId = full.Author?.Id;
                c.AuthorName = Cut(full.Author?.Name, 200);
                c.AuthorIsStaff = clock.AuthorIsStaff;
                c.AssigneeId = full.AssignedTo?.Id;
                c.AssigneeName = Cut(full.AssignedTo?.Name, 200);
                c.StatusId = full.Status.Id;
                c.StatusName = Cut(full.Status.Name, 100);
                c.TicketCreatedAt = Clock.AsUtc(full.CreatedOn);
                c.TicketUpdatedOn = Clock.AsUtc(full.UpdatedOn);
                c.FirstAnswerAt = clock.FirstAnswerAt;
                c.LastStaffUpdateAt = clock.LastStaffUpdateAt;
                c.Importance = PriorityImportance(full.Priority?.Id, lookups.Priorities);
                c.ChangedAt = now;
            }
        }

        // Not among the open tickets of a watched project any more: closed, moved, gone, or no longer watched.
        foreach (var c in open.Where(c => c.IssueId is { } id && !seen.Contains(id)))
        {
            var why = "No longer watched.";
            if (c.ProjectId is { } p && watched.ContainsKey(p))
            {
                try
                {
                    var issue = (await redmine.GetAsync<RmIssueEnvelope>($"/issues/{c.IssueId}.json", ct)).Issue;
                    why = issue.Status.IsClosed ? $"Closed in Redmine ({issue.Status.Name})." : watched.ContainsKey(issue.Project.Id) ? "" : "Moved to a project that isn't watched.";
                }
                catch (RedmineException e) when (e.Failure is RedmineFailure.NotFound or RedmineFailure.Forbidden)
                {
                    why = "No longer in Redmine.";
                }
            }
            if (why.Length == 0)
            {
                continue;
            }
            c.Open = false;
            c.ClosedAt = now;
            c.ChangedAt = now;
            Entry(db, c.Id, now, "closed", why);
            counts.Closed++;
        }
    }

    /// <summary>The project's members with a staff role (§7.2), cached for an hour; null when no role is set.</summary>
    private async Task<IReadOnlySet<int>?> StaffAsync(WatchedProject project, CancellationToken ct)
    {
        if (project.StaffRoleIds.Count == 0)
        {
            return null;
        }
        var key = $"cookie-staff:{project.ProjectId}:{string.Join(",", project.StaffRoleIds.Order())}";
        return await cache.GetOrCreateAsync(key, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = StaffFor;
            var staff = new HashSet<int>();
            for (var offset = 0; offset < 1000; offset += 100)
            {
                var page = await redmine.GetAsync<RmMemberships>($"/projects/{project.ProjectId}/memberships.json?limit=100&offset={offset}", ct);
                foreach (var m in page.Memberships.Where(m => m.User is not null && (m.Roles ?? []).Any(r => project.StaffRoleIds.Contains(r.Id))))
                {
                    staff.Add(m.User!.Id);
                }
                if (page.Memberships.Count < 100)
                {
                    break;
                }
            }
            return (IReadOnlySet<int>)staff;
        });
    }

    /// <summary>Above Redmine's default priority is High, below it Low.</summary>
    private static Importance PriorityImportance(int? priorityId, IReadOnlyList<RmPriority> priorities)
    {
        var index = priorities.Select((p, i) => (p.Id, i)).Where(p => p.Id == priorityId).Select(p => p.i).DefaultIfEmpty(-1).First();
        var standard = priorities.Select((p, i) => (p.IsDefault, i)).Where(p => p.IsDefault).Select(p => p.i).DefaultIfEmpty(-1).First();
        return index < 0 || standard < 0 ? Importance.Normal : index > standard ? Importance.High : index < standard ? Importance.Low : Importance.Normal;
    }

    // --- Duties ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The duties each case has now (§7, §19.1): added when new, moved while still due, removed when no longer due (a first
    /// answer came, the problem has a ticket). A duty that was done stays done, by its key.
    /// </summary>
    private async Task DutiesAsync(PnmtdDbContext db, CaseSettings caseSettings, DateTime now, DateTime lastRun, Counts counts, CancellationToken ct)
    {
        var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
        var calendar = await plan.CalendarAsync(ct);
        var closedSince = now.AddDays(-1);
        var cases = await db.CookieCases.AsNoTracking()
            .Where(c => c.Open || (c.Kind == CookieCaseKind.Problem && c.OwnIssueId != null && c.ClosedAt >= closedSince)).ToListAsync(ct);
        var problemHosts = cases.Where(c => c.Kind == CookieCaseKind.Problem && c.Open && c.HostId is not null).Select(c => c.HostId!.Value).ToList();
        var problemSensors = cases.Where(c => c.Kind == CookieCaseKind.Problem && c.Open)
            .SelectMany(c => (JsonSerializer.Deserialize<List<CaseMember>>(c.Members, Json) ?? []).Where(m => m.Type == CaseMember.Sensor))
            .Select(m => Guid.Parse(m.Id)).Distinct().ToList();
        var linked = await LinkedTicketsAsync(db, problemHosts, problemSensors, ct);

        var plans = new Dictionary<Guid, IReadOnlyList<DutyPlan>>();
        foreach (var c in cases)
        {
            if (c.StoppedAt is not null)
            {
                // A stopped case (§4.3): nothing new falls due; what was due was closed as stopped.
                plans[c.Id] = [];
            }
            else if (c.Kind == CookieCaseKind.Ticket && c.ProjectId is { } p && redmineSettings.Of(p) is { } project)
            {
                plans[c.Id] = CaseRules.TicketDuties(c, project, calendar);
            }
            else if (c.Kind == CookieCaseKind.Problem && c.Open)
            {
                var members = JsonSerializer.Deserialize<List<CaseMember>>(c.Members, Json) ?? [];
                var openLinked = linked.Any(t => !t.IsClosed && (t.HostId == c.HostId || members.Any(m => m.Id == t.SensorId?.ToString())));
                plans[c.Id] = CaseRules.ProblemDuties(c, openLinked, caseSettings.UnresolvedHours);
            }
            else if (c.Kind == CookieCaseKind.Problem && c.ClosedAt is { } closed)
            {
                plans[c.Id] = [new DutyPlan("problem:closing-note", CookieDutyKind.ClosingNote, closed)];
            }
        }
        var ids = plans.Keys.ToList();
        var existing = await db.CookieDuties.Where(d => ids.Contains(d.CaseId)).ToListAsync(ct);
        var byKey = existing.ToDictionary(d => (d.CaseId, d.Key));
        foreach (var (caseId, list) in plans)
        {
            foreach (var step in list)
            {
                if (byKey.TryGetValue((caseId, step.Key), out var duty))
                {
                    if (duty.State == CookieDutyState.Due && duty.DueAt != step.DueAt)
                    {
                        duty.DueAt = step.DueAt;
                    }
                    continue;
                }
                db.CookieDuties.Add(new CookieDutyEntity { CaseId = caseId, Key = step.Key, Kind = step.Kind, DueAt = step.DueAt, CreatedAt = now });
            }
        }
        var planned = plans.SelectMany(p => p.Value.Select(d => (p.Key, d.Key))).ToHashSet();
        db.CookieDuties.RemoveRange(existing.Where(d => d.State == CookieDutyState.Due && !planned.Contains((d.CaseId, d.Key))));
        // Duties of cases that closed, and aren't planned again, go too.
        await db.CookieDuties.Where(d => d.State == CookieDutyState.Due && !ids.Contains(d.CaseId)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        counts.DutiesDue = await db.CookieDuties.CountAsync(d => d.State == CookieDutyState.Due && d.DueAt > lastRun && d.DueAt <= now, ct);
    }

    // --- The code's status ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Today's staffing as the plan says it, when it is short (§8.4): on a working day, nobody responsible who works, or a
    /// count below its minimum.
    /// </summary>
    private async Task<string?> StaffingAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var day = await plan.DayAsync(today, ct);
        if (day.Closed(today) || day.People.Count == 0)
        {
            return null;
        }
        var parts = new List<string>();
        if (day.ResponsibleOn(today).UserIds.Count == 0)
        {
            parts.Add("nobody responsible works today");
        }
        foreach (var column in day.Settings.CountColumns.Where(c => c.Shown && c.Minimum is > 0))
        {
            var count = day.Count(column, today);
            if (count < column.Minimum)
            {
                parts.Add($"{column.Name}: {count} today, the minimum is {column.Minimum}");
            }
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private async Task StatusAsync(PnmtdDbContext db, DateTime now, bool ticketsRead, string? error, CancellationToken ct)
    {
        var open = await db.CookieCases.AsNoTracking().Where(c => c.Open && c.Kind != CookieCaseKind.Request).ToListAsync(ct);
        var openIds = open.Select(c => c.Id).ToList();
        var duties = await db.CookieDuties.AsNoTracking().Where(d => openIds.Contains(d.CaseId) && d.DueAt <= now
            && d.Kind != CookieDutyKind.ProblemTicket && d.Kind != CookieDutyKind.ClosingNote).ToListAsync(ct);
        var locations = await db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var status = CookieStatusRules.Compute(open, duties, locations, await StaffingAsync(ct));
        var runtime = await RuntimeAsync(db, ct);
        runtime.CodeStatus = status.State;
        runtime.CodeReason = status.Reason;
        runtime.CodeCases = [.. status.Cases];
        runtime.CodeStatusAt = now;
        runtime.CaseBookAt = now;
        if (ticketsRead)
        {
            runtime.TicketsAt = now;
        }
        if (ticketsRead || error is not null)
        {
            runtime.CaseBookError = error;
        }
    }

    /// <summary>The one runtime row, created on first use.</summary>
    public static async Task<CookieRuntimeEntity> RuntimeAsync(PnmtdDbContext db, CancellationToken ct)
    {
        await CookieLoop.EnsureAsync(db, ct);
        return await db.CookieRuntime.SingleAsync(r => r.SingletonId == CookieRuntimeEntity.Id, ct);
    }

    public static void Entry(PnmtdDbContext db, Guid caseId, DateTime at, string kind, string text, string actor = "code", string? actorName = null) =>
        db.CookieCaseEntries.Add(new CookieCaseEntryEntity { CaseId = caseId, At = at, Kind = kind, Text = Cut(text, 2000)!, Actor = actor, ActorName = actorName });

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>The code's overall state (docs/cookie-plan.md §8, §19.1): from the open cases and the deadlines that have passed.</summary>
public static class CookieStatusRules
{
    public sealed record Result(OverallState State, string Reason, IReadOnlyList<Guid> Cases);

    /// <summary>
    /// Alert: a problem of importance High, or 3 hosts with problems at one location. Warning: any other problem of normal
    /// importance, or a ticket's deadline missed. Info: only minor problems, or a ticket's reminder due. All fine otherwise.
    /// </summary>
    public static Result Compute(IReadOnlyList<CookieCaseEntity> all, IReadOnlyList<CookieDutyEntity> passed, IReadOnlyDictionary<Guid, string> locations,
        string? staffing = null)
    {
        // A stopped case raises the state to Info at most (§4.3); the plan's staffing gives Info too (§8.4).
        var stopped = all.Where(c => c.StoppedAt is not null).ToList();
        var stoppedIds = stopped.Select(c => c.Id).ToHashSet();
        var open = all.Where(c => c.StoppedAt is null).ToList();
        passed = [.. passed.Where(d => !stoppedIds.Contains(d.CaseId))];
        var problems = open.Where(c => c.Kind == CookieCaseKind.Problem).OrderByDescending(c => c.Importance).ThenBy(c => c.OpenedAt).ToList();
        var missed = passed.Where(d => d.Kind is CookieDutyKind.FirstAnswerMissed or CookieDutyKind.UpdateMissed).Select(d => d.CaseId).Distinct().ToList();
        var reminders = passed.Where(d => d.Kind is CookieDutyKind.FirstAnswerReminder or CookieDutyKind.UpdateReminder).Select(d => d.CaseId)
            .Distinct().Except(missed).ToList();
        var high = problems.Where(c => c.Importance == Importance.High).ToList();
        var crowded = problems.Where(c => c.LocationId is not null && c.HostId is not null).GroupBy(c => c.LocationId!.Value)
            .Where(g => g.Select(c => c.HostId).Distinct().Count() >= 3).OrderByDescending(g => g.Count()).FirstOrDefault();

        string More(int count, string what) => count > 0 ? $"; {count} more {what}" : "";
        if (high.Count > 0)
        {
            return new(OverallState.Alert, $"{high[0].Title}{More(problems.Count - 1, problems.Count == 2 ? "problem" : "problems")}",
                [.. high.Concat(problems).Distinct().Take(10).Select(c => c.Id)]);
        }
        if (crowded is not null)
        {
            var name = locations.GetValueOrDefault(crowded.Key) ?? "one location";
            return new(OverallState.Alert, $"{crowded.Select(c => c.HostId).Distinct().Count()} hosts at {name} have problems",
                [.. crowded.Take(10).Select(c => c.Id)]);
        }
        var normal = problems.Where(c => c.Importance == Importance.Normal).ToList();
        if (normal.Count > 0 || missed.Count > 0)
        {
            var parts = new List<string>();
            if (normal.Count > 0)
            {
                parts.Add(normal.Count == 1 ? normal[0].Title : $"{normal.Count} problems, the first: {normal[0].Title}");
            }
            if (missed.Count > 0)
            {
                parts.Add(missed.Count == 1 ? "a ticket's deadline missed" : $"{missed.Count} tickets' deadlines missed");
            }
            return new(OverallState.Warning, string.Join("; ", parts), [.. normal.Select(c => c.Id).Concat(missed).Take(10)]);
        }
        if (problems.Count > 0 || reminders.Count > 0 || stopped.Count > 0 || staffing is not null)
        {
            var parts = new List<string>();
            if (staffing is not null)
            {
                parts.Add(staffing);
            }
            if (problems.Count > 0)
            {
                parts.Add(problems.Count == 1 ? $"minor: {problems[0].Title}" : $"{problems.Count} minor problems");
            }
            if (reminders.Count > 0)
            {
                parts.Add(reminders.Count == 1 ? "a ticket waits for an answer" : $"{reminders.Count} tickets wait for an answer");
            }
            if (stopped.Count > 0)
            {
                parts.Add(stopped.Count == 1 ? $"stopped: {stopped[0].Title}" : $"{stopped.Count} stopped cases");
            }
            return new(OverallState.Info, string.Join("; ", parts), [.. problems.Select(c => c.Id).Concat(reminders).Concat(stoppedIds).Take(10)]);
        }
        return new(OverallState.AllFine, "Nothing needs attention.", []);
    }
}

/// <summary>
/// Runs the case book every <c>Cookie:CaseBookSeconds</c> (30), reading the tickets every <c>Cookie:TicketsMinutes</c> (5), and then
/// the duties that are due.
/// </summary>
public sealed class CaseBookTask(CaseBook book, DutyRunner duties, IOptionsMonitor<CookieOptions> options, ILogger<CaseBookTask> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        var ticketsAt = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            if (o.Enabled)
            {
                try
                {
                    var readTickets = Clock.UtcNow - ticketsAt >= TimeSpan.FromMinutes(Math.Max(1, o.TicketsMinutes));
                    var result = await book.RunAsync(readTickets, stoppingToken);
                    if (result.TicketsRead || result.Error is not null)
                    {
                        ticketsAt = Clock.UtcNow;
                    }
                    // The duties that fell due (docs/cookie-plan.md §19.1): they keep running whether Cookie is started or not.
                    await duties.RunAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogError(e, "The case book failed");
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(o.CaseBookSeconds, 5, 600)), stoppingToken);
        }
    }
}
