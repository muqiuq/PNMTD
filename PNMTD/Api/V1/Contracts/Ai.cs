using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// Admin → AI's settings (docs/ai-operations-plan.md §15): the configuration (never the key), the budget, the spending
/// and the queue. <c>Warning</c>: the main and verification models of one vendor, or a missing guard.
/// </summary>
public sealed record AiStatus(
    bool Enabled,
    bool KeySet,
    string BaseUrl,
    string MainModel,
    string VerifyModel,
    string GuardModel,
    string GuardUrl,
    bool GuardRequired,
    double GuardThreshold,
    string DataCollection,
    int LogEntries,
    IReadOnlyList<string> Warnings,
    AiBudgetStatus Budget,
    AiSpendingSummary Spending,
    AiQueueStatus Queue,
    bool MailEnabled);

/// <summary><c>Share</c>: spent / limit; null without a limit.</summary>
public sealed record AiBudgetStatus(decimal Usd, string Period, string PeriodKey, decimal Spent, double? Share, DateTime PeriodEndsAt);

public sealed record AiSpendingSummary(decimal Today, int RequestsToday, decimal Last30Days, int Requests30Days,
    IReadOnlyList<AiSpendingRow> ByPurpose, IReadOnlyList<AiSpendingRow> ByModel);

public sealed record AiSpendingRow(string Name, int Requests, decimal Cost);

public sealed record AiQueueStatus(int Queued, int Running, int WaitingRetry, int WaitingBudget, int Failed24Hours);

public sealed record AiRequestItem(
    long Id,
    DateTime At,
    string Purpose,
    AiRole Role,
    int Attempt,
    string ModelAsked,
    string? ModelServed,
    string? Provider,
    AiOutcome Outcome,
    int? HttpStatus,
    int DurationMs,
    int? PromptTokens,
    int? CompletionTokens,
    decimal? Cost,
    Guid? WorkId,
    string? Error);

/// <summary>A request with its raw bodies (§3.6), and the other requests of its work item.</summary>
public sealed record AiRequestDetail(
    AiRequestItem Item,
    string Request,
    string? Response,
    bool Truncated,
    string? PromptVersion,
    string? GenerationId,
    int? ReasoningTokens,
    int? CachedTokens,
    IReadOnlyList<AiRequestItem> SameWork);

/// <summary><c>GET /ai/requests</c>: newest first.</summary>
public sealed class AiRequestQuery : PageQuery
{
    public string? Purpose { get; set; }

    public AiRole? Role { get; set; }

    public string? Model { get; set; }

    public AiOutcome? Outcome { get; set; }

    public Guid? WorkId { get; set; }
}

public sealed record AiWorkItem(Guid Id, string Purpose, string? SubjectType, Guid? SubjectId, AiWorkState State, int Attempts,
    DateTime NextAttemptAt, string? LastError, DateTime CreatedAt, DateTime? FinishedAt);

/// <summary><c>GET /ai/work</c>: the open items first, then the newest.</summary>
public sealed class AiWorkQuery : PageQuery
{
    public AiWorkState? State { get; set; }

    public string? Purpose { get; set; }
}

/// <summary><c>POST /ai/mail/backfill</c> (§6.2): the unmatched mails of the last days; a dry run only counts them.</summary>
public sealed class MailBackfillRequest
{
    public int Days { get; init; } = 7;

    public bool DryRun { get; init; } = true;
}

/// <summary><c>Mails</c>: unmatched and not analysed; <c>Queued</c>: one request each; <c>Attached</c>: to a waiting decision.</summary>
public sealed record MailBackfillResult(int Mails, int Queued, int Attached);

/// <summary>Admin → Debug → Test the AI (§8.4): each model's answer to a fixed prompt.</summary>
public sealed record DebugAiResult(AiRole Role, string Model, bool Ok, string Detail, int DurationMs, decimal? Cost, string? ModelServed);

