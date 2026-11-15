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

/// <summary>
/// The status box (§9.1). <see cref="CookieSays"/> and <see cref="SeraphSays"/>: how things are, as one would say it over coffee
/// (docs/adjustments1.md), the box's first lines; only while Cookie's status is current, and only for readers who may see every
/// case behind it.
/// </summary>
public sealed record CookieStatusItem(OverallStateItem Overall, CookieStateItem Cookie, CookieSaysItem? CookieSays = null, CookieSaysItem? SeraphSays = null);

/// <summary>A coffee sentence and when it was said.</summary>
public sealed record CookieSaysItem(string Text, DateTime At);

public sealed record CookieCaseQuery(bool? Open = null, CookieCaseKind? Kind = null, int Page = 1, int PageSize = 50);

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
    string? HostName,
    string? LocationName,
    string? CookieNote,
    string? CookieUrgency,
    CookieNextDuty? Next,
    CookieStop? Stopped = null);

/// <summary>A person stopped the case (§4.3): who, when, why.</summary>
public sealed record CookieStop(DateTime At, string? By, string? Comment);

/// <summary><c>POST cases/{id}/comments</c>, <c>…/stop</c> (the comment is required), <c>…/go-on</c> (optional).</summary>
public sealed record CookieCaseCommentRequest(string? Text);

/// <summary>A case's next deadline.</summary>
public sealed record CookieNextDuty(CookieDutyKind Kind, DateTime DueAt);

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
    string? OwnIssueUrl,
    bool CanComment = false,
    bool CanStop = false,
    CookieSolutionItem? Solution = null,
    DateTime? BigBrotherAskedAt = null,
    bool CanAskBigBrother = false);

/// <summary>The big brother's last solution on a case (§15.4).</summary>
public sealed record CookieSolutionItem(string Text, DateTime At, string? Model);

/// <summary>
/// An entry of Cookie's decision log (§11, §21.2): the proposal and what came of it, Seraph's verdict, and its rating. Shown
/// only to readers who have all the areas of its sources.
/// </summary>
public sealed record CookieDecisionItem(
    long Id,
    DateTime At,
    Guid? CaseId,
    string? CaseTitle,
    string Kind,
    string Title,
    string? Text,
    string? Reason,
    double? Confidence,
    string? Verdict,
    string? VerdictReason,
    string Outcome,
    string? Detail,
    string? Model,
    Guid? InboxDecisionId,
    string? Rating,
    string? RatingComment,
    string? RatedByName,
    bool CanRate);

public sealed class CookieDecisionQuery : PageQuery
{
    public Guid? CaseId { get; init; }

    public string? Kind { get; init; }

    public string? Outcome { get; init; }
}

/// <summary><c>PUT decisions/{id}/rating</c>: right, wrong, or null to take it back; a comment says why.</summary>
public sealed record CookieRatingRequest(string? Rating, string? Comment);

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
    bool HasGroup,
    CookieActingItem? Acting = null);

/// <summary>
/// What Cookie did today (§21.1): proposals waiting in the inbox, done, rejected by Seraph, declined by people, held back;
/// whether Seraph has a model; the big brother's budget.
/// </summary>
public sealed record CookieActingItem(int Waiting, int DoneToday, int RejectedToday, int DeclinedToday, int HeldBackToday, bool SeraphConfigured,
    CookieBudgetItem BigBrother, int ActiveInstructions, int Lessons);

/// <summary>How the duties of one kind ended in the last 7 days.</summary>
public sealed record CookieDutyCount(CookieDutyKind Kind, bool On, int Due, int Done, int HeldBack, int Off, int Failed);

/// <summary>Every section of Admin → Cookie, as in force.</summary>
public sealed record CookieAllSettings(
    IdentitySettings Identity,
    PromptSettings Prompts,
    CaseSettings Cases,
    CookieRedmineSettings Redmine,
    ActionSettings Actions,
    ModelSettings Models,
    CookieRightsSettings Rights,
    LessonSettings Lessons,
    PNMTD.Cookie.Access.AccessSettings Access);

public sealed record CookieSaved(string Section, int Version);

/// <summary>The defaults Admin → Cookie offers: every prompt, every kind of case's instruction, the limits' ceilings.</summary>
public sealed record CookieDefaults(
    IReadOnlyList<CookiePromptItem> Prompts,
    IReadOnlyList<CookiePromptItem> CaseKinds,
    OutputLimits Ceilings,
    OutputLimits Limits,
    WatchedProject NewProject,
    CookieEnvDefaults Env);

