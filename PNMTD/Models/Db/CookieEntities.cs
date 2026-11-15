using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Lib.Models.Enum;

namespace PNMTD.Models.Db;

/// <summary>Cookie's own state (docs/cookie-plan.md §4.1).</summary>
public enum CookieState { Stopped, Sleeping, Working, Paused }

/// <summary>The overall state on the dashboard (docs/cookie-plan.md §8).</summary>
public enum OverallState { AllFine, Info, Warning, Alert }

/// <summary>What a case is about (docs/cookie-plan.md §4.3, §19.1).</summary>
public enum CookieCaseKind { Problem, Ticket, Request }

/// <summary>A deadline's step (docs/cookie-plan.md §7, §19.1).</summary>
public enum CookieDutyKind { FirstAnswerReminder, FirstAnswerMissed, UpdateReminder, UpdateMissed, ProblemTicket, ClosingNote }

/// <summary>What came of a duty: still due, done, held back by a limit, switched off, not possible, failed, or stopped with its case.</summary>
public enum CookieDutyState { Due, Done, HeldBack, Off, NotPossible, Failed, Stopped }

/// <summary>How something left PNMT (docs/cookie-plan.md §6.1).</summary>
public enum CookieChannel { Email, Pushover, RedmineNote, RedminePrivateNote, RedmineTicket }

public enum CookieOutboxState { Sent, HeldBack, Failed, Simulated }

/// <summary>
/// One version of one section of Admin → Cookie (docs/cookie-plan.md §9.3, §19.2): the newest is in force, a rollback
/// saves an old one again.
/// </summary>
[Table("cookie_settings")]
[NotAudited("every version is kept here; each save is in the audit log as CookieSettingsChanged")]
public class CookieSettingEntity
{
    [Key]
    public long Id { get; set; }

    [MaxLength(32)]
    public string Section { get; set; } = "";

    public int Version { get; set; }

    /// <summary>The section as JSON.</summary>
    public string Json { get; set; } = "{}";

    public DateTime ChangedAt { get; set; }

    public Guid? ChangedById { get; set; }

    [MaxLength(200)]
    public string ChangedByName { get; set; } = "";

    /// <summary>"Rolled back to version 3".</summary>
    [MaxLength(300)]
    public string? Note { get; set; }
}

/// <summary>Cookie's running state and the two statuses (docs/cookie-plan.md §4.1, §8), in one row.</summary>
[Table("cookie_runtime")]
[NotAudited("Cookie's running state; starting and stopping it are in the audit log")]
public class CookieRuntimeEntity
{
    public const int Id = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int SingletonId { get; set; } = Id;

    /// <summary>An admin started it; it resumes after a restart (§9.3).</summary>
    public bool Running { get; set; }

    public DateTime? StartedAt { get; set; }

    [MaxLength(200)]
    public string? StartedBy { get; set; }

    public DateTime? StoppedAt { get; set; }

    [MaxLength(200)]
    public string? StoppedBy { get; set; }

    public CookieState State { get; set; } = CookieState.Stopped;

    [MaxLength(500)]
    public string? StateReason { get; set; }

    public DateTime? LastRoundAt { get; set; }

    public DateTime? NextRoundAt { get; set; }

    /// <summary>The state code computes from sensors, cases and deadlines (§3.4 A).</summary>
    public OverallState CodeStatus { get; set; }

    [MaxLength(500)]
    public string CodeReason { get; set; } = "";

    public List<Guid> CodeCases { get; set; } = [];

    public DateTime? CodeStatusAt { get; set; }

    /// <summary>Cookie's state, which replaces the code's while Cookie is running (§8).</summary>
    public OverallState? CookieStatus { get; set; }

    [MaxLength(500)]
    public string? CookieReason { get; set; }

    public List<Guid> CookieCases { get; set; } = [];

    public DateTime? CookieStatusAt { get; set; }

    /// <summary>
    /// How things are, in Cookie's words, as one would say it over coffee (docs/adjustments1.md): written with the status, the
    /// first line of Cookie's status box.
    /// </summary>
    [MaxLength(300)]
    public string? CookieCoffee { get; set; }

