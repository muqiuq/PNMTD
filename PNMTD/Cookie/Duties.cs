using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Cookie;

/// <summary>
/// Who is told what (docs/cookie-plan.md §8.3): the assignee by their Redmine user, the project's escalation group, and whoever
/// the availability plan makes responsible. People who don't work that day are skipped; the channel follows the urgency.
/// </summary>
public static class Recipients
{
    /// <summary>The active PNMT user whose Redmine user is <paramref name="assigneeName"/>.</summary>
    public static async Task<UserEntity?> AssigneeAsync(PnmtdDbContext db, string? assigneeName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assigneeName))
        {
            return null;
        }
        var name = assigneeName.Trim().ToLowerInvariant();
        return await db.Users.AsNoTracking().Where(u => u.IsActive && u.RedmineName != null && u.RedmineName.ToLower() == name).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The people a ticket's duty names (§8.3): a reminder goes to the assignee, or to the escalation group when nobody is
    /// assigned or the assignee doesn't work today; a missed deadline to the assignee, the group and whoever is responsible.
    /// Without an escalation group, or when none of its members works, whoever is responsible stands in for it.
    /// </summary>
    public static IReadOnlyCollection<Guid> ForTicket(Guid? assignee, IReadOnlyList<Guid> group, bool hasGroup, bool missed, PNMTD.Availability.PlanSnapshot snapshot,
        DateOnly today, DateTime now)
    {
        var named = new HashSet<Guid>();
        var assigneeWorks = assignee is { } a && snapshot.Works(a, today);
        if (assigneeWorks)
        {
            named.Add(assignee!.Value);
        }
        var responsible = snapshot.ResponsibleAt(now).UserIds;
        if (!assigneeWorks || missed)
        {
            var working = hasGroup ? group.Where(u => snapshot.Works(u, today)).ToList() : [];
            named.UnionWith(working.Count > 0 ? working : responsible);
        }
        if (missed)
        {
            named.UnionWith(responsible);
        }
        return named;
    }
}