/// <summary>What .env sets (§13.3, §14.3), in force where Admin → Cookie → Models leaves a field empty.</summary>
public sealed record CookieEnvDefaults(bool Enabled, string Privacy, string Model, string Providers, bool Zdr, string MinQuantization, decimal BudgetUsd,
    string SeraphModel = "", string SeraphProviders = "", string BigBrotherPrimary = "", string BigBrotherBackup = "", decimal BigBrotherBudgetUsd = 0);

/// <summary>Who Admin → Cookie can name: users and groups, for Cookie: Manage, which may not open Admin → Users.</summary>
public sealed record CookiePeople(IReadOnlyList<CookiePerson> Users, IReadOnlyList<CookieGroupRef> Groups);

public sealed record CookiePerson(Guid Id, string Username, string DisplayName, bool IsActive);

public sealed record CookieGroupRef(Guid Id, string Name);

public sealed record CookiePromptItem(string Key, string Label, string UsedFor, string Text, IReadOnlyList<string> Required);

/// <summary>What Admin → Cookie → Redmine chooses from: the projects PNMT shows, their roles and the statuses.</summary>
public sealed record CookieRedmineLookups(bool Configured, bool Writable, IReadOnlyList<RedmineNamed> Projects, IReadOnlyList<RedmineNamed> Roles,
    IReadOnlyList<RedmineNamed> Statuses, IReadOnlyList<RedmineNamed> Trackers, string? Error, string? RolesError);

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
public sealed record CookieTraceItem(long Id, long? RoundId, Guid? CaseId, string? CaseTitle, DateTime At, string Kind, string Title, string Body,
    IReadOnlyList<string> Areas);

public sealed record CookieTraceQuery(Guid? CaseId = null, long? RoundId = null, string? Kind = null, long? AfterId = null, int Limit = 100);

/// <summary>The trace the reader may see: <see cref="Full"/> with reasoning and tool results, otherwise per case (§9.2).</summary>
public sealed record CookieTrace(IReadOnlyList<CookieTraceItem> Items, bool Full);

public sealed record CookieRoundItem(long Id, DateTime StartedAt, DateTime? EndedAt, string Trigger, string Outcome, int Cases, int Calls, int ToolCalls,
    decimal Cost, string? Error);

/// <summary>Cookie's budget this month (§19.1).</summary>
public sealed record CookieBudgetItem(decimal Limit, decimal Spent);

/// <summary>A prompt as the model receives it, with what code fills in at the time of the call shown in brackets.</summary>
public sealed record CookiePromptPreview(string Key, string Text);

/// <summary>A message of the reader's chat with Cookie (§10.2), placeholders restored.</summary>
/// <summary>
/// A message of the chat. <see cref="Source"/>: <c>ui</c> or <c>mcp</c> (docs/mcp-plan.md §8.5), <see cref="ReadOnly"/> through a
/// read-only token, <see cref="RefusedWhy"/> when Jev refused it before Cookie saw it.
/// </summary>
public sealed record CookieMessageItem(long Id, DateTime At, string From, string Text, bool Answered, string To = "cookie",
    CookieInstructionItem? Instruction = null, string Source = "ui", bool ReadOnly = false, string? RefusedWhy = null);

/// <summary>Mute, suppress or an instruction (§10.3, §21.1): a draft from the chat until a person confirms it.</summary>
public sealed record CookieInstructionItem(
    Guid Id,
    string Kind,
    string Scope,
    string? Value,
    string? Label,
    string Text,
    DateTime Until,
    bool Confirmed,
    DateTime CreatedAt,
    string CreatedByName,
    string Source,
    DateTime? EndedAt,
    string? EndedByName,
    bool Active);

/// <summary><c>POST admin/instructions</c>: a new one, confirmed at once.</summary>
public sealed record CookieInstructionRequest(string Kind, string Scope, string? Value, string? Label, string Text, DateTime Until);

/// <summary>The reader's thread: the newest messages, whether one waits for a reply, and Cookie's state.</summary>
public sealed record CookieThread(IReadOnlyList<CookieMessageItem> Messages, bool Waiting, CookieState State);

/// <summary>A message to Cookie, or to Seraph (§10.2, §21.1).</summary>
public sealed record CookieMessageRequest(string Text, string? To = null);