    /// <summary>The same in Seraph's words, the reviewer's view; asked when the status changes, at most every 30 minutes.</summary>
    [MaxLength(300)]
    public string? SeraphCoffee { get; set; }

    public DateTime? SeraphCoffeeAt { get; set; }

    /// <summary>The case book's last run, and the last time it read the watched tickets.</summary>
    public DateTime? CaseBookAt { get; set; }

    public DateTime? TicketsAt { get; set; }

    [MaxLength(500)]
    public string? CaseBookError { get; set; }

    /// <summary>The last round's brief, hashed: the same brief again is a quiet round, with no call (§3.3).</summary>
    [MaxLength(64)]
    public string? BriefHash { get; set; }

    /// <summary>Rounds that failed in a row; at 3, Cookie pauses for a while.</summary>
    public int Failures { get; set; }

    public DateTime? PausedUntil { get; set; }
}

/// <summary>
/// A case (docs/cookie-plan.md §4.3, §19.1): a host's problems, a watched ticket, or a person's request. Code opens and
/// closes it.
/// </summary>
[Table("cookie_cases")]
[NotAudited("Cookie's case book, kept by code from the monitoring state and the watched tickets")]
public class CookieCaseEntity
{
    [Key]
    public Guid Id { get; set; }

    public CookieCaseKind Kind { get; set; }

    /// <summary>"host:{id}", "zabbix:{hostid}", "ticket:{number}", "request:{id}": one open case per key.</summary>
    [MaxLength(100)]
    public string Key { get; set; } = "";

    public bool Open { get; set; } = true;

    [MaxLength(300)]
    public string Title { get; set; } = "";

    public Guid? HostId { get; set; }

    public Guid? LocationId { get; set; }

    public long? ZabbixHostId { get; set; }

    /// <summary>The ticket of a ticket case.</summary>
    public int? IssueId { get; set; }

    public int? ProjectId { get; set; }

    public Importance Importance { get; set; } = Importance.Normal;

    public DateTime OpenedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    /// <summary>The last change Cookie should know of: a member, progress, a ticket's update.</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>The last progress (§7.1); the opening when there was none.</summary>
    public DateTime ProgressAt { get; set; }

    /// <summary>A problem case's members as JSON: its bad sensors and Zabbix problems.</summary>
    public string Members { get; set; } = "[]";

    /// <summary>The notes of the host and the members' sensors when last looked at: a change is progress.</summary>
    [MaxLength(64)]
    public string? NotesHash { get; set; }

    // A ticket case's clock (§7.2).
    public int? AuthorId { get; set; }

    [MaxLength(200)]
    public string? AuthorName { get; set; }

    public bool AuthorIsStaff { get; set; }

    public int? AssigneeId { get; set; }

    [MaxLength(200)]
    public string? AssigneeName { get; set; }

    public int? StatusId { get; set; }

    [MaxLength(100)]
    public string? StatusName { get; set; }

    public DateTime? TicketCreatedAt { get; set; }

    /// <summary>Redmine's updated_on when its journals were last read.</summary>
    public DateTime? TicketUpdatedOn { get; set; }

    public DateTime? FirstAnswerAt { get; set; }

    public DateTime? LastStaffUpdateAt { get; set; }

    /// <summary>The ticket opened for a problem case (§7.1).</summary>
    public int? OwnIssueId { get; set; }

    // Cookie (package nine, B).
    [MaxLength(2000)]
    public string? CookieNote { get; set; }

    [MaxLength(16)]
    public string? CookieUrgency { get; set; }

    public DateTime? CookieNoteAt { get; set; }

    /// <summary>The areas the note's sources came from: only readers with all of them see it (§9.2).</summary>
    public List<string> CookieNoteAreas { get; set; } = [];

    /// <summary>The areas its sources come from (Monitoring, Zabbix, Redmine): only readers with all of them see it (§9.2).</summary>
    public List<string> Areas { get; set; } = [];

