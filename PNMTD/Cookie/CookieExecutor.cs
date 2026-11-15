using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Decisions;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Redmine;

namespace PNMTD.Cookie;

/// <summary>A proposal to take through the executor: from a round (with Cookie's rights and the round's masking), or from a mail.</summary>
public sealed record ActionRequest(
    CookieProposal Proposal,
    CookieCaseEntity? Case,
    long? RoundId,
    UserRights Rights,
    Pseudonyms Privacy,
    IReadOnlyCollection<string> Areas,
    Func<string, CancellationToken, Task<ProposedAction?>>? Retry = null);

/// <summary>
/// Cookie's executor (docs/cookie-plan.md §6, §21.1): every proposal goes through the action's level, its confidence
/// threshold, its targets and rights, mute and suppress and the case's stop, duplicates, Seraph for what leaves PNMT or
/// reaches people, and then the inbox (<i>Propose</i>) or the outbox (<i>Auto</i>). Each step is in Cookie's decision log.
/// Whatever a model said, code decides here.
/// </summary>
public sealed partial class CookieExecutor(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, Seraph seraph, Instructions instructions, Outbox outbox,
    DecisionBook inbox, PrivacyFilter privacy, IOptionsMonitor<RedmineOptions> redmineOptions, PNMTD.Availability.AvailabilityPlan plan, IServiceProvider services,
    ILogger<CookieExecutor> logger)
{
    public const string InboxKind = "cookie.action";

    /// <summary>The areas a Cookie proposal in the inbox may carry for everyone with <i>Decisions: Decide</i>; others are admins' (§9.2).</summary>
    private static readonly HashSet<string> InboxAreas = ["Monitoring", "Mail", "Cookie"];

    /// <summary>A line Cookie added to a host's or sensor's notes: <c>[Cookie, 2026-11-15 10:30] …</c>. It isn't a person's progress (§7.1).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\[[^\]\n]{1,60}, \d{4}-\d{2}-\d{2} \d{2}:\d{2}\] ")]
    public static partial System.Text.RegularExpressions.Regex NoteLine();

    private static readonly JsonSerializerOptions Json = AiJson.Options;

    /// <summary>What Seraph reviews (§5): everything that leaves PNMT or reaches people, and a mail rule's event.</summary>
    public static bool Reviewed(CookieActionKind kind) => kind is not (CookieActionKind.ObjectNote or CookieActionKind.HandToPerson);

    public async Task<CookieDecisionEntity> HandleAsync(ActionRequest r, CancellationToken ct)
    {
        var a = r.Proposal.Action with
        {
            Subject = CaseBook.Cut(r.Proposal.Action.Subject?.Trim(), 120) ?? "",
            Text = CaseBook.Cut(r.Proposal.Action.Text?.Trim(), 1500) ?? "",
            Reason = CaseBook.Cut(r.Proposal.Action.Reason?.Trim(), 300) ?? "",
            Evidence = r.Proposal.Action.Evidence ?? [],
        };
        var proposal = r.Proposal with { Action = a };
        await using var db = await dbs.CreateDbContextAsync(ct);
        var log = new CookieDecisionEntity
        {
            At = Clock.UtcNow, CaseId = r.Case?.Id, RoundId = r.RoundId, Kind = ProposedAction.Name(a.Action),
            Title = CaseBook.Cut($"{Label(a.Action)}: {r.Privacy.Unmask(a.Subject)}", 300)!, Proposal = JsonSerializer.Serialize(proposal, Json),
            Confidence = Math.Clamp(a.Confidence, 0, 1), Areas = [.. r.Areas.Distinct().Order()], Outcome = "noted",
        };
        db.CookieDecisions.Add(log);
        var actions = await settings.GetAsync<ActionSettings>(ct);
        var rule = actions.Levels.Of(a.Action);

        string? Done(string outcome, string? detail)
        {
            log.Outcome = outcome;
            log.Detail = CaseBook.Cut(detail, 1000);
            return null;
        }

        if (a.Action != CookieActionKind.HandToPerson && rule.Level == ActionLevel.Off)
        {
            Done("off", $"{Label(a.Action)} is off on Admin → Cookie → Actions.");
            await db.SaveChangesAsync(ct);
            return log;
        }
        var (refusal, target) = await CheckAsync(db, proposal, r.Case, r.Rights, ct);
        log.Target = target;
        if (refusal is not null)
        {
            Done("refused", refusal);
            await db.SaveChangesAsync(ct);
            return log;
        }
        var members = r.Case is null ? [] : JsonSerializer.Deserialize<List<CaseMember>>(r.Case.Members, CookieSettings.Json) ?? [];
        if (r.Case?.StoppedAt is not null)
        {
            Done("muted", $"The case is stopped by {r.Case.StoppedByName}.");
            await db.SaveChangesAsync(ct);
            return log;
        }
        var active = await instructions.ActiveAsync(ct);
        if (Instructions.Blocks(active, a.Action, r.Case, members, Guid.TryParse(a.ObjectId, out var obj) ? obj : null) is { } blocked)
        {
            Done("muted", $"{blocked.Kind} {blocked.Scope} {blocked.Label ?? blocked.Value}, by {blocked.CreatedByName}");
            await db.SaveChangesAsync(ct);
            return log;
        }
        var since = Clock.UtcNow.AddDays(-1);
        if (target is not null && await db.CookieDecisions.AnyAsync(d => d.CaseId == log.CaseId && d.Kind == log.Kind && d.Target == target
                && (d.Outcome == "proposed" || d.Outcome == "running" || d.At >= since && (d.Outcome == "done" || d.Outcome == "approved")), ct))
        {
            Done("duplicate", "The same action on the same target was done within a day, or waits for a person.");
            await db.SaveChangesAsync(ct);
            return log;
        }

        // Seraph (§5): approve, revise (one retry), reject, or a person. Without Seraph, a person.
        var toPerson = a.Action == CookieActionKind.HandToPerson ? "Cookie hands it to a person."
            : a.Confidence < rule.MinConfidence ? $"Below the threshold ({a.Confidence:0.00} < {rule.MinConfidence:0.00})." : null;
        // Outside working hours Cookie reaches nobody by itself (§8.3): a message waits for a person's decision.
        if (a.Action is CookieActionKind.Pushover or CookieActionKind.Email && !(await plan.CalendarAsync(ct)).IsWorking(Clock.UtcNow))
        {
            toPerson ??= "Outside working hours: a person decides whether it goes out now.";
        }
        if (Reviewed(a.Action))
        {
            var evidence = await EvidenceAsync(db, proposal, r.Case, members, r.Privacy, ct);
            var (verdict, model, error) = await seraph.ReviewAsync(Masked(proposal, r.Privacy), evidence, r.Privacy.Mask(Instructions.PromptText(active)), ct);
            log.Model = model;
            if (verdict is null)
            {
                toPerson ??= $"Seraph couldn't review it: {error}";
            }
            else
            {
                log.Verdict = verdict.Verdict.ToString().ToLowerInvariant();
                log.VerdictReason = verdict.Reason;
                switch (verdict.Verdict)
                {
                    case SeraphVerdictKind.Reject:
                        Done("rejected", $"Seraph: {verdict.Reason}");
                        await db.SaveChangesAsync(ct);
                        return log;
                    case SeraphVerdictKind.Revise when r.Retry is not null:
                    {
                        ProposedAction? again;
                        try
                        {
                            again = await r.Retry(verdict.Reason, ct);
                        }
                        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            toPerson ??= $"Seraph: {verdict.Reason}; Cookie's retry failed: {e.Message}";
                            break;
                        }
                        Done("revised", $"Seraph sent it back: {verdict.Reason}");
                        await db.SaveChangesAsync(ct);
                        if (again is null)
                        {
                            return log;
                        }
                        // The retry is the last: a second "revise" goes to a person.
                        return await HandleAsync(r with { Proposal = proposal with { Action = again }, Retry = null }, ct);
                    }
                    case SeraphVerdictKind.Revise or SeraphVerdictKind.Ask:
                        toPerson ??= $"Seraph: {verdict.Reason}";
                        break;
                }
            }
        }
        if (toPerson is not null || rule.Level == ActionLevel.Propose)
        {
            await db.SaveChangesAsync(ct);
            log.InboxDecisionId = await inbox.RaiseAsync(new DecisionDraft(InboxKind, CaseBook.Cut($"Cookie proposes: {log.Title}", 300)!,
                Summary(proposal, r.Privacy, log.VerdictReason, toPerson), new CookieActionPayload(log.Id), $"{InboxKind}:{log.Id}", r.Case?.Id,
                "cookie-case", r.Case?.Id, AdminOnly: log.Areas.Any(x => !InboxAreas.Contains(x)),
                Priority: a.Urgency == Urgency.Urgent ? 2 : a.Urgency == Urgency.Important ? 1 : 0), ct);
            Done("proposed", toPerson ?? "Propose: a person decides in the decisions inbox.");
            await db.SaveChangesAsync(ct);
            return log;
        }
        await RunAsync(db, log, proposal, r.Case, "cookie", ct);
        await db.SaveChangesAsync(ct);
        return log;
    }

    /// <summary>A person approved a proposal in the inbox: it runs now, through the limits.</summary>
    public async Task<CookieDecisionEntity> RunApprovedAsync(long id, string by, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        // Claimed once: a second click finds it running or done.
        if (await db.CookieDecisions.Where(d => d.Id == id && d.Outcome == "proposed").ExecuteUpdateAsync(u => u.SetProperty(d => d.Outcome, "running"), ct) == 0)
        {
            throw Api.ApiException.Conflict("cookie_not_proposed", "Only a proposal that waits can be run.");
        }
        var log = await db.CookieDecisions.SingleAsync(d => d.Id == id, ct);
        var proposal = JsonSerializer.Deserialize<CookieProposal>(log.Proposal, Json)!;
        var c = log.CaseId is { } caseId ? await db.CookieCases.SingleOrDefaultAsync(x => x.Id == caseId, ct) : null;
        await privacy.EnsureLoadedAsync(ct);
        if (proposal.Action.Action == CookieActionKind.HandToPerson)
        {
            log.Outcome = "approved";
            log.Detail = $"Noted by {by}.";
            await db.SaveChangesAsync(ct);
            return log;
        }
        // Checked again (§21.1): what was true when Cookie proposed it may not be any more.
        var rights = await services.GetRequiredService<CookieRound>().RightsAsync(ct) ?? UserRights.None;
        var level = (await settings.GetAsync<ActionSettings>(ct)).Levels.Of(proposal.Action.Action).Level;
        var (refusal, _) = await CheckAsync(db, proposal, c, rights, ct);
        var members = c is null ? [] : JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? [];
        var blocked = Instructions.Blocks(await instructions.ActiveAsync(ct), proposal.Action.Action, c, members,
            Guid.TryParse(proposal.Action.ObjectId, out var obj) ? obj : null);
        var why = level == ActionLevel.Off ? $"{Label(proposal.Action.Action)} is off now." : refusal
            ?? (c?.StoppedAt is not null ? $"The case was stopped by {c.StoppedByName}." : null)
            ?? (blocked is not null ? $"{blocked.Kind} {blocked.Scope} {blocked.Label ?? blocked.Value}, by {blocked.CreatedByName}." : null);
        if (why is not null)
        {
            log.Outcome = "refused";
            log.Detail = CaseBook.Cut($"Approved by {by}, but not any more: {why}", 1000);
            await db.SaveChangesAsync(ct);
            return log;
        }
        await RunAsync(db, log, proposal, c, "cookie", ct);
        log.Detail = CaseBook.Cut($"Approved by {by}. {log.Detail}", 1000);
        await db.SaveChangesAsync(ct);
        return log;
    }

    /// <summary>A person declined it: it doesn't happen, and the comment stays with it.</summary>
    public async Task DeclineAsync(long id, string by, string? comment, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var log = await db.CookieDecisions.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw Api.ApiException.NotFound("Cookie's proposal");
        log.Outcome = "declined";
        log.Detail = CaseBook.Cut($"Declined by {by}{(string.IsNullOrWhiteSpace(comment) ? "." : $": {comment.Trim()}")}", 1000);
        await db.SaveChangesAsync(ct);
    }

    public static string Label(CookieActionKind kind) => kind switch
    {
        CookieActionKind.Pushover => "Pushover",
        CookieActionKind.Email => "E-mail",
        CookieActionKind.Ticket => "Ticket",
        CookieActionKind.PrivateNote => "Private note",
        CookieActionKind.PublicAnswer => "Public answer",
        CookieActionKind.ObjectNote => "Note",
        CookieActionKind.MailEvent => "Mail rule event",
        _ => "For a person",
    };

    /// <summary>The proposal as Seraph reads it: every text part masked, whoever built it (§13.2).</summary>
    private static CookieProposal Masked(CookieProposal p, Pseudonyms privacy) => p with
    {
        Action = p.Action with { Subject = privacy.Mask(p.Action.Subject), Text = privacy.Mask(p.Action.Text), Reason = privacy.Mask(p.Action.Reason) },
        Mail = p.Mail is null ? null : p.Mail with { Message = privacy.Mask(p.Mail.Message) },
    };

    private string Summary(CookieProposal p, Pseudonyms privacy, string? verdict, string? toPerson)
    {
        var text = new StringBuilder();
        text.AppendLine(privacy.Unmask(p.Action.Text));
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Why: {privacy.Unmask(p.Action.Reason)} (confidence {p.Action.Confidence:0.00})");
        if (verdict is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Seraph: {privacy.Unmask(verdict)}");
        }
        if (toPerson is not null)
        {
            text.AppendLine(privacy.Unmask(toPerson));
        }
        return text.ToString().TrimEnd();
    }

    // --- Targets and rights ------------------------------------------------------------------------------------------------

    /// <summary>The refusal, if any, and the target's key for duplicates. Every target is an id from PNMT's data (§6).</summary>
    private async Task<(string? Refusal, string? Target)> CheckAsync(PnmtdDbContext db, CookieProposal p, CookieCaseEntity? c, UserRights rights,
        CancellationToken ct)
    {
        var a = p.Action;
        var redmine = redmineOptions.CurrentValue;
        switch (a.Action)
        {
            case CookieActionKind.Pushover or CookieActionKind.Email:
            {
                if (!Guid.TryParse(a.UserId, out var userId))
                {
                    return ("A person must be named by their user id.", null);
                }
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
                if (user is null)
                {
                    return ("No active PNMT user with that id.", null);
                }
                var address = a.Action == CookieActionKind.Pushover ? user.PushoverKey : user.Email;
                if (string.IsNullOrWhiteSpace(address))
                {
                    return ($"{user.DisplayName} has no {(a.Action == CookieActionKind.Pushover ? "Pushover key" : "e-mail address")}.", null);
                }
                // §6: Pushover only for what is urgent, e-mail for what is important or urgent; and only to someone who works today (§8.2).
                if (a.Action == CookieActionKind.Pushover && a.Urgency != Urgency.Urgent || a.Action == CookieActionKind.Email && a.Urgency == Urgency.Info)
                {
                    return (a.Action == CookieActionKind.Pushover ? "Pushover is only for what is urgent." : "E-mail is for what is important or urgent.", null);
                }
                var today = DateOnly.FromDateTime(Clock.LocalNow);
                var day = await plan.DayAsync(today, ct, [userId]);
                if (!day.Closed(today) && !day.Works(userId, today))
                {
                    return ($"{user.DisplayName} doesn't work today (the availability plan).", null);
                }
                return (null, $"user:{userId}");
            }
            case CookieActionKind.Ticket:
            {
                var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
                if (!redmine.CanWrite || redmineSettings.TicketProjectId is null)
                {
                    return ("A ticket needs Redmine__WriteApiKey and a project for Cookie's tickets.", null);
                }
                if (!rights.Has(Area.Redmine, Level.Link))
                {
                    return ("Cookie's group needs Redmine: Link for a ticket.", null);
                }
                if (c is null || c.Kind != CookieCaseKind.Problem)
                {
                    return ("A ticket is for a problem's case.", null);
                }
                if (c.OwnIssueId is { } own && !await db.RedmineIssues.AnyAsync(i => i.IssueId == own && i.IsClosed, ct))
                {
                    return ($"The case has its ticket already: #{own}.", null);
                }
                return (null, $"case:{c.Id}");
            }
            case CookieActionKind.PrivateNote or CookieActionKind.PublicAnswer:
            {
                if (!redmine.CanWrite)
                {
                    return ("A note on a ticket needs Redmine__WriteApiKey.", null);
                }
                if (!rights.Has(Area.Redmine, Level.View))
                {
                    return ("Cookie's group needs Redmine: View for a note on a ticket.", null);
                }
                var linked = c is not null && (a.IssueId == c.IssueId || a.IssueId == c.OwnIssueId
                    || c.HostId is { } host && await db.RedmineLinks.AnyAsync(l => l.HostId == host && l.IssueId == a.IssueId, ct));
                return (linked ? null : "Only the case's own ticket, or one linked to its host.", $"issue:{a.IssueId}");
            }
            case CookieActionKind.ObjectNote:
            {
                if (!Guid.TryParse(a.ObjectId, out var id))
                {
                    return ("A note names its host or sensor by id.", null);
                }
                if (!rights.Has(Area.Monitoring, Level.Edit))
                {
                    return ("Cookie's group needs Monitoring: Edit for a note on a host or sensor.", null);
                }
                var known = await db.Hosts.AnyAsync(h => h.Id == id, ct) || await db.Sensors.AnyAsync(s => s.Id == id, ct);
                return (known ? null : "No host or sensor with that id.", $"object:{id}");
            }
            case CookieActionKind.MailEvent:
                return (p.Mail is null ? "A mail rule's event needs its mail." : null, p.Mail is null ? null : $"mail:{p.Mail.MailId}");
            default:
                // One thing per case waits for a person at a time.
                return (null, c is null ? null : $"case:{c.Id}");
        }
    }

    /// <summary>The evidence for Seraph, read again from the source (§5), masked like Cookie's own.</summary>
    private static async Task<string> EvidenceAsync(PnmtdDbContext db, CookieProposal p, CookieCaseEntity? c, IReadOnlyList<CaseMember> members, Pseudonyms privacy,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        if (c is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Case {c.Id}: {privacy.Mask(c.Title)} ({c.Kind}, {c.Importance}, open since {c.OpenedAt:u}, last progress {c.ProgressAt:u})");
            foreach (var m in members)
            {
                var message = string.IsNullOrWhiteSpace(m.Message) ? "" : $", <untrusted>{privacy.Mask(InputSanitizer.Clean(m.Message, 300).Text)}</untrusted>";
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"- {m.Type} {m.Id}: {privacy.Mask(m.Name)} since {m.Since:u}{(m.Code is { } code ? $", code {code}" : "")}{message}");
            }
            if (c.IssueId is { } issue)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"Ticket #{issue}: author {privacy.Mask(c.AuthorName)}, assignee {privacy.Mask(c.AssigneeName)}, status {c.StatusName}, first answer {c.FirstAnswerAt:u}, last staff update {c.LastStaffUpdateAt:u}");
            }
            var entries = await db.CookieCaseEntries.AsNoTracking().Where(e => e.CaseId == c.Id).OrderByDescending(e => e.At).Take(15).ToListAsync(ct);
            foreach (var e in entries.AsEnumerable().Reverse())
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- {e.At:u} {e.Kind} ({e.Actor}): {privacy.Mask(InputSanitizer.Clean(e.Text, 300).Text)}");
            }
        }
        if (Guid.TryParse(p.Action.UserId, out var userId) && await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) is { } name)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"The recipient {userId} is {privacy.Mask(name)}.");
        }
        if (p.Mail is { } mail && await db.MailLogs.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mail.MailId, ct) is { } log)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"The mail: <untrusted>{privacy.Mask(InputSanitizer.Clean($"Subject: {log.Subject}\n\n{log.Content}", 3000).Text)}</untrusted>");
        }
        return text.Length == 0 ? "(no case)" : text.ToString();
    }

    // --- Running it ---------------------------------------------------------------------------------------------------------

    private async Task RunAsync(PnmtdDbContext db, CookieDecisionEntity log, CookieProposal p, CookieCaseEntity? c, string source, CancellationToken ct)
    {
        var a = p.Action;
        var identity = await settings.GetAsync<IdentitySettings>(ct);
        var sign = $"\n\n— {identity.Name}, PNMT's assistant";
        var subject = privacy.Unmask(a.Subject);
        var body = privacy.Unmask(a.Text);
        try
        {
            switch (a.Action)
            {
                case CookieActionKind.Pushover or CookieActionKind.Email:
                {
                    var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(a.UserId!), ct);
                    var pushover = a.Action == CookieActionKind.Pushover;
                    var row = await outbox.SendAsync(new OutboxRequest(pushover ? CookieChannel.Pushover : CookieChannel.Email, subject,
                        pushover ? body : body + sign, c?.Id, null, user.Id, user.DisplayName, pushover ? user.PushoverKey : user.Email, c?.IssueId,
                        Urgency: a.Urgency, Source: source), ct);
                    Outcome(log, row);
                    break;
                }
                case CookieActionKind.Ticket:
                {
                    var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
                    var row = await outbox.SendAsync(new OutboxRequest(CookieChannel.RedmineTicket, subject, body + sign, c?.Id,
                        Recipient: $"project {redmineSettings.TicketProjectId}", ProjectId: redmineSettings.TicketProjectId, TrackerId: redmineSettings.TicketTrackerId,
                        Urgency: a.Urgency, Source: source), ct);
                    Outcome(log, row);
                    if (row.State == CookieOutboxState.Sent && row.IssueId is { } issueId && c is not null)
                    {
                        var tracked = await db.CookieCases.SingleAsync(x => x.Id == c.Id, ct);
                        tracked.OwnIssueId = issueId;
                        if (c.HostId is { } hostId && !await db.RedmineLinks.AnyAsync(l => l.HostId == hostId && l.IssueId == issueId, ct))
                        {
                            db.RedmineLinks.Add(new RedmineLinkEntity
                            {
                                Id = Guid.NewGuid(), IssueId = issueId, HostId = hostId, Note = $"Opened by {identity.Name}", CreatedAt = Clock.UtcNow,
                                CreatedByName = identity.Name,
                            });
                        }
                        CaseBook.Entry(db, c.Id, Clock.UtcNow, "ticket", $"Cookie opened #{issueId}.", "cookie");
                    }
                    break;
                }
                case CookieActionKind.PrivateNote or CookieActionKind.PublicAnswer:
                {
                    var row = await outbox.SendAsync(new OutboxRequest(a.Action == CookieActionKind.PrivateNote ? CookieChannel.RedminePrivateNote : CookieChannel.RedmineNote,
                        $"#{a.IssueId}: {subject}", body + sign, c?.Id, IssueId: a.IssueId, Recipient: $"#{a.IssueId}", Urgency: a.Urgency, Source: source), ct);
                    Outcome(log, row);
                    break;
                }
                case CookieActionKind.ObjectNote:
                    await ObjectNoteAsync(db, log, Guid.Parse(a.ObjectId!), body, identity.Name, ct);
                    break;
                case CookieActionKind.MailEvent:
                    await MailEventAsync(db, log, p.Mail!, ct);
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Cookie's action {Kind} failed", log.Kind);
            log.Outcome = "failed";
            log.Detail = CaseBook.Cut(e.Message, 1000);
        }
        if (c is not null && log.Outcome is "done" or "held-back")
        {
            CaseBook.Entry(db, c.Id, Clock.UtcNow, log.Outcome == "done" ? "action" : "held-back", $"{log.Title}: {log.Detail}", "cookie");
        }
    }

    private static void Outcome(CookieDecisionEntity log, CookieOutboxEntity row)
    {
        log.OutboxId = row.Id;
        log.Outcome = row.State switch
        {
            CookieOutboxState.Sent or CookieOutboxState.Simulated => "done",
            CookieOutboxState.HeldBack => "held-back",
            _ => "failed",
        };
        log.Detail = CaseBook.Cut(row.Detail, 1000);
    }

    /// <summary>A note on a host or sensor (§6): added to its notes, never replacing them, within the day's limit.</summary>
    private async Task ObjectNoteAsync(PnmtdDbContext db, CookieDecisionEntity log, Guid id, string body, string name, CancellationToken ct)
    {
        var limit = (await settings.GetAsync<ActionSettings>(ct)).Limits.ObjectNotesPerDay;
        var today = Clock.ToUtc(Clock.LocalNow.Date);
        if (await db.CookieDecisions.CountAsync(d => d.Kind == "object-note" && d.Outcome == "done" && d.At >= today, ct) >= limit)
        {
            log.Outcome = "held-back";
            log.Detail = $"Notes on hosts and sensors: {limit} a day.";
            return;
        }
        var line = $"[{CaseBook.Cut(name, 40)}, {Clock.LocalNow:yyyy-MM-dd HH:mm}] {body.ReplaceLineEndings(" ")}";
        if (await db.Hosts.SingleOrDefaultAsync(h => h.Id == id, ct) is { } host)
        {
            host.Notes = string.IsNullOrWhiteSpace(host.Notes) ? line : $"{host.Notes.TrimEnd()}\n\n{line}";
            AuditLog.Record(db, AuditEvents.CookieNoteAdded, AuditLog.Cut($"A note on {host.Name}: {body}", AuditLog.MaxDetail), AuditWho.Cookie("note"),
                AuditTarget.Of(AuditTargets.Host, host.Id, host.Name));
        }
        else
        {
            var sensor = await db.Sensors.SingleAsync(s => s.Id == id, ct);
            sensor.Notes = string.IsNullOrWhiteSpace(sensor.Notes) ? line : $"{sensor.Notes.TrimEnd()}\n\n{line}";
            AuditLog.Record(db, AuditEvents.CookieNoteAdded, AuditLog.Cut($"A note on {sensor.Name}: {body}", AuditLog.MaxDetail), AuditWho.Cookie("note"),
                AuditTarget.Of(AuditTargets.Sensor, sensor.Id, sensor.Name, sensor.ParentId));
        }
        log.Outcome = "done";
        log.Detail = "Added to the notes.";
    }

    /// <summary>An <i>AI every time</i> rule's event (§12): the rule's OK or fail code on its sensor, and the mail processed.</summary>
    private static async Task MailEventAsync(PnmtdDbContext db, CookieDecisionEntity log, MailEventPayload m, CancellationToken ct)
    {
        var mail = await db.MailLogs.SingleOrDefaultAsync(x => x.Id == m.MailId, ct);
        if (mail is null || mail.Processed)
        {
            log.Outcome = "failed";
            log.Detail = "The mail was handled meanwhile.";
            return;
        }
        db.Events.Add(new EventEntity
        {
            Id = Guid.NewGuid(), SensorId = m.SensorId, Code = m.Code, Created = Clock.UtcNow, Message = CaseBook.Cut($"Cookie: {m.Message}".Trim(), 500),
            Source = mail.From,
        });
        mail.Processed = true;
        mail.Disposition = MailDisposition.AiEvent;
        mail.AiState = MailAiState.Analysed;
        mail.ProcessLog += $"\nCookie: {m.Code}: {m.Message}";
        log.Outcome = "done";
        log.Detail = $"Event {m.Code} on the rule's sensor.";
    }
}

/// <summary>The inbox's payload for <see cref="CookieExecutor.InboxKind"/>: the decision log's entry.</summary>
public sealed record CookieActionPayload(long LogId);
