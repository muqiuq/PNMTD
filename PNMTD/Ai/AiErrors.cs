using PNMTD.Models.Db;

namespace PNMTD.Ai;

/// <summary>
/// A request that did not give a usable answer (docs/ai-operations-plan.md §3.5). <see cref="Retryable"/>: retried on
/// the schedule; otherwise the work fails for good (HTTP 400: PNMT's own bug) and is notified at once.
/// </summary>
public class AiRequestException(AiOutcome outcome, string message, bool retryable = true, int? status = null) : Exception(message)
{
    public AiOutcome Outcome { get; } = outcome;

    public bool Retryable { get; } = retryable;

    public int? Status { get; } = status;
}

/// <summary>The budget is spent: the work waits for the next period, without counting an attempt (§3.7).</summary>
public sealed class AiBudgetExceededException(DateTime until) : Exception($"The AI budget is spent until {until:yyyy-MM-dd HH:mm} UTC.")
{
    public DateTime Until { get; } = until;
}

/// <summary>AI is off, has no key, or the guard a task needs is not configured: the work fails for good.</summary>
public sealed class AiNotConfiguredException(string message) : Exception(message);