    /// <summary>A request case's asker.</summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// A person stopped the case (docs/cookie-plan.md §4.3): Cookie leaves it alone, its duties stop, it raises the state to
    /// <i>Info</i> at most. It goes on when someone lets it, or, for a problem, when a new member joins.
    /// </summary>
    public DateTime? StoppedAt { get; set; }

    public Guid? StoppedById { get; set; }

    [MaxLength(200)]
    public string? StoppedByName { get; set; }

    [MaxLength(1000)]
    public string? StopComment { get; set; }

    /// <summary>The members when it was stopped: one that isn't among them lets it go on (§4.3).</summary>
    public List<string> StoppedMembers { get; set; } = [];

    /// <summary>A person asked the big brother (§15.2): the next round calls it.</summary>
    public DateTime? BigBrotherAskedAt { get; set; }

    [MaxLength(200)]
    public string? BigBrotherAskedBy { get; set; }

    /// <summary>The big brother's last solution (§15.4), placeholders restored, with the model that wrote it.</summary>
    public string? Solution { get; set; }

    public DateTime? SolutionAt { get; set; }

    [MaxLength(200)]
    public string? SolutionModel { get; set; }

    /// <summary>The areas the big brother's tools read: only readers with all of them see the solution (§9.2).</summary>
    public List<string> SolutionAreas { get; set; } = [];
}

/// <summary>
/// Cookie's decision log (docs/cookie-plan.md §11, §21.2): every proposal and what came of it, Seraph's verdict, and how
/// people rated it. Also the duties' texts, the mails' events, the big brother's calls and Seraph's drift reports.
/// </summary>
[Table("cookie_decisions")]
[NotAudited("Cookie's decision log; what it sends is in the outbox and the audit log, a rating is audited")]
public class CookieDecisionEntity
{
    [Key]
    public long Id { get; set; }

    public DateTime At { get; set; }

    public Guid? CaseId { get; set; }

    public long? RoundId { get; set; }

    /// <summary>The action (pushover, email, ticket, private-note, public-answer, object-note, mail-event, hand-to-person), or duty-text, big-brother, drift.</summary>
    [MaxLength(32)]
    public string Kind { get; set; } = "";

    [MaxLength(300)]
    public string Title { get; set; } = "";

    /// <summary>What it is about, for duplicates: user:{id}, issue:{n}, host:{id}, mail:{id}.</summary>
    [MaxLength(100)]
    public string? Target { get; set; }

    /// <summary>The proposal as JSON, placeholders as the model wrote them.</summary>
    public string Proposal { get; set; } = "{}";

    public double? Confidence { get; set; }

    /// <summary>approve, revise, reject or ask; null when Seraph wasn't asked.</summary>
    [MaxLength(16)]
    public string? Verdict { get; set; }

    [MaxLength(1000)]
    public string? VerdictReason { get; set; }

    /// <summary>done, proposed, approved, declined, rejected, off, below-threshold, refused, duplicate, muted, held-back, failed, noted.</summary>
    [MaxLength(32)]
    public string Outcome { get; set; } = "";

    [MaxLength(1000)]
    public string? Detail { get; set; }

    public Guid? InboxDecisionId { get; set; }

    public long? OutboxId { get; set; }

    [MaxLength(200)]
    public string? Model { get; set; }

    /// <summary>right or wrong (§11).</summary>
    [MaxLength(8)]
    public string? Rating { get; set; }

    [MaxLength(1000)]
    public string? RatingComment { get; set; }

    public Guid? RatedById { get; set; }

    [MaxLength(200)]
    public string? RatedByName { get; set; }

    public DateTime? RatedAt { get; set; }

    /// <summary>The lessons summariser has read the rating.</summary>
    public bool RatingRead { get; set; }

    public List<string> Areas { get; set; } = [];
}

