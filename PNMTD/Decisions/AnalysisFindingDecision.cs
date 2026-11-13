using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Decisions;

/// <summary>An analysis finding's payload (docs/ai-operations-plan.md §9.3).</summary>
public sealed record FindingPayload(FindingEvidence Finding, AiEvidence Ai);

/// <summary>
/// <c>analysis.finding</c> (§5.2, §9.3): a warning or critical finding of the situation analysis. A mute is the decision
/// itself: a finding with the same key is skipped for 7 days after a decision chose <c>mute</c>.
/// </summary>
public sealed class AnalysisFindingDecision : IDecisionKind
{
    public const string KindName = "analysis.finding";
    public static readonly TimeSpan MuteFor = TimeSpan.FromDays(7);

    public string Kind => KindName;

    public IReadOnlyList<DecisionOption> Options(DecisionEntity decision) =>
    [
        new("acknowledge", "Acknowledge", "Seen; it is being handled.", false, false, true),
        new("dismiss", "Dismiss", "Not a problem.", false, false, true),
        new("mute", "Mute similar for 7 days", "Findings with the same key are skipped for 7 days.", false, false, true),
    ];

    public Task<string?> ApplyAsync(DecisionEntity decision, string choice, DecideRequest request, DecisionContext context, CancellationToken ct) =>
        Task.FromResult<string?>(choice == "mute" ? $$"""{"mutedUntil":"{{Clock.UtcNow.Add(MuteFor):o}}"}""" : null);

    public Task<bool> StillRelevantAsync(DecisionEntity decision, PnmtdDbContext db, CancellationToken ct) => Task.FromResult(true);

    /// <summary>Whether a finding key was muted within the last 7 days.</summary>
    public static Task<bool> MutedAsync(PnmtdDbContext db, string dedupeKey, CancellationToken ct)
    {
        var since = Clock.UtcNow - MuteFor;
        return db.Decisions.AnyAsync(d => d.Kind == KindName && d.DedupeKey == dedupeKey && d.Choice == "mute" && d.DecidedAt >= since, ct);
    }
}
