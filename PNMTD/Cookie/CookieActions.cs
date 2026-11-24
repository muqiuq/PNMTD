using System.ComponentModel;
using System.Text.Json;

namespace PNMTD.Cookie;

/// <summary>
/// The closed list of Cookie's actions (docs/cookie-plan.md §6): every one only adds. <see cref="AccessNotice"/>: telling the access
/// watch's contacts about an unusual login (docs/cookie-access-plan.md §6.4); its recipients come from the settings, never the model.
/// <see cref="LoungeEmail"/>: a light e-mail from the lounge (docs/lounge-plan.md §8.2), with a level and limits of its own.
/// </summary>
public enum CookieActionKind { Pushover, Email, Ticket, PrivateNote, PublicAnswer, ObjectNote, MailEvent, HandToPerson, AccessNotice, LoungeEmail, AwayNotice, BoardMessage }

/// <summary>
/// A proposal at the end of a case's work (§21.1): the action, its targets as ids from the brief or the tools (never an
/// address or a name), the text, why, on what evidence, how sure. Code checks every part of it.
/// </summary>
public sealed record ProposedAction(
    [property: Description("pushover, email, ticket, private-note, public-answer, object-note, board-message or hand-to-person")] CookieActionKind Action,
    [property: Description("For pushover and email: the person's user id from your data; empty otherwise")] string? UserId,
    [property: Description("For private-note and public-answer: the ticket's number; 0 otherwise")] int IssueId,
    [property: Description("For object-note: the host's or sensor's id; for board-message: the board's id from BoardsYouMayMessage; empty otherwise")] string? ObjectId,
    [property: Description("A subject, or the ticket's title; at most 120 characters")] string Subject,
    [property: Description("The text as it is to be sent or written; at most 1500 characters")] string Text,
    [property: Description("Why, at most 300 characters")] string Reason,
    [property: Description("The ids of the evidence it rests on: cases, sensors, tickets, pages")] IReadOnlyList<string> Evidence,
    [property: Description("How sure you are, 0 to 1; 0.9 means wrong 1 time in 10")] double Confidence,
    [property: Description("info, important or urgent")] Urgency Urgency,
    [property: Description("For board-message: alert, warning, maintenance, information or success; empty otherwise")] string? BoardKind = null)
{
    public static string Name(CookieActionKind kind) => JsonNamingPolicy.KebabCaseLower.ConvertName(kind.ToString());
}

/// <summary>An <i>AI every time</i> mail's event (§12, §21.1): the rule's sensor and code, decided by Cookie.</summary>
public sealed record MailEventPayload(Guid MailId, Guid RuleId, Guid SensorId, int Code, string Message);

/// <summary>An access notice's case and verdict (docs/cookie-access-plan.md §6.4): code wrote both; the model only the reason.</summary>
public sealed record AccessNoticePayload(Guid CaseId, string Verdict, bool Strong);

/// <summary>
/// An away notice's comments (docs/cookie-redmine-tab-plan.md §6.4): code found them and names whom to tell; the model gave the verdict
/// and the reason only.
/// </summary>
public sealed record AwayNoticePayload(Guid CaseId, int IssueId, IReadOnlyList<long> CommentIds);

/// <summary>
/// What the decision log keeps of a proposal: the action, and for a mail its event, for an access notice its case and verdict, for a lounge
/// e-mail the voice that wrote it (docs/lounge-plan.md §8.2), for an away notice its comments.
/// </summary>
public sealed record CookieProposal(ProposedAction Action, MailEventPayload? Mail = null, AccessNoticePayload? Access = null, string? LoungeVoice = null,
    AwayNoticePayload? Away = null);

public enum SeraphVerdictKind { Approve, Revise, Reject, Ask }

/// <summary>Seraph's verdict on a proposal (docs/cookie-prompts.md §8).</summary>
public sealed record SeraphVerdict(
    [property: Description("approve, revise, reject or ask")] SeraphVerdictKind Verdict,
    [property: Description("One concrete reason, at most 300 characters")] string Reason,
    [property: Description("How sure you are of the verdict, 0 to 1; 0.95 means you would be wrong 1 time in 20")] double Confidence = 1);

/// <summary>
/// The actions an admin's empowered message asked for (docs/cookie-powers-plan.md §5): proposals as a case makes them, each through
/// the executor as an order.
/// </summary>
public sealed record OrderAnswer(
    [property: Description("The actions the message asked for, at most 5; empty when it asked for none")] IReadOnlyList<ProposedAction> Proposals);

/// <summary>Who ordered a proposal from the chat, with which message (docs/cookie-powers-plan.md §6): the executor passes the gates of judgement for it.</summary>
public sealed record CookieOrder(Guid UserId, string Name, long MessageId, string Text);

/// <summary>Seraph's daily drift report (§5).</summary>
public sealed record SeraphReport(
    [property: Description("What drifted, at most 5 points of one sentence each; empty when nothing did")] IReadOnlyList<string> Points,
    [property: Description("One sentence on the day as a whole")] string Summary);

/// <summary>Seraph's reply in the chat (§10.2): it explains, and can't act.</summary>
public sealed record SeraphReply([property: Description("The reply, at most 1200 characters")] string Reply);

/// <summary>Seraph's coffee sentence: how things are, from the reviewer's side (docs/adjustments1.md).</summary>
public sealed record SeraphCoffeeAnswer([property: Description(CoffeeAnswer.Description)] string Coffee);
