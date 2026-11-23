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

namespace PNMTD.Cookie;

/// <summary>
/// A proposal to take through the executor: from a round (with Cookie's rights and the round's masking), or from a mail; with an
/// <see cref="Order"/> when an admin's empowered message asked for it (docs/cookie-powers-plan.md §6).
/// </summary>
public sealed record ActionRequest(
    CookieProposal Proposal,
    CookieCaseEntity? Case,
    long? RoundId,
    UserRights Rights,
    Pseudonyms Privacy,
    IReadOnlyCollection<string> Areas,
    Func<string, CancellationToken, Task<ProposedAction?>>? Retry = null,
    CookieOrder? Order = null);

/// <summary>A lounge e-mail to take through the executor (docs/lounge-plan.md §8.2): who wrote it, to whom, what, and what it read.</summary>
public sealed record LoungeEmailRequest(string Voice, Guid UserId, string Subject, string Text, IReadOnlyList<string> Areas, Pseudonyms Privacy);

/// <summary>
/// Cookie's executor (docs/cookie-plan.md §6, §21.1): every proposal goes through the action's level, its confidence
/// threshold, its targets and rights, mute and suppress and the case's stop, duplicates, Seraph for what leaves PNMT or
/// reaches people, and then the inbox (<i>Propose</i>) or the outbox (<i>Auto</i>). Each step is in Cookie's decision log.
/// Whatever a model said, code decides here.
/// </summary>
public sealed class CookieExecutor(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, Seraph seraph, Instructions instructions, Outbox outbox,
    DecisionBook inbox, PrivacyFilter privacy, PNMTD.Availability.AvailabilityPlan plan, IServiceProvider services, CookieIntegrations integrations,
    ILogger<CookieExecutor> logger)
{
    public const string InboxKind = "cookie.action";

    /// <summary>The areas a Cookie proposal in the inbox may carry for everyone with <i>Decisions: Decide</i>; others are admins' (§9.2).</summary>
    private static readonly HashSet<string> InboxAreas = ["Monitoring", "Mail", "Cookie"];

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
        // An order (docs/cookie-powers-plan.md §6): an admin's empowered message asked for it. The gates of judgement are passed and
        // named in the detail; the gates of fact (a real user with a channel, the integration, the rights) stay.
        var order = r.Order;
        var passed = new List<string>();
        log.OrderedBy = CaseBook.Cut(order?.Name, 200);

        string? Done(string outcome, string? detail)
        {
            log.Outcome = outcome;
            log.Detail = CaseBook.Cut(WithOrder(detail), 1000);
            return null;
        }

        string? WithOrder(string? detail) => order is null ? detail
            : $"On {order.Name}'s order from the chat.{(passed.Count == 0 ? "" : $" Passed: {string.Join("; ", passed)}.")} {detail}".TrimEnd();

        if (a.Action != CookieActionKind.HandToPerson && rule.Level == ActionLevel.Off)
        {
            if (order is null)
            {
                Done("off", $"{Label(a.Action)} is off on Admin → Cookie → Actions.");
                await db.SaveChangesAsync(ct);
                return log;
            }
            passed.Add($"{Label(a.Action)} is off");
        }
        var (refusal, target) = await CheckAsync(db, proposal, r.Case, r.Rights, ct, order is not null);
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
        // Mute doesn't stop an access notice, and a suppress of the channel "access" never an alarming one (docs/cookie-access-plan.md §6.4).
        var blocked = a.Action == CookieActionKind.AccessNotice
            ? Instructions.SuppressesAccess(active, proposal.Access)
            : Instructions.Blocks(active, a.Action, r.Case, members, Guid.TryParse(a.ObjectId, out var obj) ? obj : null);
        if (blocked is not null)
        {
            if (order is null)
            {
                Done("muted", $"{blocked.Kind} {blocked.Scope} {blocked.Label ?? blocked.Value}, by {blocked.CreatedByName}");
                await db.SaveChangesAsync(ct);
                return log;
            }
            passed.Add($"{blocked.Kind} {blocked.Scope} {blocked.Label ?? blocked.Value}, by {blocked.CreatedByName}");
        }
        // An access notice again after 4 hours, or when its verdict rises (its target names the verdict, docs/cookie-access-plan.md §6.3).
        var since = a.Action == CookieActionKind.AccessNotice ? Clock.UtcNow.AddHours(-4) : Clock.UtcNow.AddDays(-1);
        if (order is null && target is not null && await db.CookieDecisions.AnyAsync(d => d.CaseId == log.CaseId && d.Kind == log.Kind && d.Target == target
                && (d.Outcome == "proposed" || d.Outcome == "running" || d.At >= since && (d.Outcome == "done" || d.Outcome == "approved")), ct))
        {
            Done("duplicate", "The same action on the same target was done within a day, or waits for a person.");
            await db.SaveChangesAsync(ct);
            return log;
        }

        // Seraph (§5): approve, revise (one retry), reject, or a person. Without Seraph, a person.
        // An access notice below its threshold went one lower already; only an unusual one goes to a person (docs/cookie-access-plan.md §6.4).
        var thresholded = a.Action != CookieActionKind.AccessNotice || proposal.Access?.Verdict == "unusual";
        var toPerson = a.Action == CookieActionKind.HandToPerson ? "Cookie hands it to a person."
            : order is null && thresholded && a.Confidence < rule.MinConfidence ? $"Below the threshold ({a.Confidence:0.00} < {rule.MinConfidence:0.00})." : null;
        // Outside working hours Cookie reaches nobody by itself (§8.3): a message waits for a person's decision; an order is the person's.
        if (order is null && a.Action is CookieActionKind.Pushover or CookieActionKind.Email && !(await plan.CalendarAsync(ct)).IsWorking(Clock.UtcNow))
        {
            toPerson ??= "Outside working hours: a person decides whether it goes out now.";
        }
        // Seraph reads every order, object notes included (docs/cookie-powers-plan.md §6.1), and judges harm only.
        if (Reviewed(a.Action) || order is not null)
        {
            var evidence = await EvidenceAsync(db, proposal, r.Case, members, r.Privacy, ct);
            if (order is not null)
            {
                evidence += await OrderEvidenceAsync(db, order, proposal, r.Privacy, ct);
            }
            var (verdict, model, error) = await seraph.ReviewAsync(Masked(proposal, r.Privacy), evidence, r.Privacy.Mask(Instructions.PromptText(active)), ct,
                order is null ? null : Seraph.OrderParagraph(r.Privacy.Mask(order.Name)));
            log.Model = model;
            if (verdict is null)
            {
                toPerson ??= $"Seraph couldn't review it: {error}";
            }
            else
            {
                log.Verdict = verdict.Verdict.ToString().ToLowerInvariant();
                log.VerdictReason = verdict.Reason;
                log.VerdictConfidence = verdict.Confidence;
                switch (verdict.Verdict)
                {
                    case SeraphVerdictKind.Reject when order is not null && verdict.Confidence < actions.SeraphVetoConfidence:
                        // A doubt, not a veto (docs/cookie-powers-plan.md §6.1): the admin decides in the inbox.
                        toPerson ??= $"Seraph doubts it ({verdict.Confidence:0.00}): {verdict.Reason}";
                        break;
                    case SeraphVerdictKind.Reject:
                        Done("rejected", order is null ? $"Seraph: {verdict.Reason}" : $"Seraph stopped the order ({verdict.Confidence:0.00}): {verdict.Reason}");
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
        if (toPerson is not null || (order is null && rule.Level == ActionLevel.Propose))
        {
            await db.SaveChangesAsync(ct);
            // An order's inbox entry is admins' and outlives its request case, which closes with the reply (docs/cookie-powers-plan.md §6).
            log.InboxDecisionId = await inbox.RaiseAsync(new DecisionDraft(InboxKind, CaseBook.Cut($"Cookie proposes: {log.Title}", 300)!,
                Summary(proposal, r.Privacy, log.VerdictReason, WithOrder(toPerson)), new CookieActionPayload(log.Id), $"{InboxKind}:{log.Id}", r.Case?.Id,
                "cookie-case", order is null ? r.Case?.Id : null, AdminOnly: order is not null || log.Areas.Any(x => !InboxAreas.Contains(x)),
                Priority: a.Urgency == Urgency.Urgent ? 2 : a.Urgency == Urgency.Important ? 1 : 0), ct);
            Done("proposed", toPerson ?? "Propose: a person decides in the decisions inbox.");
            await db.SaveChangesAsync(ct);
            return log;
        }
        await RunAsync(db, log, proposal, r.Case, order is null ? "cookie" : "order", ct, order?.Name);
        log.Detail = CaseBook.Cut(WithOrder(log.Detail), 1000);
        await db.SaveChangesAsync(ct);
        return log;
    }

    /// <summary>A lounge e-mail again to the same person within this long is a duplicate (docs/lounge-plan.md §8.2).</summary>
    public static readonly TimeSpan LoungeEmailEvery = TimeSpan.FromDays(7);

    /// <summary>
    /// A lounge e-mail (docs/lounge-plan.md §8.2): the lounge's level, the recipient's checks (active, an address, not opted out, works
    /// today), mute and suppress of e-mail, one per person a week, working hours, and a review by the other voice's model, never its own (D7);
    /// then the inbox (<i>Propose</i>) or the outbox's own channel (<i>Auto</i>). Each step is in Cookie's decision log, kept out of the lessons.
    /// </summary>
    public async Task<CookieDecisionEntity> HandleLoungeEmailAsync(LoungeEmailRequest r, CancellationToken ct)
    {
        var lounge = await settings.GetAsync<Lounge.LoungeSettings>(ct);
        var voiceName = r.Voice == Lounge.LoungeVoices.Seraph ? "Seraph" : (await settings.GetAsync<IdentitySettings>(ct)).Name;
        var action = new ProposedAction(CookieActionKind.LoungeEmail, r.UserId.ToString(), 0, null, CaseBook.Cut(r.Subject.Trim(), 120)!,
            CaseBook.Cut(r.Text.Trim(), 1500)!, $"From the lounge, written by {voiceName}.", [], 1, Urgency.Info);
        var proposal = new CookieProposal(action, LoungeVoice: r.Voice);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var recipient = await db.Users.AsNoTracking().Where(u => u.Id == r.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? r.UserId.ToString();
        var log = new CookieDecisionEntity
        {
            At = Clock.UtcNow, Kind = ProposedAction.Name(CookieActionKind.LoungeEmail),
            Title = CaseBook.Cut($"{Label(CookieActionKind.LoungeEmail)} by {voiceName} to {recipient}: {r.Privacy.Unmask(action.Subject)}", 300)!,
            Proposal = JsonSerializer.Serialize(proposal, Json), Confidence = 1, Areas = [.. r.Areas.Distinct().Order()], Outcome = "noted",
        };
        db.CookieDecisions.Add(log);
        async Task<CookieDecisionEntity> Done(string outcome, string detail)
        {
            log.Outcome = outcome;
            log.Detail = CaseBook.Cut(detail, 1000);
            await db.SaveChangesAsync(ct);
            return log;
        }
        if (lounge.EmailLevel == ActionLevel.Off)
        {
            return await Done("off", "Lounge e-mails are off on Admin → Cookie → Lounge.");
        }
        var (refusal, target) = await CheckAsync(db, proposal, null, UserRights.None, ct);
        log.Target = target;
        if (refusal is not null)
        {
            return await Done("refused", refusal);
        }
        var active = await instructions.ActiveAsync(ct);
        if (Instructions.Blocks(active, CookieActionKind.LoungeEmail, null, []) is { } blocked)
        {
            return await Done("muted", $"{blocked.Kind} {blocked.Scope} {blocked.Label ?? blocked.Value}, by {blocked.CreatedByName}");
        }
        var since = Clock.UtcNow - LoungeEmailEvery;
        if (await db.CookieDecisions.AnyAsync(d => d.Kind == log.Kind && d.Target == target
                && (d.Outcome == "proposed" || d.Outcome == "running" || d.At >= since && (d.Outcome == "done" || d.Outcome == "approved")), ct))
        {
            return await Done("duplicate", "A lounge e-mail to the same person was sent this week, or waits for a person.");
        }
        string? toPerson = null;
        if (!(await plan.CalendarAsync(ct)).IsWorking(Clock.UtcNow))
        {
            toPerson = "Outside working hours: a person decides whether it goes out now.";
        }
        // Never its own reviewer (D7): Seraph's model reviews Cookie's e-mails, and Cookie's model reviews Seraph's.
        var reviewer = r.Voice == Lounge.LoungeVoices.Seraph ? await seraph.CookieChoiceAsync(ct) : await seraph.ChoiceAsync(ct);
        var evidence = $"The recipient {r.UserId} is {r.Privacy.Mask(recipient)}. {voiceName} wrote it in the lounge, a conversation everyone at the " +
            "organisation reads.";
        var tokens = services.GetRequiredService<IOptionsMonitor<CookieOptions>>().CurrentValue.Tokens.LoungeReview;
        var (verdict, model, error) = reviewer is null ? (null, null, "Seraph has no model (Cookie__SeraphModel).")
            : await seraph.ReviewAsync(Masked(proposal, r.Privacy), evidence, r.Privacy.Mask(Instructions.PromptText(active)), ct, Seraph.LoungeParagraph, reviewer,
                "lounge.review", tokens);
        log.Model = model;
        if (verdict is null)
        {
            toPerson ??= $"It couldn't be reviewed: {error}";
        }
        else
        {
            log.Verdict = verdict.Verdict.ToString().ToLowerInvariant();
            log.VerdictReason = verdict.Reason;
            log.VerdictConfidence = verdict.Confidence;
            if (verdict.Verdict == SeraphVerdictKind.Reject)
            {
                return await Done("rejected", $"The review: {verdict.Reason}");
            }
            if (verdict.Verdict is SeraphVerdictKind.Revise or SeraphVerdictKind.Ask)
            {
                toPerson ??= $"The review: {verdict.Reason}";
            }
        }
        if (toPerson is not null || lounge.EmailLevel == ActionLevel.Propose)
        {
            await db.SaveChangesAsync(ct);
            log.InboxDecisionId = await inbox.RaiseAsync(new DecisionDraft(InboxKind, CaseBook.Cut($"Cookie proposes: {log.Title}", 300)!,
                Summary(proposal, r.Privacy, log.VerdictReason, toPerson), new CookieActionPayload(log.Id), $"{InboxKind}:{log.Id}",
                AdminOnly: log.Areas.Any(x => !InboxAreas.Contains(x)), Priority: 0), ct);
            return await Done("proposed", toPerson ?? "Propose: a person decides in the decisions inbox.");
        }
        await RunAsync(db, log, proposal, null, "lounge", ct);
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
        // Checked again (§21.1): what was true when Cookie proposed it may not be any more. An order keeps passing the gates of
        // judgement it passed the first time (docs/cookie-powers-plan.md §6).
        var ordered = log.OrderedBy is not null;
        var rights = await services.GetRequiredService<CookieRound>().RightsAsync(ct) ?? UserRights.None;
        var level = proposal.Action.Action == CookieActionKind.LoungeEmail ? (await settings.GetAsync<Lounge.LoungeSettings>(ct)).EmailLevel
            : (await settings.GetAsync<ActionSettings>(ct)).Levels.Of(proposal.Action.Action).Level;
        var (refusal, _) = await CheckAsync(db, proposal, c, rights, ct, ordered);
        var members = c is null ? [] : JsonSerializer.Deserialize<List<CaseMember>>(c.Members, CookieSettings.Json) ?? [];
        var blocked = ordered ? null : Instructions.Blocks(await instructions.ActiveAsync(ct), proposal.Action.Action, c, members,
            Guid.TryParse(proposal.Action.ObjectId, out var obj) ? obj : null);
        var why = !ordered && level == ActionLevel.Off ? $"{Label(proposal.Action.Action)} is off now." : refusal
            ?? (c?.StoppedAt is not null ? $"The case was stopped by {c.StoppedByName}." : null)
            ?? (blocked is not null ? $"{blocked.Kind} {blocked.Scope} {blocked.Label ?? blocked.Value}, by {blocked.CreatedByName}." : null);
        if (why is not null)
        {
            log.Outcome = "refused";
            log.Detail = CaseBook.Cut($"Approved by {by}, but not any more: {why}", 1000);
            await db.SaveChangesAsync(ct);
            return log;
        }
        await RunAsync(db, log, proposal, c, ordered ? "order" : proposal.Action.Action == CookieActionKind.LoungeEmail ? "lounge" : "cookie", ct, log.OrderedBy,
            approved: true);
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
        CookieActionKind.AccessNotice => "Access notice",
        CookieActionKind.LoungeEmail => "Lounge e-mail",
        CookieActionKind.AwayNotice => "Away notice",
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
        CancellationToken ct, bool ordered = false)
    {
        var a = p.Action;
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
                // An order chose the urgency and the person (docs/cookie-powers-plan.md §6).
                if (!ordered && (a.Action == CookieActionKind.Pushover && a.Urgency != Urgency.Urgent || a.Action == CookieActionKind.Email && a.Urgency == Urgency.Info))
                {
                    return (a.Action == CookieActionKind.Pushover ? "Pushover is only for what is urgent." : "E-mail is for what is important or urgent.", null);
                }
                var today = DateOnly.FromDateTime(Clock.LocalNow);
                var day = await plan.DayAsync(today, ct, [userId]);
                if (!ordered && !day.Closed(today) && !day.Works(userId, today))
                {
                    return ($"{user.DisplayName} doesn't work today (the availability plan).", null);
                }
                return (null, $"user:{userId}");
            }
            case CookieActionKind.LoungeEmail:
            {
                // docs/lounge-plan.md §8.2, §9.4: an active user with an address, who hasn't opted out and works today; never important.
                if (!Guid.TryParse(a.UserId, out var userId))
                {
                    return ("A person must be named by their user id.", null);
                }
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
                if (user is null)
                {
                    return ("No active PNMT user with that id.", null);
                }
                if (string.IsNullOrWhiteSpace(user.Email))
                {
                    return ($"{user.DisplayName} has no e-mail address.", null);
                }
                if (!user.LoungeEmails)
                {
                    return ($"{user.DisplayName} doesn't want e-mails from the lounge.", null);
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
                // Without a writable Redmine there is no ticket (docs/cookie-fallback-plan.md §4.4): the reading says why.
                var reading = await integrations.ReadAsync(ct);
                if (!reading.CanTicket)
                {
                    return (reading.NoTicketReason, null);
                }
                if (!rights.Has(Area.Redmine, Level.Link))
                {
                    return ("Cookie's group needs Redmine: Link for a ticket.", null);
                }
                // An order from the chat asks for a ticket by itself (docs/cookie-powers-plan.md §6); otherwise a ticket is a problem's.
                if (ordered)
                {
                    return (null, c is null ? null : $"case:{c.Id}");
                }
                if (c is null || c.Kind is not (CookieCaseKind.Problem or CookieCaseKind.Access))
                {
                    return ("A ticket is for a problem's case.", null);
                }
                // An access case gets a ticket only when a person asked for one (docs/cookie-access-plan.md §7).
                if (c.Kind == CookieCaseKind.Access && c.TicketAskedAt is null)
                {
                    return ("An access case gets a ticket only when someone asks for one (Ask Cookie for a ticket).", null);
                }
                if (c.OwnIssueId is { } own && !await db.RedmineIssues.AnyAsync(i => i.IssueId == own && i.IsClosed, ct))
                {
                    return ($"The case has its ticket already: #{own}.", null);
                }
                return (null, $"case:{c.Id}");
            }
            case CookieActionKind.PrivateNote or CookieActionKind.PublicAnswer:
            {
                var reading = await integrations.ReadAsync(ct);
                if (!reading.RedmineWritable)
                {
                    return (reading.NoNoteReason, null);
                }
                if (!rights.Has(Area.Redmine, Level.View))
                {
                    return ("Cookie's group needs Redmine: View for a note on a ticket.", null);
                }
                var linked = c is not null && (a.IssueId == c.IssueId || a.IssueId == c.OwnIssueId
                    || c.HostId is { } host && await db.RedmineLinks.AnyAsync(l => l.HostId == host && l.IssueId == a.IssueId, ct));
                return (linked || ordered ? null : "Only the case's own ticket, or one linked to its host.", $"issue:{a.IssueId}");
            }
            case CookieActionKind.ObjectNote:
            {
                if (!Guid.TryParse(a.ObjectId, out var id))
                {
                    return ("A note names its host or sensor by id.", null);
                }
                // Cookie's notes are rows of its own (docs/cookie-object-notes-plan.md §3.6): it needs to see the object, nothing on the people's notes.
                if (!rights.Has(Area.Monitoring, Level.View))
                {
                    return ("Cookie's group needs Monitoring: View for a note on a host or sensor.", null);
                }
                if (await CookieObjectNotes.ObjectAsync(db, id, ct) is not { } o)
                {
                    return ("No host or sensor with that id.", null);
                }
                var cap = (await settings.LimitsAsync(ct)).MaxNotesPerObject;
                var count = await CookieObjectNotes.CountAsync(db, id, ct);
                if (count >= cap)
                {
                    return ($"{o.Name} has {count} notes of yours already; a person removes old ones. Don't propose another.", null);
                }
                return (null, $"object:{id}");
            }
            case CookieActionKind.MailEvent:
                return (p.Mail is null ? "A mail rule's event needs its mail." : null, p.Mail is null ? null : $"mail:{p.Mail.MailId}");
            case CookieActionKind.AccessNotice:
            {
                // The recipients are the Access tab's contacts, never the model's (docs/cookie-access-plan.md §6.4).
                if (p.Access is not { } access || c is null || c.Id != access.CaseId || c.Kind != CookieCaseKind.Access || !c.Open)
                {
                    return ("An access notice is for its own open access case.", null);
                }
                if (Access.AccessSignals.VerdictOf(access.Verdict) is not { } v || v == Access.AccessVerdict.Normal)
                {
                    return ("An access notice needs a verdict above normal.", null);
                }
                return (null, $"access:{c.Id}:{access.Verdict}");
            }
            case CookieActionKind.AwayNotice:
            {
                // Whom to tell is the settings', never the model's (docs/cookie-redmine-tab-plan.md §6.5): the proposal names its comments only.
                if (p.Away is not { CommentIds.Count: > 0 } away || c is null || c.Id != away.CaseId || c.Kind != CookieCaseKind.Ticket)
                {
                    return ("An away notice is for its own ticket's comments.", null);
                }
                var ids = away.CommentIds.ToList();
                if (await db.CookieAwayComments.CountAsync(x => ids.Contains(x.Id) && x.CaseId == c.Id && x.State == AwayCommentState.Pending, ct) != ids.Count)
                {
                    return ("These comments aren't waiting for a verdict any more.", null);
                }
                return (null, $"away:{away.IssueId}:{ids.Min()}");
            }
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
        if (p.Access is { } access)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"The verdict, held to the signals by code: {access.Verdict}; a strong signal: {(access.Strong ? "yes" : "no")}.");
            text.AppendLine(await Access.AccessJudge.EvidenceAsync(db, access.CaseId, privacy, ct));
        }
        if (p.Away is { } away)
        {
            // The comments as Cookie read them (docs/cookie-redmine-tab-plan.md §6.4): outside text.
            var ids = away.CommentIds.ToList();
            foreach (var x in await db.CookieAwayComments.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.JournalId).ToListAsync(ct))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"A comment by {privacy.Mask(x.AuthorName)} at {x.CommentAt:u}, the assignee away " +
                    $"{Away.AwayRules.Until(x.AwayUntil)}: <untrusted source=\"redmine-comment\">{privacy.Mask(InputSanitizer.Clean(x.Text, 2000).Text)}</untrusted>");
            }
        }
        if (p.Mail is { } mail && await db.MailLogs.AsNoTracking().SingleOrDefaultAsync(m => m.Id == mail.MailId, ct) is { } log)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"The mail: <untrusted>{privacy.Mask(InputSanitizer.Clean($"Subject: {log.Subject}\n\n{log.Content}", 3000).Text)}</untrusted>");
        }
        return text.Length == 0 ? "(no case)" : text.ToString();
    }

    /// <summary>The order itself as evidence for Seraph (docs/cookie-powers-plan.md §6.1): the admin's message as outside text, and who the recipient is.</summary>
    private static async Task<string> OrderEvidenceAsync(PnmtdDbContext db, CookieOrder order, CookieProposal p, Pseudonyms privacy, CancellationToken ct)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Ordered by {privacy.Mask(order.Name)}, an admin, in the chat: <untrusted source=\"message\">" +
            $"{privacy.Mask(InputSanitizer.Clean(order.Text, 2000).Text)}</untrusted>");
        if (p.Action.Action is CookieActionKind.Pushover or CookieActionKind.Email && Guid.TryParse(p.Action.UserId, out var id)
            && await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct) is { } user)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"The recipient: {privacy.Mask(user.DisplayName)} (user {id}), {(user.IsAdmin ? "an admin" : "a PNMT user")}.");
        }
        return text.ToString();
    }

    // --- Running it ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="orderedBy"/>: the admin whose order it is; the outbox then counts no limit and the audit names them.
    /// <paramref name="approved"/>: a person approved it in the inbox. Either way a person waits, so a Redmine write goes in the
    /// worker's button lane (docs/redmine-queue-outbound-plan.md §3.3); the limits don't change.
    /// </summary>
    private async Task RunAsync(PnmtdDbContext db, CookieDecisionEntity log, CookieProposal p, CookieCaseEntity? c, string source, CancellationToken ct,
        string? orderedBy = null, bool approved = false)
    {
        var byPerson = approved || orderedBy is not null;
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
                        Urgency: a.Urgency, Source: source, Unlimited: orderedBy is not null, OrderedBy: orderedBy, ByPerson: byPerson), ct);
                    Outcome(log, row);
                    break;
                }
                case CookieActionKind.LoungeEmail:
                {
                    // Signed by the voice that wrote it, from the lounge, with a link to it (docs/lounge-plan.md §8.2).
                    var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == Guid.Parse(a.UserId!), ct);
                    var writer = p.LoungeVoice == Lounge.LoungeVoices.Seraph ? "Seraph" : identity.Name;
                    var other = p.LoungeVoice == Lounge.LoungeVoices.Seraph ? identity.Name : "Seraph";
                    var baseUrl = services.GetRequiredService<IConfiguration>()["PublicBaseUrl"];
                    var link = string.IsNullOrWhiteSpace(baseUrl) ? "" : $"\n{baseUrl.TrimEnd('/')}/pnmt-admin/lounge";
                    var row = await outbox.SendAsync(new OutboxRequest(CookieChannel.LoungeEmail, subject, $"{body}\n\n— {writer} (and {other}), from PNMT's lounge{link}",
                        null, null, user.Id, user.DisplayName, user.Email, Urgency: Urgency.Info, Source: source, ByPerson: byPerson), ct);
                    Outcome(log, row);
                    break;
                }
                case CookieActionKind.Ticket:
                {
                    var redmineSettings = await settings.GetAsync<CookieRedmineSettings>(ct);
                    var row = await outbox.SendAsync(new OutboxRequest(CookieChannel.RedmineTicket, subject, body + sign, c?.Id,
                        Recipient: $"project {redmineSettings.TicketProjectId}", ProjectId: redmineSettings.TicketProjectId, TrackerId: redmineSettings.TicketTrackerId,
                        Urgency: a.Urgency, Source: source, Unlimited: orderedBy is not null, OrderedBy: orderedBy, ByPerson: byPerson), ct);
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
                        $"#{a.IssueId}: {subject}", body + sign, c?.Id, IssueId: a.IssueId, Recipient: $"#{a.IssueId}", Urgency: a.Urgency, Source: source, Unlimited: orderedBy is not null, OrderedBy: orderedBy, ByPerson: byPerson), ct);
                    Outcome(log, row);
                    break;
                }
                case CookieActionKind.ObjectNote:
                    await ObjectNoteAsync(db, log, Guid.Parse(a.ObjectId!), body, ct);
                    break;
                case CookieActionKind.MailEvent:
                    await MailEventAsync(db, log, p.Mail!, ct);
                    break;
                case CookieActionKind.AwayNotice:
                {
                    // Code tells whom the settings name, in working hours, once per ticket within 4 hours (§6.5 to §6.8).
                    var away = p.Away!;
                    var ids = away.CommentIds.ToList();
                    var rows = await db.CookieAwayComments.Where(x => ids.Contains(x.Id) && x.State == AwayCommentState.Pending).ToListAsync(ct);
                    foreach (var row in rows)
                    {
                        Away.AwayNotices.ToTell(row, "cookie", "high", a.Confidence, privacy.Unmask(a.Reason), Clock.UtcNow, log.Id > 0 ? log.Id : row.DecisionId);
                    }
                    await db.SaveChangesAsync(ct);
                    var told = await services.GetRequiredService<Away.AwayNotices>().SendDueAsync(ct);
                    log.Outcome = rows.Count > 0 ? "done" : "failed";
                    log.Detail = rows.Count == 0 ? "The comments weren't waiting any more."
                        : told > 0 ? "Told whom the settings name; the comments show who." : "Waits for working hours, or for the ticket's next notice.";
                    break;
                }
                case CookieActionKind.AccessNotice:
                {
                    var access = p.Access!;
                    var notified = await services.GetRequiredService<Access.AccessNotices>().NotifyAsync(access.CaseId, Access.AccessSignals.VerdictOf(access.Verdict)!.Value,
                        privacy.Unmask(a.Reason), source, ct);
                    log.Outcome = notified.Sent > 0 || notified.Waiting ? "done" : notified.HeldBack > 0 ? "held-back" : "failed";
                    log.Detail = CaseBook.Cut(notified.Detail, 1000);
                    log.OutboxId = notified.FirstOutboxId;
                    break;
                }
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

    /// <summary>
    /// A note on a host or sensor (docs/cookie-object-notes-plan.md §3.2): a row of Cookie's own, shown on the object's page, never
    /// the people's notes; within the day's limit.
    /// </summary>
    private async Task ObjectNoteAsync(PnmtdDbContext db, CookieDecisionEntity log, Guid id, string body, CancellationToken ct)
    {
        var limit = (await settings.LimitsAsync(ct)).ObjectNotesPerDay;
        var today = Clock.ToUtc(Clock.LocalNow.Date);
        if (await db.CookieDecisions.CountAsync(d => d.Kind == "object-note" && d.Outcome == "done" && d.At >= today, ct) >= limit)
        {
            log.Outcome = "held-back";
            log.Detail = $"Notes on hosts and sensors: {limit} a day.";
            return;
        }
        var name = (await CookieObjectNotes.ObjectAsync(db, id, ct))?.Name ?? id.ToString();
        if (log.Id == 0)
        {
            // The note names its decision: the log row needs its id first.
            await db.SaveChangesAsync(ct);
        }
        await CookieObjectNotes.AddAsync(db, id, body, log.CaseId, log.Id, log.Areas, ct);
        log.Outcome = "done";
        log.Detail = $"Noted on {name}.";
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
