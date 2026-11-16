using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Notifications;
using PNMTD.Redmine;

namespace PNMTD.Cookie;

/// <summary>
/// What is to go out: a message to a person (e-mail, Pushover), a note on a ticket, or a ticket. <see cref="Unlimited"/>: no limit
/// holds it back, for an <i>alarming</i> access notice (docs/cookie-access-plan.md §6.4).
/// </summary>
public sealed record OutboxRequest(
    CookieChannel Channel,
    string Subject,
    string Body,
    Guid? CaseId = null,
    long? DutyId = null,
    Guid? UserId = null,
    string Recipient = "",
    string? Address = null,
    int? IssueId = null,
    int? ProjectId = null,
    int? TrackerId = null,
    Urgency Urgency = Urgency.Important,
    string Source = "code",
    bool Unlimited = false,
    string? OrderedBy = null);

/// <summary>
/// Everything that leaves PNMT for Cookie goes through here (docs/cookie-plan.md §6.1, §19.1): the limits are counted
/// against what went out today, what is over a limit is held back and kept, and a person can send it later.
/// </summary>
public sealed class Outbox(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, NotificationService notifications, RedmineClient redmine,
    IServiceScopeFactory scopes, IConfiguration configuration, ILogger<Outbox> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The limit it would break, or null.</summary>
    public async Task<string?> LimitAsync(PnmtdDbContext db, OutboxRequest r, CancellationToken ct)
    {
        if (r.Unlimited)
        {
            return null;
        }
        var limits = (await settings.GetAsync<ActionSettings>(ct)).Limits;
        var today = Clock.ToUtc(Clock.LocalNow.Date);
        var hour = Clock.UtcNow.AddHours(-1);
        var sent = db.CookieOutbox.AsNoTracking().Where(o => o.CreatedAt >= today && (o.State == CookieOutboxState.Sent || o.State == CookieOutboxState.Simulated));
        var messages = sent.Where(o => o.Channel == CookieChannel.Email || o.Channel == CookieChannel.Pushover);
        var notes = sent.Where(o => o.Channel == CookieChannel.RedmineNote || o.Channel == CookieChannel.RedminePrivateNote);
        switch (r.Channel)
        {
            case CookieChannel.Pushover:
                if (await sent.CountAsync(o => o.Channel == CookieChannel.Pushover && o.UserId == r.UserId, ct) >= limits.PushoverPerPersonDay)
                {
                    return $"Pushover per person: {limits.PushoverPerPersonDay} a day";
                }
                if (await sent.CountAsync(o => o.Channel == CookieChannel.Pushover && o.UserId == r.UserId && o.CreatedAt >= hour, ct) >= limits.PushoverPerPersonHour)
                {
                    return $"Pushover per person: {limits.PushoverPerPersonHour} an hour";
                }
                break;
            case CookieChannel.Email:
                if (await sent.CountAsync(o => o.Channel == CookieChannel.Email && o.UserId == r.UserId, ct) >= limits.EmailPerPersonDay)
                {
                    return $"e-mail per person: {limits.EmailPerPersonDay} a day";
                }
                break;
            case CookieChannel.AccessEmail or CookieChannel.AccessPushover:
            {
                // The access watch's own limits (docs/cookie-access-plan.md §6.4): per contact and channel, a day and an hour.
                var access = (await settings.GetAsync<Access.AccessSettings>(ct)).Limits;
                var mine = sent.Where(o => o.Channel == r.Channel && o.UserId == r.UserId);
                if (await mine.CountAsync(ct) >= access.PerContactDay)
                {
                    return $"access notices per contact: {access.PerContactDay} a day";
                }
                if (await mine.CountAsync(o => o.CreatedAt >= hour, ct) >= access.PerContactHour)
                {
                    return $"access notices per contact: {access.PerContactHour} an hour";
                }
                break;
            }
            case CookieChannel.RedmineTicket:
                if (await sent.CountAsync(o => o.Channel == CookieChannel.RedmineTicket, ct) >= limits.TicketsPerDay)
                {
                    return $"new tickets: {limits.TicketsPerDay} a day";
                }
                break;
            case CookieChannel.RedmineNote or CookieChannel.RedminePrivateNote:
                if (await notes.CountAsync(o => o.IssueId == r.IssueId, ct) >= limits.NotesPerTicketDay)
                {
                    return $"notes on one ticket: {limits.NotesPerTicketDay} a day";
                }
                if (await notes.CountAsync(ct) >= limits.RedmineNotesPerDay)
                {
                    return $"notes on Redmine: {limits.RedmineNotesPerDay} a day";
                }
                // Cookie's public answers (§6.1); code's public notes are the closing notes on its own tickets.
                if (r.Channel == CookieChannel.RedmineNote && r.Source != "code"
                    && await sent.CountAsync(o => o.Channel == CookieChannel.RedmineNote && o.Source != "code", ct) >= limits.PublicAnswersPerDay)
                {
                    return $"public answers: {limits.PublicAnswersPerDay} a day";
                }
                break;
        }
        if (r.Channel is CookieChannel.Email or CookieChannel.Pushover && await messages.CountAsync(ct) >= limits.MessagesPerDay)
        {
            return $"Pushover and e-mail: {limits.MessagesPerDay} a day";
        }
        return null;
    }

    /// <summary>Sends it, or holds it back at a limit; the row says which, and for a ticket, its number.</summary>
    public async Task<CookieOutboxEntity> SendAsync(OutboxRequest r, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            var row = new CookieOutboxEntity
            {
                CreatedAt = Clock.UtcNow, CaseId = r.CaseId, DutyId = r.DutyId, Channel = r.Channel, UserId = r.UserId,
                Recipient = CaseBook.Cut(r.Recipient, 300)!, IssueId = r.IssueId, Subject = CaseBook.Cut(r.Subject, 300)!, Body = r.Body, Source = r.Source,
            };
            var limit = await LimitAsync(db, r, ct);
            if (limit is not null)
            {
                row.State = CookieOutboxState.HeldBack;
                row.Detail = $"Held back: {limit}.";
            }
            else
            {
                await DeliverAsync(db, row, r, ct);
            }
            db.CookieOutbox.Add(row);
            await db.SaveChangesAsync(ct);
            return row;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A person sends what a limit held back (§6.1): no limit applies, the audit names them.</summary>
    public async Task<CookieOutboxEntity> SendHeldBackAsync(long id, Guid? userId, string userName, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var row = await db.CookieOutbox.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw Api.ApiException.NotFound("Message");
        if (row.State != CookieOutboxState.HeldBack)
        {
            throw Api.ApiException.Conflict("cookie_not_held_back", "Only what a limit held back can be sent.");
        }
        var person = row.UserId is { } u ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == u, ct) : null;
        var address = row.Channel switch
        {
            CookieChannel.Email or CookieChannel.AccessEmail or CookieChannel.WasThisYou => person?.Email,
            CookieChannel.Pushover or CookieChannel.AccessPushover => person?.PushoverKey,
            _ => null,
        };
        var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
        var request = new OutboxRequest(row.Channel, row.Subject, row.Body, row.CaseId, row.DutyId, row.UserId, row.Recipient, address, row.IssueId,
            redmineSettings.TicketProjectId, redmineSettings.TicketTrackerId, Urgency.Important, row.Source);
        await DeliverAsync(db, row, request, ct, byPerson: true);
        row.SentById = userId;
        row.SentByName = CaseBook.Cut(userName, 200);
        await db.SaveChangesAsync(ct);
        AuditLog.Summary($"{Label(row.Channel)} \"{row.Subject}\", held back by a limit, sent: {row.Detail}");
        return row;
    }

    private async Task DeliverAsync(PnmtdDbContext db, CookieOutboxEntity row, OutboxRequest r, CancellationToken ct, bool byPerson = false)
    {
        try
        {
            switch (r.Channel)
            {
                case CookieChannel.Email or CookieChannel.Pushover or CookieChannel.AccessEmail or CookieChannel.AccessPushover or CookieChannel.WasThisYou:
                {
                    var pushover = r.Channel is CookieChannel.Pushover or CookieChannel.AccessPushover;
                    if (string.IsNullOrWhiteSpace(r.Address))
                    {
                        row.State = CookieOutboxState.Failed;
                        row.Detail = pushover ? "The person has no Pushover key." : "The person has no e-mail address.";
                        return;
                    }
                    var recipient = pushover ? $"pushover://{r.Address.Trim()}" : r.Address.Trim();
                    var importance = r.Urgency == Urgency.Urgent ? Importance.High : Importance.Normal;
                    var message = new NotificationMessage(recipient, r.Subject, CaseBook.Cut(r.Body, 900)!, r.Body, importance);
                    var simulate = configuration.GetValue("Development:DoNotSendNotifications", false);
                    var result = await notifications.SendAsync(message, $"Cookie: {r.Subject}", simulate, ct);
                    row.State = result is null || !result.Ok ? CookieOutboxState.Failed : simulate ? CookieOutboxState.Simulated : CookieOutboxState.Sent;
                    row.Detail = CaseBook.Cut(result?.Detail ?? "No provider takes this recipient.", 500);
                    break;
                }
                case CookieChannel.RedmineNote or CookieChannel.RedminePrivateNote:
                {
                    await AllowedAsync(r.IssueId!.Value, ct);
                    await redmine.WriteAsync(HttpMethod.Put, $"/issues/{r.IssueId}.json", new JsonObject
                    {
                        ["issue"] = new JsonObject { ["notes"] = r.Body, ["private_notes"] = r.Channel == CookieChannel.RedminePrivateNote },
                    }, ct);
                    row.State = CookieOutboxState.Sent;
                    row.Detail = $"Note on #{r.IssueId}.";
                    break;
                }
                case CookieChannel.RedmineTicket:
                {
                    var fields = new JsonObject { ["project_id"] = r.ProjectId, ["subject"] = r.Subject, ["description"] = r.Body };
                    if (r.TrackerId is { } tracker)
                    {
                        fields["tracker_id"] = tracker;
                    }
                    using var created = await redmine.WriteAsync(HttpMethod.Post, "/issues.json", new JsonObject { ["issue"] = fields }, ct);
                    var id = created?.RootElement.GetProperty("issue").GetProperty("id").GetInt32();
                    row.IssueId = id;
                    row.State = CookieOutboxState.Sent;
                    row.Detail = $"Ticket #{id}.";
                    break;
                }
            }
        }
        catch (RedmineException e)
        {
            row.State = CookieOutboxState.Failed;
            row.Detail = CaseBook.Cut(e.Message, 500);
            logger.LogWarning("Cookie's {Channel} failed: {Error}", r.Channel, e.Message);
        }
        if (row.State is CookieOutboxState.Sent or CookieOutboxState.Simulated)
        {
            row.SentAt = Clock.UtcNow;
            var type = r.Channel switch
            {
                CookieChannel.RedmineTicket => AuditEvents.CookieTicketCreated,
                CookieChannel.RedmineNote or CookieChannel.RedminePrivateNote => AuditEvents.CookieNoteAdded,
                _ => AuditEvents.CookieMessageSent,
            };
            if (!byPerson)
            {
                var caseTitle = r.CaseId is { } caseId ? await db.CookieCases.Where(c => c.Id == caseId).Select(c => c.Title).FirstOrDefaultAsync(ct) : null;
                var ordered = r.OrderedBy is null ? "" : $" (on {r.OrderedBy}'s order from the chat)";
                AuditLog.Record(db, type, AuditLog.Cut($"{Label(r.Channel)} to {Who(r)}: {r.Subject}{ordered}", AuditLog.MaxDetail), AuditWho.Cookie(r.Source),
                    r.CaseId is { } id ? AuditTarget.Of(AuditTargets.CookieCase, id, caseTitle) : null);
            }
        }
    }

    /// <summary>Only tickets of the projects PNMT shows (audit plan §6): an id from elsewhere is refused before anything is written.</summary>
    private async Task AllowedAsync(int issueId, CancellationToken ct)
    {
        var issue = (await redmine.GetAsync<RmIssueEnvelope>($"/issues/{issueId}.json", ct)).Issue;
        var allowed = await AllowedProjectsAsync(ct);
        if (!allowed.Contains(issue.Project.Id))
        {
            throw new RedmineException(RedmineFailure.Forbidden, $"#{issueId} is in a project PNMT doesn't show.");
        }
    }

    private async Task<IReadOnlySet<int>> AllowedProjectsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<RedmineService>().LookupsAsync(ct)).Allowed;
    }

    private static string Who(OutboxRequest r) => r.Channel switch
    {
        CookieChannel.RedmineTicket => $"project {r.ProjectId}",
        CookieChannel.RedmineNote or CookieChannel.RedminePrivateNote => $"#{r.IssueId}",
        _ => r.Recipient,
    };

    public static string Label(CookieChannel channel) => channel switch
    {
        CookieChannel.Email => "E-mail",
        CookieChannel.Pushover => "Pushover",
        CookieChannel.RedmineNote => "Note",
        CookieChannel.RedminePrivateNote => "Private note",
        CookieChannel.RedmineTicket => "Ticket",
        CookieChannel.AccessEmail => "Access e-mail",
        CookieChannel.AccessPushover => "Access Pushover",
        CookieChannel.WasThisYou => "Was this you?",
        _ => channel.ToString(),
    };
}