/// <summary>
/// Turns the duties that are due into notes, tickets and messages, from template texts (docs/cookie-plan.md §3.4 A, §7,
/// §19.1). A duty that is off is closed as off, so switching it on later sends nothing old; a reminder or an escalation
/// outside working hours waits for the next working period; a reminder whose deadline is due as well gives way to it.
/// </summary>
public sealed class DutyRunner(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, Outbox outbox, IOptionsMonitor<RedmineOptions> redmineOptions,
    IConfiguration configuration, PNMTD.Availability.AvailabilityPlan plan, DutyWriter writer, CookieIntegrations integrations, ILogger<DutyRunner> logger)
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
        var calendar = await plan.CalendarAsync(ct);
        var working = calendar.IsWorking(now);
        var reading = await integrations.ReadAsync(ct);
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
            if (c.StoppedAt is not null)
            {
                Close(duty, CookieDutyState.Stopped, $"Stopped with its case by {c.StoppedByName}.", now);
                done++;
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
            // To people, so only in working hours (§8.3): the ticket duties, and the e-mails that stand in for a problem's ticket and its
            // closing note (docs/cookie-fallback-plan.md §4.5).
            var toPeople = duty.Kind is CookieDutyKind.FirstAnswerReminder or CookieDutyKind.FirstAnswerMissed or CookieDutyKind.UpdateReminder
                or CookieDutyKind.UpdateMissed
                || duty.Kind == CookieDutyKind.ProblemTicket && !reading.CanTicket
                || duty.Kind == CookieDutyKind.ClosingNote && c.OwnIssueId is null;
            if (toPeople && !working)
            {
                continue;
            }
            try
            {
                var (state, outcome) = duty.Kind switch
                {
                    CookieDutyKind.ProblemTicket => await TicketAsync(db, c, duty, reading, ct),
                    CookieDutyKind.ClosingNote => await ClosingNoteAsync(db, c, duty, reading, ct),
                    _ => await TicketDutyAsync(db, c, duty, reading, ct),
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

    private async Task<(CookieDutyState, string)> TicketDutyAsync(PnmtdDbContext db, CookieCaseEntity c, CookieDutyEntity duty, IntegrationsReading reading,
        CancellationToken ct)
    {
        var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var project = c.ProjectId is { } p ? redmineSettings.Of(p) : null;
        if (project is null || c.IssueId is not { } issueId)
        {
            return (CookieDutyState.NotPossible, "The ticket's project is no longer watched.");
        }
        var missed = duty.Kind is CookieDutyKind.FirstAnswerMissed or CookieDutyKind.UpdateMissed;
        var urgency = CaseRules.UrgencyOf(duty.Kind);
        var template = DutyTexts.Ticket(duty.Kind, c, project, duty.DueAt, identity.Name, IssueUrl(issueId), configuration["PublicBaseUrl"]);
        // Cookie writes the text while it runs and Admin → Cookie → Actions says so (§21.1); the template otherwise.
        var text = await writer.WriteAsync(c, duty, template, ct);
        var rows = new List<CookieOutboxEntity>();
        var notes = new List<string>();

        if (!missed)
        {
            if (reading.RedmineWritable)
            {
                rows.Add(await outbox.SendAsync(new OutboxRequest(CookieChannel.RedminePrivateNote, text.Subject, text.Note, c.Id, duty.Id, IssueId: issueId,
                    Recipient: $"#{issueId}", Urgency: urgency), ct));
            }
            else
            {
                // Not attempted while Redmine can't be written (docs/cookie-fallback-plan.md §4.5); the e-mail below goes out regardless.
                notes.Add(reading.Redmine == RedmineAvailability.ReadOnly ? "no note on the ticket: Redmine__WriteApiKey is not set"
                    : $"no note on the ticket: {reading.RedmineReason}");
            }
        }

        // Who the duty names (§8.3): the assignee, the escalation group, whoever the plan makes responsible.
        var assignee = await Recipients.AssigneeAsync(db, c.AssigneeName, ct);
        var group = project.EscalationGroupId is { } g ? (await MembersAsync(db, [g], ct)).GetValueOrDefault(g) ?? [] : [];
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var snapshot = await plan.DayAsync(today, ct, [.. group, .. assignee is null ? Array.Empty<Guid>() : [assignee.Id]]);
        var named = Recipients.ForTicket(assignee?.Id, group, project.EscalationGroupId is not null, missed, snapshot, today, Clock.UtcNow);
        if (assignee is not null && !named.Contains(assignee.Id))
        {
            notes.Add($"{assignee.DisplayName} doesn't work today");
        }
        var users = await db.Users.AsNoTracking().Where(u => named.Contains(u.Id) && u.IsActive).OrderBy(u => u.DisplayName).ToListAsync(ct);
        foreach (var user in users)
        {
            // The channel follows the urgency (§8.3): e-mail, and Pushover too for what is urgent.
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                rows.Add(await outbox.SendAsync(new OutboxRequest(CookieChannel.Email, text.Subject, text.Message, c.Id, duty.Id, user.Id, user.DisplayName,
                    user.Email, issueId, Urgency: urgency), ct));
            }
            if (urgency == Urgency.Urgent && !string.IsNullOrWhiteSpace(user.PushoverKey))
            {
                rows.Add(await outbox.SendAsync(new OutboxRequest(CookieChannel.Pushover, text.Subject, text.Short, c.Id, duty.Id, user.Id, user.DisplayName,
                    user.PushoverKey, issueId, Urgency: urgency), ct));
            }
            if (string.IsNullOrWhiteSpace(user.Email) && (urgency != Urgency.Urgent || string.IsNullOrWhiteSpace(user.PushoverKey)))
            {
                notes.Add($"{user.DisplayName} has no e-mail address");
            }
        }
        if (users.Count == 0)
        {
            notes.Add(assignee is null && c.AssigneeName is { Length: > 0 }
                ? $"nobody to tell: no PNMT user has the Redmine user {c.AssigneeName}, and nobody stands in"
                : "nobody to tell: nobody works who could stand in (the escalation group, the availability plan)");
        }
        return Summarise(rows, notes);
    }

    private static async Task<Dictionary<Guid, List<Guid>>> MembersAsync(PnmtdDbContext db, List<Guid> groups, CancellationToken ct) =>
        (await db.UserGroups.AsNoTracking().Where(m => groups.Contains(m.GroupId)).Select(m => new { m.GroupId, m.UserId }).ToListAsync(ct))
        .GroupBy(m => m.GroupId).ToDictionary(g => g.Key, g => g.Select(m => m.UserId).ToList());

    // --- A problem's ticket and its closing note (§7.1) -----------------------------------------------------------------

    private async Task<(CookieDutyState, string)> TicketAsync(PnmtdDbContext db, CookieCaseEntity c, CookieDutyEntity duty, IntegrationsReading reading,
        CancellationToken ct)
    {
        var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
        if (!reading.CanTicket || redmineSettings.TicketProjectId is not { } projectId)
        {
            // No ticket system to open one in: whoever is responsible is told instead (docs/cookie-fallback-plan.md §4.5).
            return await ProblemByEmailAsync(db, c, duty, reading, ct);
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

    private async Task<(CookieDutyState, string)> ClosingNoteAsync(PnmtdDbContext db, CookieCaseEntity c, CookieDutyEntity duty, IntegrationsReading reading,
        CancellationToken ct)
    {
        if (c.OwnIssueId is not { } issueId)
        {
            return await ClosingEmailAsync(db, c, duty, ct);
        }
        if (await db.RedmineIssues.AsNoTracking().AnyAsync(i => i.IssueId == issueId && i.IsClosed, ct))
        {
            return (CookieDutyState.Off, $"#{issueId} is closed already.");
        }
        if (!reading.RedmineWritable)
        {
            return (CookieDutyState.NotPossible, $"Not possible: {reading.NoNoteReason}");
        }
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var note = DutyTexts.ClosingNote(c, identity.Name);
        var row = await outbox.SendAsync(new OutboxRequest(CookieChannel.RedmineNote, $"#{issueId} resolved", note, c.Id, duty.Id, IssueId: issueId,
            Recipient: $"#{issueId}", Urgency: Urgency.Info), ct);
        return Summarise([row], []);
    }

    /// <summary>
    /// The problem ticket's stand-in (docs/cookie-fallback-plan.md §4.5): the ticket's content as an e-mail to whoever is responsible
    /// now, and Pushover too for a case of importance High. Nobody comes from an escalation group: that is a ticket project's.
    /// </summary>
    private async Task<(CookieDutyState, string)> ProblemByEmailAsync(PnmtdDbContext db, CookieCaseEntity c, CookieDutyEntity duty, IntegrationsReading reading,
        CancellationToken ct)
    {
        var why = reading.NoTicketShort;
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var snapshot = await plan.DayAsync(today, ct);
        var named = snapshot.ResponsibleAt(Clock.UtcNow).UserIds;
        var users = await db.Users.AsNoTracking().Where(u => named.Contains(u.Id) && u.IsActive).OrderBy(u => u.DisplayName).ToListAsync(ct);
        if (users.Count == 0)
        {
            return (CookieDutyState.NotPossible, $"Not possible: no ticket ({why}), and nobody is responsible now (the availability plan), so nobody to tell.");
        }
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var caseSettings = await settings.GetAsync<CaseSettings>(ct);
        var members = JsonSerializer.Deserialize<List<CaseMember>>(c.Members, Json) ?? [];
        var timeline = await db.CookieCaseEntries.AsNoTracking().Where(e => e.CaseId == c.Id).OrderBy(e => e.At).Take(30).ToListAsync(ct);
        var text = DutyTexts.ProblemEmail(c, members, timeline, caseSettings.UnresolvedHours, identity.Name, configuration["PublicBaseUrl"], why);
        // Urgent as the prompts define it (docs/cookie-prompts.md §7.1): importance High.
        var urgent = c.Importance == Importance.High;
        var rows = new List<CookieOutboxEntity>();
        var notes = new List<string>();
        foreach (var user in users)
        {
            if (!string.IsNullOrWhiteSpace(user.Email))
            {
                rows.Add(await outbox.SendAsync(new OutboxRequest(CookieChannel.Email, text.Subject, text.Message, c.Id, duty.Id, user.Id, user.DisplayName,
                    user.Email, Urgency: Urgency.Important), ct));
            }
            if (urgent && !string.IsNullOrWhiteSpace(user.PushoverKey))
            {
                rows.Add(await outbox.SendAsync(new OutboxRequest(CookieChannel.Pushover, text.Subject, text.Short, c.Id, duty.Id, user.Id, user.DisplayName,
                    user.PushoverKey, Urgency: Urgency.Urgent), ct));
            }
            if (string.IsNullOrWhiteSpace(user.Email) && !(urgent && !string.IsNullOrWhiteSpace(user.PushoverKey)))
            {
                notes.Add($"{user.DisplayName} has no e-mail address");
            }
        }
        if (rows.Count == 0)
        {
            return (CookieDutyState.NotPossible, $"Not possible: no ticket ({why}), and nobody to tell: {string.Join("; ", notes)}.");
        }
        var (state, outcome) = Summarise(rows, notes);
        return (state, $"No ticket ({why}): {outcome}");
    }

    /// <summary>Told by e-mail instead of a ticket (docs/cookie-fallback-plan.md §4.5): the same people hear that it is resolved, in two lines.</summary>
    private async Task<(CookieDutyState, string)> ClosingEmailAsync(PnmtdDbContext db, CookieCaseEntity c, CookieDutyEntity duty, CancellationToken ct)
    {
        var told = await db.CookieOutbox.AsNoTracking()
            .Where(o => o.CaseId == c.Id && o.Channel == CookieChannel.Email && o.Source == "code" && o.UserId != null
                && (o.State == CookieOutboxState.Sent || o.State == CookieOutboxState.Simulated)
                && db.CookieDuties.Any(d => d.Id == o.DutyId && d.Kind == CookieDutyKind.ProblemTicket))
            .Select(o => o.UserId!.Value).Distinct().ToListAsync(ct);
        if (told.Count == 0)
        {
            return (CookieDutyState.Off, "The case has no ticket of Cookie's, and nobody was told by e-mail.");
        }
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var subject = CaseBook.Cut($"{c.Title}: resolved", 250)!;
        var body = DutyTexts.ClosingEmail(c, identity.Name);
        var users = await db.Users.AsNoTracking().Where(u => told.Contains(u.Id) && u.IsActive && u.Email != null && u.Email != "").OrderBy(u => u.DisplayName)
            .ToListAsync(ct);
        var rows = new List<CookieOutboxEntity>();
        foreach (var user in users)
        {
            rows.Add(await outbox.SendAsync(new OutboxRequest(CookieChannel.Email, subject, body, c.Id, duty.Id, user.Id, user.DisplayName, user.Email,
                Urgency: Urgency.Info), ct));
        }
        var (state, outcome) = Summarise(rows, []);
        return (state, $"Resolved, told by e-mail: {outcome}");
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
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{name}, PNMT's assistant, opened this ticket: this problem has shown no progress for {Days(hours)} (no acknowledgement, no note, no linked ticket that moved).");
        text.AppendLine();
        ProblemBody(text, c, members, timeline, pnmtUrl);
        var subject = CaseBook.Cut($"{c.Title}: unresolved since {Clock.ToLocal(c.OpenedAt):d MMM}", 250)!;
        return new Text(subject, subject, text.ToString().TrimEnd(), text.ToString().TrimEnd());
    }

    /// <summary>
    /// The ticket's stand-in without a ticket system (docs/cookie-fallback-plan.md §4.5): the same content as an e-mail to whoever is
    /// responsible, and a Pushover line for what is urgent.
    /// </summary>
    public static Text ProblemEmail(CookieCaseEntity c, IReadOnlyList<CaseMember> members, IReadOnlyList<CookieCaseEntryEntity> timeline, double hours,
        string name, string? pnmtUrl, string why)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"This problem has shown no progress for {Days(hours)} (no acknowledgement, no note, no linked ticket that moved). There is no ticket to open " +
            $"for it ({why}), so it comes to you, who are responsible today.");
        text.AppendLine();
        ProblemBody(text, c, members, timeline, pnmtUrl);
        text.Append(CultureInfo.InvariantCulture, $"\n\n— {name}, PNMT's assistant");
        var since = Clock.ToLocal(c.OpenedAt).ToString("d MMM", CultureInfo.InvariantCulture);
        var body = text.ToString().TrimEnd();
        return new Text(CaseBook.Cut($"{c.Title}: unresolved since {since}, nobody took care", 250)!,
            CaseBook.Cut($"{c.Title}: unresolved since {since} and nobody took care. No ticket system now, so this reaches you.", 200)!, body, body);
    }

    public static string ClosingEmail(CookieCaseEntity c, string name) =>
        $"Resolved: everything of this case has been fine again since {When(c.ClosedAt ?? Clock.UtcNow)}. Nothing is left to do; this closes the e-mail " +
        $"about it.\n\n— {name}, PNMT's assistant";

    private static string Days(double hours) => hours >= 48 && hours % 24 == 0 ? $"{hours / 24:0} days" : $"{hours:0} hours";

    /// <summary>What is failing, the timeline and the link, shared by the ticket and its stand-in.</summary>
    private static void ProblemBody(StringBuilder text, CookieCaseEntity c, IReadOnlyList<CaseMember> members, IReadOnlyList<CookieCaseEntryEntity> timeline, string? pnmtUrl)
    {
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
    }

    public static string ClosingNote(CookieCaseEntity c, string name) =>
        $"{name}: resolved. Everything of this case has been fine again since {When(c.ClosedAt ?? Clock.UtcNow)}. Cookie doesn't close tickets; " +
        "close it when nothing is left to do.";
}