/// <summary>
/// A temporary instruction (docs/cookie-plan.md §10.3, §21.1): <i>mute</i> a host, sensor, location or case; <i>suppress</i> a
/// channel, sensor type or kind of case; or a free-text <i>note</i>. Each ends, at most 30 days away. A draft from the chat
/// counts only once a person confirms it.
/// </summary>
[Table("cookie_instructions")]
[NotAudited("temporary instructions; creating, confirming and ending them is audited")]
public class CookieInstructionEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>mute, suppress or note.</summary>
    [MaxLength(16)]
    public string Kind { get; set; } = "";

    /// <summary>host, sensor, location or case (mute); channel, sensor-type or case-kind (suppress); empty for a note.</summary>
    [MaxLength(16)]
    public string Scope { get; set; } = "";

    [MaxLength(100)]
    public string? Value { get; set; }

    /// <summary>What the value names, for people: the host's name, "Pushover".</summary>
    [MaxLength(200)]
    public string? Label { get; set; }

    [MaxLength(1000)]
    public string Text { get; set; } = "";

    public DateTime Until { get; set; }

    public bool Confirmed { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedById { get; set; }

    [MaxLength(200)]
    public string CreatedByName { get; set; } = "";

    /// <summary>chat, admin or lesson.</summary>
    [MaxLength(16)]
    public string Source { get; set; } = "admin";

    public DateTime? EndedAt { get; set; }

    [MaxLength(200)]
    public string? EndedByName { get; set; }
}

/// <summary>A line of a case's timeline: what happened, when, by whom (code, Cookie, a person).</summary>
[Table("cookie_case_entries")]
[NotAudited("a case's timeline, written by code and Cookie")]
public class CookieCaseEntryEntity
{
    [Key]
    public long Id { get; set; }

    public Guid CaseId { get; set; }

    public DateTime At { get; set; }

    /// <summary>opened, bad, ok, progress, closed, ticket, reminder, escalation, note, held-back, failed, comment, stopped, go-on.</summary>
    [MaxLength(32)]
    public string Kind { get; set; } = "";

    [MaxLength(2000)]
    public string Text { get; set; } = "";

    /// <summary>code, cookie or person.</summary>
    [MaxLength(16)]
    public string Actor { get; set; } = "code";

    [MaxLength(200)]
    public string? ActorName { get; set; }
}

/// <summary>
/// A deadline's step for a case (docs/cookie-plan.md §7, §19.2), due at a time. Its key makes it happen once: "first-answer:reminder",
/// "update:missed@202611141030", "problem:ticket".
/// </summary>
[Table("cookie_duties")]
[NotAudited("deadlines computed by the case book; what they send is in the outbox and the audit log")]
public class CookieDutyEntity
{
    [Key]
    public long Id { get; set; }

    public Guid CaseId { get; set; }

    [MaxLength(64)]
    public string Key { get; set; } = "";

    public CookieDutyKind Kind { get; set; }

    public DateTime DueAt { get; set; }

    public CookieDutyState State { get; set; } = CookieDutyState.Due;

    public DateTime CreatedAt { get; set; }

    public DateTime? DoneAt { get; set; }

    [MaxLength(500)]
    public string? Outcome { get; set; }
}

/// <summary>
/// Everything that went out, or was held back by a limit (docs/cookie-plan.md §6.1, §19.1): a message, a note, a ticket.
/// The limits are counted here.
/// </summary>
[Table("cookie_outbox")]
[NotAudited("the outbox itself; what is sent is in the audit log as Cookie's")]
public class CookieOutboxEntity
{
    [Key]
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid? CaseId { get; set; }

    public long? DutyId { get; set; }

    public CookieChannel Channel { get; set; }

    /// <summary>The PNMT user a message is for.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Who, for people: a name; never shown as an address to anyone but admins.</summary>
    [MaxLength(300)]
    public string Recipient { get; set; } = "";

    /// <summary>The ticket a note is on, or the ticket created.</summary>
    public int? IssueId { get; set; }

    [MaxLength(300)]
    public string Subject { get; set; } = "";

    public string Body { get; set; } = "";

    public CookieOutboxState State { get; set; }

    [MaxLength(500)]
    public string? Detail { get; set; }

    /// <summary>code or cookie.</summary>
    [MaxLength(16)]
    public string Source { get; set; } = "code";

    public DateTime? SentAt { get; set; }

