using PNMTD.Cookie;
using PNMTD.Cookie.Lounge;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// The lounge (docs/lounge-plan.md §14): one conversation for every signed-in user, and Admin → Cookie → Lounge.

/// <summary>
/// A line of the lounge, names restored. <see cref="From"/>: cookie, seraph, person or system. <see cref="Kind"/>: message or comment.
/// A removed one keeps its place with no text; a refused one is marked; a comment the voices haven't taken up yet has no <see cref="TakenById"/>.
/// </summary>
public sealed record LoungeMessageItem(
    long Id,
    DateTime At,
    string From,
    string Kind,
    Guid? UserId,
    string? Author,
    string? AuthorColor,
    string? AuthorCode,
    string Text,
    long? OnId,
    long? TakenById,
    long? ReplyToId,
    bool Refused,
    bool Removed,
    Guid? CaseId,
    string? CaseTitle,
    LoungeEmailItem? Email,
    bool Important = false);

/// <summary>What came of a lounge e-mail (§15.2): to whom, and waiting for a person, sent, or not.</summary>
public sealed record LoungeEmailItem(string To, string Outcome);

/// <summary>
/// The lounge's state for its readers (§3.3): stopped, paused (with why), asleep (until), awake, speaking (who is typing); whether the
/// reader may write now, and whether a person's message waits for its answer.
/// </summary>
public sealed record LoungeStateItem(bool Running, LoungeState State, string? Reason, string? Speaking, DateTime? NextAt, bool CanWrite, bool Waiting,
    DateTime? AwakeUntil);

public sealed record LoungeRoom(IReadOnlyList<LoungeMessageItem> Messages, LoungeStateItem State, string CookieName);

/// <summary><c>POST /api/v1/lounge/messages</c>: a message (the voices answer it) or a comment (they take it up), on a message or on none.</summary>
public sealed record LoungePostRequest(string? Text, string? Kind = null, long? OnId = null);

/// <summary>A keepsake as the room shows it (§7.4): what a voice will never forget.</summary>
public sealed record LoungeKeepsakeItem(long Id, string Text, DateTime Since);

/// <summary>A conviction with both positions, and its last move (§6).</summary>
public sealed record LoungeConvictionItem(string Key, string Question, string Minus, string Plus, string Owner, double Cookie, double Seraph,
    LoungeMoveItem? LastMove);

public sealed record LoungeMoveItem(DateTime At, string Voice, double Before, double After, string Why);

public sealed record LoungeAbout(IReadOnlyList<LoungeKeepsakeItem> Cookie, IReadOnlyList<LoungeKeepsakeItem> Seraph, IReadOnlyList<LoungeConvictionItem> Convictions,
    string CookieName);

/// <summary>One's own switches (§9.4): Cookie and Seraph may talk about one, and may e-mail one.</summary>
public sealed record LoungeSwitches(bool Mentions, bool Emails);

// --- Admin → Cookie → Lounge --------------------------------------------------------------------------------------------------

/// <summary>An area and level the lounge reads with, and the active users who lack it (§3.2): they still hear what the voices say about it.</summary>
public sealed record LoungeGapItem(string Area, string Level, int Lacking, IReadOnlyList<string> Users);

/// <summary>The tab's status (§14): running or stopped and by whom, the state, today's numbers, spend, the group's gap, who may write.</summary>
public sealed record LoungeAdminStatus(
    bool Enabled,
    bool Running,
    DateTime? StartedAt,
    string? StartedBy,
    DateTime? StoppedAt,
    string? StoppedBy,
    LoungeState State,
    string? Reason,
    string? Speaking,
    DateTime? NextAt,
    string? CantStart,
    int ScenesToday,
    int MessagesToday,
    int CommentsToday,
    int EmailsToday,
    int CasesToday,
    CookieBudgetItem Budget,
    string Rhythm,
    string? BudgetLasts,
    bool BudgetWarning,
    string? LastError,
    DateTime? LastErrorAt,
    IReadOnlyList<string> ReadsWith,
    IReadOnlyList<LoungeGapItem> Gap,
    int Users,
    IReadOnlyList<string> Writers);

public sealed record LoungeSceneItem(long Id, DateTime StartedAt, DateTime? EndedAt, string Kind, string Opener, string? Seed, bool Disagree, int PlannedLength,
    int Messages, int ToolCalls, decimal Cost, string Outcome);

public sealed record LoungeStepItem(long Id, long? MessageId, DateTime At, string Kind, string Title, string Body, IReadOnlyList<string> Areas);

public sealed record LoungeSceneDetail(LoungeSceneItem Scene, IReadOnlyList<LoungeStepItem> Steps, IReadOnlyList<LoungeMessageItem> Messages);

/// <summary>A memory with its strength now (§7), names restored, and the people it names.</summary>
public sealed record LoungeMemoryItem(long Id, string Voice, string Text, string Kind, bool Keepsake, double Strength, DateTime CreatedAt, DateTime RefreshedAt,
    int Recalls, long? SourceMessageId, IReadOnlyList<string> People);

/// <summary>A move of a position, for the tab's history (§6.2).</summary>
public sealed record LoungeOpinionItem(long Id, DateTime At, string Conviction, string Voice, double Before, double After, string Why);

public sealed record LoungeForgetRequest(string? Voice);
