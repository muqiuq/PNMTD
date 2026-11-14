using PNMTD.Cookie;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Cookie (docs/cookie-plan.md §19.4).

/// <summary>A case named by the status: its id and title.</summary>
public sealed record CookieCaseRef(Guid Id, string Title);

/// <summary>
/// The overall state (§8): Cookie's while it runs, the code's otherwise. A reader who can't see every case behind it gets
/// the state with a reason that names none of them.
/// </summary>
public sealed record OverallStateItem(OverallState State, string Reason, string Source, DateTime? At, IReadOnlyList<CookieCaseRef> Cases);

/// <summary>Cookie's own state (§4.1).</summary>
public sealed record CookieStateItem(bool Enabled, bool Running, CookieState State, string? Reason, DateTime? LastRoundAt, DateTime? NextRoundAt);

public sealed record CookieStatusItem(OverallStateItem Overall, CookieStateItem Cookie);

public sealed record CookieCaseQuery(bool? Open = true, CookieCaseKind? Kind = null, int Page = 1, int PageSize = 50);

public sealed record CookieCaseItem(
    Guid Id,
    CookieCaseKind Kind,
    bool Open,
    string Title,
    Importance Importance,
    DateTime OpenedAt,
    DateTime? ClosedAt,
    DateTime ChangedAt,
    DateTime ProgressAt,
    Guid? HostId,
    Guid? LocationId,
    int? IssueId,
    string? CookieNote,
    string? CookieUrgency,
    DateTime? NextDueAt,
    CookieDutyKind? NextDue);

public sealed record CookieEntryItem(DateTime At, string Kind, string Text, string Actor, string? ActorName);

public sealed record CookieDutyItem(long Id, CookieDutyKind Kind, string Key, DateTime DueAt, CookieDutyState State, DateTime? DoneAt, string? Outcome);

/// <summary>A ticket case's clock (§7.2).</summary>
public sealed record CookieTicketClock(
    int IssueId,
    string? Author,
    bool AuthorIsStaff,
    string? Assignee,
    string? Status,
    DateTime? CreatedAt,
    DateTime? FirstAnswerAt,
    DateTime? LastStaffUpdateAt,
    int? OwnIssueId,
    string? IssueUrl);

public sealed record CookieCaseDetail(
    CookieCaseItem Case,
    IReadOnlyList<CaseMember> Members,
    IReadOnlyList<CookieEntryItem> Entries,
    IReadOnlyList<CookieDutyItem> Duties,
    CookieTicketClock? Ticket,
    string? OwnIssueUrl);

/// <summary>Admin → Cookie → Overview (§9.3).</summary>
public sealed record CookieAdminOverview(
    CookieStatusItem Status,
    string CodeReason,
    OverallState CodeState,
    DateTime? CaseBookAt,
    DateTime? TicketsAt,
    string? CaseBookError,
    int OpenProblems,
    int OpenTickets,
    int DutiesDue,
    bool RedmineConfigured,
    bool RedmineWritable,
    bool AiUsable,
    IReadOnlyList<CookieDutyCount> Duties,
    int HeldBackToday,
    CookieBudgetItem Budget,
    bool HasGroup);

/// <summary>How the duties of one kind ended in the last 7 days.</summary>
public sealed record CookieDutyCount(CookieDutyKind Kind, bool On, int Due, int Done, int HeldBack, int Off, int Failed);

/// <summary>Every section of Admin → Cookie, as in force.</summary>
public sealed record CookieAllSettings(
    IdentitySettings Identity,
    PromptSettings Prompts,
    CaseSettings Cases,
    HoursSettings Hours,
    NotificationSettings Notifications,
    CookieRedmineSettings Redmine,
    ActionSettings Actions,
    ModelSettings Models,
    CookieRightsSettings Rights);

public sealed record CookieSaved(string Section, int Version);

/// <summary>The defaults Admin → Cookie offers: every prompt, every kind of case's instruction, the limits' ceilings.</summary>
public sealed record CookieDefaults(
    IReadOnlyList<CookiePromptItem> Prompts,
    IReadOnlyList<CookiePromptItem> CaseKinds,
    OutputLimits Ceilings,
    OutputLimits Limits);

public sealed record CookiePromptItem(string Key, string Label, string UsedFor, string Text, IReadOnlyList<string> Required);

/// <summary>What Admin → Cookie → Redmine chooses from: the projects PNMT shows, their roles and the statuses.</summary>
public sealed record CookieRedmineLookups(bool Configured, bool Writable, IReadOnlyList<RedmineNamed> Projects, IReadOnlyList<RedmineNamed> Roles,
    IReadOnlyList<RedmineNamed> Statuses, IReadOnlyList<RedmineNamed> Trackers, string? Error);

public sealed record CookieOutboxQuery(CookieOutboxState? State = null, CookieChannel? Channel = null, int Page = 1, int PageSize = 50);

/// <summary>A row of the outbox (§6.1). The address is never shown: only the person's name, or the ticket.</summary>
public sealed record CookieOutboxItem(
    long Id,
    DateTime CreatedAt,
    CookieChannel Channel,
    string Recipient,
    int? IssueId,
    string Subject,
    string Body,
    CookieOutboxState State,
    string? Detail,
    string Source,
    Guid? CaseId,
    DateTime? SentAt,
    string? SentByName);

/// <summary>A step of Cookie's trace (§9.2), placeholders restored.</summary>
public sealed record CookieTraceItem(long Id, long? RoundId, Guid? CaseId, DateTime At, string Kind, string Title, string Body, IReadOnlyList<string> Areas);

public sealed record CookieTraceQuery(Guid? CaseId = null, long? RoundId = null, string? Kind = null, long? AfterId = null, int Limit = 100);

/// <summary>The trace the reader may see: <see cref="Full"/> with reasoning and tool results, otherwise per case (§9.2).</summary>
public sealed record CookieTrace(IReadOnlyList<CookieTraceItem> Items, bool Full);

public sealed record CookieRoundItem(long Id, DateTime StartedAt, DateTime? EndedAt, string Trigger, string Outcome, int Cases, int Calls, int ToolCalls,
    decimal Cost, string? Error);

/// <summary>Cookie's budget this month (§19.1).</summary>
public sealed record CookieBudgetItem(decimal Limit, decimal Spent);

/// <summary>A prompt as the model receives it, with what code fills in at the time of the call shown in brackets.</summary>
public sealed record CookiePromptPreview(string Key, string Text);