    public Guid? SentById { get; set; }

    [MaxLength(200)]
    public string? SentByName { get; set; }
}

/// <summary>One of Cookie's rounds (docs/cookie-plan.md §4.2): why it ran, what it did, what it cost.</summary>
[Table("cookie_rounds")]
[NotAudited("Cookie's rounds, for its window and Admin → Cookie")]
public class CookieRoundEntity
{
    [Key]
    public long Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    [MaxLength(300)]
    public string Trigger { get; set; } = "";

    /// <summary>quiet, worked, failed, stopped.</summary>
    [MaxLength(16)]
    public string Outcome { get; set; } = "";

    public int Cases { get; set; }

    public int Calls { get; set; }

    public int ToolCalls { get; set; }

    public decimal Cost { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }
}

/// <summary>
/// A step of a round, for Cookie's window (docs/cookie-plan.md §9.2): triage, reasoning, a tool call, a note, the status, an
/// error. Kept with the areas of its sources and its case, so that each reader sees what their rights allow.
/// </summary>
[Table("cookie_trace")]
[NotAudited("Cookie's trace, written by Cookie")]
public class CookieTraceEntity
{
    [Key]
    public long Id { get; set; }

    public long? RoundId { get; set; }

    public Guid? CaseId { get; set; }

    public DateTime At { get; set; }

    /// <summary>round, triage, reasoning, tool, note, status, summary, reply, error.</summary>
    [MaxLength(16)]
    public string Kind { get; set; } = "";

    [MaxLength(300)]
    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    public List<string> Areas { get; set; } = [];
}

/// <summary>A message in a person's chat with Cookie (docs/cookie-plan.md §10.2), and Cookie's replies.</summary>
[Table("cookie_messages")]
[NotAudited("a person's chat with Cookie")]
public class CookieMessageEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>The person whose thread it is.</summary>
    public Guid UserId { get; set; }

    public DateTime At { get; set; }

    /// <summary>person, cookie or seraph.</summary>
    [MaxLength(16)]
    public string From { get; set; } = "person";

    /// <summary>A person's message is to cookie or to seraph (docs/cookie-plan.md §21.1).</summary>
    [MaxLength(16)]
    public string To { get; set; } = "cookie";

    /// <summary>A reply's draft instruction, for the person to confirm (§10.3, §21.1).</summary>
    public Guid? InstructionId { get; set; }

    public string Text { get; set; } = "";

    /// <summary>A person's message waits for a reply until a round answers it.</summary>
    public bool Answered { get; set; }

    public Guid? CaseId { get; set; }

    public long? RoundId { get; set; }

    /// <summary>A reply's sources' areas (§9.2).</summary>
    public List<string> Areas { get; set; } = [];

    /// <summary>Where a person's message came from: <c>ui</c> (the chat) or <c>mcp</c> (docs/mcp-plan.md §8.5).</summary>
    [MaxLength(8)]
    public string Source { get; set; } = "ui";

    /// <summary>The MCP token it came through.</summary>
    public Guid? McpTokenId { get; set; }

    /// <summary>Through a read-only token: questions only, no instructions (docs/mcp-plan.md §8.3).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Jev refused it before Cookie saw it (docs/mcp-plan.md §8.2); never answered.</summary>
    [MaxLength(300)]
    public string? RefusedWhy { get; set; }
}

/// <summary>The privacy filter's mapping (docs/cookie-plan.md §13.2): a value and its stable placeholder, PERSON_1, EMAIL_2.</summary>
[Table("cookie_pseudonyms")]
[NotAudited("the privacy filter's mapping, kept by code")]
public class CookiePseudonymEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>PERSON, EMAIL or PHONE.</summary>
    [MaxLength(16)]
    public string Kind { get; set; } = "";

    /// <summary>The value as found, lower-cased: one placeholder per value.</summary>
    [MaxLength(300)]
    public string Value { get; set; } = "";

    /// <summary>The value as first found, for restoring it.</summary>
    [MaxLength(300)]
    public string Original { get; set; } = "";

    [MaxLength(32)]
    public string Placeholder { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}
