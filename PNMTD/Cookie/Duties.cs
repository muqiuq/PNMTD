using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Cookie;

/// <summary>Who is told what (docs/cookie-plan.md §8, §19.1): the recipient rules of Admin → Cookie → Notifications.</summary>
public static class Recipients
{
    /// <summary>What a message is about, for the rules' scope, urgency and time.</summary>
    public sealed record Subject(CookieCaseKind Kind, Guid? HostId, Guid? LocationId, int? ProjectId, Urgency Urgency, bool WorkingNow);

    public static bool Matches(RecipientRule rule, Subject s)
    {
        if (s.Urgency < rule.MinUrgency)
        {
            return false;
        }
        if (rule.When == RecipientWhen.WorkingHours && !s.WorkingNow || rule.When == RecipientWhen.OutsideWorkingHours && s.WorkingNow)
        {
            return false;
        }
        var value = rule.Value?.Trim() ?? "";
        return rule.Scope switch
        {
            RecipientScope.Everything => true,
            RecipientScope.Location => s.LocationId is { } l && string.Equals(value, l.ToString(), StringComparison.OrdinalIgnoreCase),
            RecipientScope.Host => s.HostId is { } h && string.Equals(value, h.ToString(), StringComparison.OrdinalIgnoreCase),
            RecipientScope.Project => s.ProjectId is { } p && value == p.ToString(CultureInfo.InvariantCulture),
            RecipientScope.CaseKind => string.Equals(value, s.Kind.ToString(), StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>The people the matching rules name, directly or through their groups, with the rules' channels.</summary>
    public static Dictionary<Guid, HashSet<RecipientChannel>> FromRules(NotificationSettings n, Subject s, IReadOnlyDictionary<Guid, List<Guid>> members)
    {
        var result = new Dictionary<Guid, HashSet<RecipientChannel>>();
        foreach (var rule in n.Rules.Where(r => Matches(r, s)))
        {
            foreach (var user in rule.Users.Concat(rule.Groups.SelectMany(g => members.GetValueOrDefault(g) ?? [])))
            {
                if (!result.TryGetValue(user, out var channels))
                {
                    result[user] = channels = [];
                }
                channels.UnionWith(rule.Channels);
            }
        }
        return result;
    }
}

/// <summary>
/// Turns the duties that are due into notes, tickets and messages, from template texts (docs/cookie-plan.md §3.4 A, §7,
/// §19.1). A duty that is off is closed as off, so switching it on later sends nothing old; a reminder or an escalation
/// outside working hours waits for the next working period; a reminder whose deadline is due as well gives way to it.
/// </summary>
public sealed class DutyRunner(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, Outbox outbox, IOptionsMonitor<RedmineOptions> redmineOptions,
    IConfiguration configuration, ILogger<DutyRunner> logger)
{
    public const int PerRun = 50;
    private static readonly JsonSerializerOptions Json = CookieSettings.Json;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await RunLockedAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> RunLockedAsync(CancellationToken ct)
    {
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var due = await db.CookieDuties.Where(d => d.State == CookieDutyState.Due && d.DueAt <= now).OrderBy(d => d.DueAt).Take(PerRun).ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }
        var actions = await settings.GetAsync<ActionSettings>(ct);
        var calendar = (await settings.GetAsync<HoursSettings>(ct)).Calendar();
        var working = calendar.IsWorking(now);
        var caseIds = due.Select(d => d.CaseId).Distinct().ToList();
        var cases = await db.CookieCases.Where(c => caseIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var dueKeys = due.Select(d => (d.CaseId, d.Key)).ToHashSet();
        var done = 0;
        foreach (var duty in due)
        {
            if (!cases.TryGetValue(duty.CaseId, out var c))
            {
                continue;
            }
            if (!actions.Duties.On(duty.Kind))
            {
                Close(duty, CookieDutyState.Off, "Switched off on Admin → Cookie → Actions.", now);
                done++;
                continue;
            }
            if (duty.Kind is CookieDutyKind.FirstAnswerReminder or CookieDutyKind.UpdateReminder
                && dueKeys.Contains((duty.CaseId, duty.Key.Replace(":reminder", ":missed", StringComparison.Ordinal))))
            {
                Close(duty, CookieDutyState.Off, "The deadline itself is due: no reminder before it.", now);
                done++;
                continue;
            }
            var toPeople = duty.Kind is CookieDutyKind.FirstAnswerReminder or CookieDutyKind.FirstAnswerMissed or CookieDutyKind.UpdateReminder
                or CookieDutyKind.UpdateMissed;
            if (toPeople && !working)
            {
                continue;
            }
            try
            {
                var (state, outcome) = duty.Kind switch
                {
                    CookieDutyKind.ProblemTicket => await TicketAsync(db, c, duty, ct),
                    CookieDutyKind.ClosingNote => await ClosingNoteAsync(db, c, duty, ct),
                    _ => await TicketDutyAsync(db, c, duty, calendar, working, ct),
                };
                Close(duty, state, outcome, now);
                CaseBook.Entry(db, c.Id, now, EntryKind(duty.Kind), outcome);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Cookie's duty {Key} on case {Case} failed", duty.Key, c.Id);
                Close(duty, CookieDutyState.Failed, CaseBook.Cut(e.Message, 500)!, now);
            }
            done++;
            await db.SaveChangesAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        return done;
    }

    private static void Close(CookieDutyEntity duty, CookieDutyState state, string outcome, DateTime now)
    {
        duty.State = state;
        duty.Outcome = CaseBook.Cut(outcome, 500);
        duty.DoneAt = now;
    }

    private static string EntryKind(CookieDutyKind kind) => kind switch
    {
        CookieDutyKind.FirstAnswerReminder or CookieDutyKind.UpdateReminder => "reminder",
        CookieDutyKind.FirstAnswerMissed or CookieDutyKind.UpdateMissed => "escalation",
        CookieDutyKind.ProblemTicket => "ticket",
        _ => "note",
    };

    /// <summary>The outcome of several sends: done when one went out, held back or failed when all were.</summary>
    private static (CookieDutyState, string) Summarise(IReadOnlyList<CookieOutboxEntity> rows, IReadOnlyList<string> notes)
    {
        var parts = rows.Select(r => $"{Outbox.Label(r.Channel)}{(r.Recipient.Length > 0 ? " to " + r.Recipient : "")}: {State(r)}").Concat(notes).ToList();
        var state = rows.Count == 0 ? CookieDutyState.Done
            : rows.Any(r => r.State is CookieOutboxState.Sent or CookieOutboxState.Simulated) ? CookieDutyState.Done
            : rows.All(r => r.State == CookieOutboxState.HeldBack) ? CookieDutyState.HeldBack
            : CookieDutyState.Failed;
        return (state, parts.Count == 0 ? "Nothing to send." : string.Join("; ", parts));
    }

    private static string State(CookieOutboxEntity r) => r.State switch
    {
        CookieOutboxState.Sent => "sent",
        CookieOutboxState.Simulated => "simulated",
        CookieOutboxState.HeldBack => (r.Detail ?? "held back").TrimEnd('.').ToLowerInvariant(),
        _ => "failed (" + (r.Detail ?? "").TrimEnd('.') + ")",
    };

    // --- A ticket's reminders and escalations (§7.2) --------------------------------------------------------------------

    private async Task<(CookieDutyState, string)> TicketDutyAsync(PnmtdDbContext db, CookieCaseEntity c, CookieDutyEntity duty, WorkingCalendar calendar,
        bool working, CancellationToken ct)
    {
        var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
        var notification = await settings.GetAsync<NotificationSettings>(ct);
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var project = c.ProjectId is { } p ? redmineSettings.Of(p) : null;
        if (project is null || c.IssueId is not { } issueId)
        {
            return (CookieDutyState.NotPossible, "The ticket's project is no longer watched.");
        }
        var missed = duty.Kind is CookieDutyKind.FirstAnswerMissed or CookieDutyKind.UpdateMissed;
        var urgency = CaseRules.UrgencyOf(duty.Kind);
        var text = DutyTexts.Ticket(duty.Kind, c, project, duty.DueAt, identity.Name, IssueUrl(issueId), configuration["PublicBaseUrl"]);
        var rows = new List<CookieOutboxEntity>();
        var notes = new List<string>();

        if (!missed)
        {
            if (redmineOptions.CurrentValue.CanWrite)
            {
                rows.Add(await outbox.SendAsync(new OutboxRequest(CookieChannel.RedminePrivateNote, text.Subject, text.Note, c.Id, duty.Id, IssueId: issueId,
                    Recipient: $"#{issueId}", Urgency: urgency), ct));
            }
            else
            {
                notes.Add("no note on the ticket: Redmine__WriteApiKey is not set");
            }
        }

        // Who the duty names: the assignee, or the escalation group when nobody is assigned or no contact matches;
        // a missed deadline tells both.
        var named = new HashSet<Guid>();
        var assignee = string.IsNullOrWhiteSpace(c.AssigneeName) ? null
            : notification.Contacts.FirstOrDefault(x => string.Equals(x.RedmineName?.Trim(), c.AssigneeName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (assignee is not null)
        {
            named.Add(assignee.UserId);
        }
        var groupIds = notification.Rules.SelectMany(r => r.Groups).Append(project.EscalationGroupId ?? Guid.Empty).Distinct().ToList();
        var members = await MembersAsync(db, groupIds, ct);
        if ((assignee is null || missed) && project.EscalationGroupId is { } group)
        {
            named.UnionWith(members.GetValueOrDefault(group) ?? []);
        }
        var local = TimeOnly.FromDateTime(Clock.LocalNow);
        var channels = named.ToDictionary(u => u, u =>
        {
            var contact = notification.ContactOf(u);
            var set = new HashSet<RecipientChannel>();
            if (!string.IsNullOrWhiteSpace(contact?.Email))
            {
                set.Add(RecipientChannel.Email);
            }
            if (urgency == Urgency.Urgent && !string.IsNullOrWhiteSpace(contact?.PushoverKey))
            {
                set.Add(RecipientChannel.Pushover);
            }
            return set;
        });
        var subject = new Recipients.Subject(CookieCaseKind.Ticket, null, null, c.ProjectId, urgency, working);
        foreach (var (user, ruleChannels) in Recipients.FromRules(notification, subject, members))
        {
            if (!channels.TryGetValue(user, out var set))
            {
                channels[user] = set = [];
            }
            set.UnionWith(ruleChannels);
        }
        var users = await db.Users.AsNoTracking().Where(u => channels.Keys.Contains(u.Id) && u.IsActive).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        foreach (var (user, set) in channels.Where(x => users.ContainsKey(x.Key)))
        {
            var contact = notification.ContactOf(user);
            if (set.Contains(RecipientChannel.Email) && !string.IsNullOrWhiteSpace(contact?.Email))
            {
                rows.Add(await outbox.SendAsync(new OutboxRequest(CookieChannel.Email, text.Subject, text.Message, c.Id, duty.Id, user, users[user],
                    contact.Email, issueId, Urgency: urgency), ct));
            }
            if (set.Contains(RecipientChannel.Pushover) && !string.IsNullOrWhiteSpace(contact?.PushoverKey))
            {
                if (contact.IsQuiet(local))
                {
                    notes.Add($"no Pushover to {users[user]}: quiet hours");
                }
                else
                {
                    rows.Add(await outbox.SendAsync(new OutboxRequest(CookieChannel.Pushover, text.Subject, text.Short, c.Id, duty.Id, user, users[user],
                        contact.PushoverKey, issueId, Urgency: urgency), ct));
                }
            }
        }
        if (users.Count == 0)
        {
            notes.Add(project.EscalationGroupId is null && assignee is null
                ? "nobody to tell: no contact matches the assignee, and the project has no escalation group"
                : "nobody to tell: no contact with an address");
        }
        return Summarise(rows, notes);
    }

    private static async Task<Dictionary<Guid, List<Guid>>> MembersAsync(PnmtdDbContext db, List<Guid> groups, CancellationToken ct) =>
        (await db.UserGroups.AsNoTracking().Where(m => groups.Contains(m.GroupId)).Select(m => new { m.GroupId, m.UserId }).ToListAsync(ct))
        .GroupBy(m => m.GroupId).ToDictionary(g => g.Key, g => g.Select(m => m.UserId).ToList());

    // --- A problem's ticket and its closing note (§7.1) -----------------------------------------------------------------

    private async Task<(CookieDutyState, string)> TicketAsync(PnmtdDbContext db, CookieCaseEntity c, CookieDutyEntity duty, CancellationToken ct)
    {
        var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
        if (!redmineOptions.CurrentValue.CanWrite || redmineSettings.TicketProjectId is not { } projectId)
        {
            return (CookieDutyState.NotPossible,
                "Not possible: a ticket needs Redmine__WriteApiKey and a project under Admin → Cookie → Redmine → Cookie's tickets.");
        }
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var caseSettings = await settings.GetAsync<CaseSettings>(ct);
        var members = JsonSerializer.Deserialize<List<CaseMember>>(c.Members, Json) ?? [];
        var timeline = await db.CookieCaseEntries.AsNoTracking().Where(e => e.CaseId == c.Id).OrderBy(e => e.At).Take(30).ToListAsync(ct);
        var text = DutyTexts.ProblemTicket(c, members, timeline, caseSettings.UnresolvedHours, identity.Name, configuration["PublicBaseUrl"]);
        var row = await outbox.SendAsync(new OutboxRequest(CookieChannel.RedmineTicket, text.Subject, text.Note, c.Id, duty.Id, Recipient: $"project {projectId}",
            ProjectId: projectId, TrackerId: redmineSettings.TicketTrackerId, Urgency: Urgency.Important), ct);
        if (row.State == CookieOutboxState.Sent && row.IssueId is { } issueId)
        {
            c.OwnIssueId = issueId;
            if (c.HostId is { } hostId && !await db.RedmineLinks.AnyAsync(l => l.HostId == hostId && l.IssueId == issueId, ct))
            {
                db.RedmineLinks.Add(new RedmineLinkEntity
                {
                    Id = Guid.NewGuid(), IssueId = issueId, HostId = hostId, Note = $"Opened by {identity.Name}", CreatedAt = Clock.UtcNow,
                    CreatedByName = identity.Name,
                });
            }
            return (CookieDutyState.Done, $"Ticket #{issueId} opened.");
        }
        return Summarise([row], []);
    }

    private async Task<(CookieDutyState, string)> ClosingNoteAsync(PnmtdDbContext db, CookieCaseEntity c, CookieDutyEntity duty, CancellationToken ct)
    {
        if (c.OwnIssueId is not { } issueId)
        {
            return (CookieDutyState.Off, "The case has no ticket of Cookie's.");
        }
        if (await db.RedmineIssues.AsNoTracking().AnyAsync(i => i.IssueId == issueId && i.IsClosed, ct))
        {
            return (CookieDutyState.Off, $"#{issueId} is closed already.");
        }
        if (!redmineOptions.CurrentValue.CanWrite)
        {
            return (CookieDutyState.NotPossible, "Not possible: Redmine__WriteApiKey is not set.");
        }
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var note = DutyTexts.ClosingNote(c, identity.Name);
        var row = await outbox.SendAsync(new OutboxRequest(CookieChannel.RedmineNote, $"#{issueId} resolved", note, c.Id, duty.Id, IssueId: issueId,
            Recipient: $"#{issueId}", Urgency: Urgency.Info), ct);
        return Summarise([row], []);
    }

    private string? IssueUrl(int issueId) => redmineOptions.CurrentValue is { Enabled: true } o ? $"{o.BrowserUrl}/issues/{issueId}" : null;
}

/// <summary>The template texts of the code's duties (docs/cookie-plan.md §3.4 A): Cookie writes its own in nine point five.</summary>
public static class DutyTexts
{
    public sealed record Text(string Subject, string Short, string Message, string Note);

    private static string When(DateTime utc) => Clock.ToLocal(utc).ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture);

    public static Text Ticket(CookieDutyKind kind, CookieCaseEntity c, WatchedProject project, DateTime dueAt, string name, string? issueUrl, string? pnmtUrl)
    {
        var title = c.Title;
        var assignee = c.AssigneeName ?? "nobody";
        var link = issueUrl is null ? "" : $"\n\n{issueUrl}";
        var window = pnmtUrl is null ? "" : $"\nIn PNMT: {pnmtUrl.TrimEnd('/')}/cookie?case={c.Id}";
        var sign = $"\n\n— {name}, PNMT's assistant";
        var since = c.LastStaffUpdateAt ?? c.FirstAnswerAt ?? c.TicketCreatedAt ?? c.OpenedAt;
        return kind switch
        {
            CookieDutyKind.FirstAnswerReminder => new(
                $"{title}: waiting for a first answer",
                $"{title} has had no answer from staff yet. The first answer is due at {When(dueAt)}.",
                $"{title} has had no answer from staff since it came in ({When(c.TicketCreatedAt ?? c.OpenedAt)}). The first answer is due within " +
                $"{project.FirstAnswerHours:0.#} working hours, at {When(dueAt)}. Assigned to: {assignee}.{link}{window}{sign}",
                $"Reminder from {name}: this ticket has no first answer from staff yet. It is due at {When(dueAt)}."),
            CookieDutyKind.FirstAnswerMissed => new(
                $"{title}: the first answer is overdue",
                $"{title}: no first answer from staff within {project.FirstAnswerHours:0.#} working hours.",
                $"{title} came in at {When(c.TicketCreatedAt ?? c.OpenedAt)} and has had no answer from staff within {project.FirstAnswerHours:0.#} working " +
                $"hours (due at {When(dueAt)}). Assigned to: {assignee}.{link}{window}{sign}",
                $"{name}: the first answer was due at {When(dueAt)}."),
            CookieDutyKind.UpdateReminder => new(
                $"{title}: an update is due soon",
                $"{title}: the last update by staff was on {When(since)}. An update is due by {When(c.LastStaffUpdateAt?.AddDays(project.UpdateDays) ?? dueAt)}.",
                $"{title} was last updated by staff on {When(since)}. Every ticket gets an update at least every {project.UpdateDays} days. Assigned to: " +
                $"{assignee}. Status: {c.StatusName}.{link}{window}{sign}",
                $"Reminder from {name}: the last update by staff was on {When(since)}; an update is due within {project.UpdateDays} days of it."),
            _ => new(
                $"{title}: the update is overdue",
                $"{title}: no update by staff for {project.UpdateDays} days.",
                $"{title} has had no update by staff since {When(since)}, more than {project.UpdateDays} days. Assigned to: {assignee}. Status: " +
                $"{c.StatusName}.{link}{window}{sign}",
                $"{name}: no update by staff for {project.UpdateDays} days."),
        };
    }

    public static Text ProblemTicket(CookieCaseEntity c, IReadOnlyList<CaseMember> members, IReadOnlyList<CookieCaseEntryEntity> timeline, double hours,
        string name, string? pnmtUrl)
    {
        var days = hours >= 48 && hours % 24 == 0 ? $"{hours / 24:0} days" : $"{hours:0} hours";
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{name}, PNMT's assistant, opened this ticket: this problem has shown no progress for {days} (no acknowledgement, no note, no linked ticket that moved).");
        text.AppendLine();
        text.AppendLine("What is failing:");
        foreach (var m in members)
        {
            var detail = m.Type == CaseMember.Zabbix ? $"Zabbix, {m.Severity}" : m.Code is { } code ? code.ToString(CultureInfo.InvariantCulture) : "failing";
            text.AppendLine(CultureInfo.InvariantCulture,
                $"- {m.Name} ({detail}) since {When(m.Since)}{(string.IsNullOrWhiteSpace(m.Message) ? "" : ": " + m.Message)}");
        }
        text.AppendLine();
        text.AppendLine("Timeline:");
        foreach (var e in timeline)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {When(e.At)}: {e.Text}");
        }
        if (pnmtUrl is not null)
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"In PNMT: {pnmtUrl.TrimEnd('/')}{(c.HostId is { } h ? $"/hosts/{h}" : $"/cookie?case={c.Id}")}");
        }
        var subject = CaseBook.Cut($"{c.Title}: unresolved since {Clock.ToLocal(c.OpenedAt):d MMM}", 250)!;
        return new Text(subject, subject, text.ToString().TrimEnd(), text.ToString().TrimEnd());
    }

    public static string ClosingNote(CookieCaseEntity c, string name) =>
        $"{name}: resolved. Everything of this case has been fine again since {When(c.ClosedAt ?? Clock.UtcNow)}. Cookie doesn't close tickets; " +
        "close it when nothing is left to do.";
}
