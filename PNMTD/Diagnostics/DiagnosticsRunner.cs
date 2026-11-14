using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Diagnostics;

/// <summary>
/// Diagnostics results (docs/windows-sensor-plan.md §5.4): each run stored once (a manual run's row is waiting for it), and on
/// the assignment's DIAGNOSTICS sensor 252 "All 5 checks passed" or 452 "2 of 5 failed: ping gw-zurich (100 % loss), …".
/// The same outcome and message collapse by the dedupe.
/// </summary>
public sealed class DiagnosticsRunner(IDbContextFactory<PnmtdDbContext> dbs, ILogger<DiagnosticsRunner> logger)
{
    public const int MaxResultsPerRun = DiagnosticsRules.MaxChecks;

    public async Task ResultsAsync(AgentConnection connection, DiagnosticResultBatch batch, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        foreach (var result in batch.Runs.Take(1000))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var assignment = await db.DiagnosticAssignments.SingleOrDefaultAsync(a => a.Id == result.AssignmentId && a.TargetSensorId == connection.SensorId, ct);
            if (assignment is null)
            {
                logger.LogInformation("Agent {Name}: a result for an assignment it doesn't have ({Assignment})", connection.AgentName, result.AssignmentId);
                continue;
            }
            var run = await db.DiagnosticRuns.SingleOrDefaultAsync(r => r.Id == result.RunId, ct);
            if (run is not null && run.AssignmentId != assignment.Id)
            {
                continue;
            }
            if (run is null)
            {
                run = new DiagnosticRunEntity { Id = result.RunId, AssignmentId = assignment.Id, SensorId = assignment.SensorId, Trigger = Cuts.Cut(result.Trigger, 20) };
                db.DiagnosticRuns.Add(run);
            }
            var results = result.Results.Take(MaxResultsPerRun).Select(r => r with
            {
                Name = Cuts.Cut(r.Name, 200),
                Message = Cuts.Cut(r.Message, 500),
                Dns = r.Dns is { } dns ? dns with { Answers = dns.Answers.Take(20).Select(a => Cuts.Cut(a, 300)).ToList() } : null,
            }).ToList();
            var (outcome, summary, ok) = Summarize(results);
            run.StartedAt = result.StartedAt.UtcDateTime;
            run.FinishedAt = result.FinishedAt.UtcDateTime;
            run.Outcome = outcome;
            run.Summary = Cuts.Cut(summary, 1000);
            run.Results = JsonSerializer.Serialize(results, AgentJson.Options);
            var sensor = await db.Sensors.Include(s => s.Parent).SingleOrDefaultAsync(s => s.Id == assignment.SensorId, ct);
            if (sensor is { Enabled: true, Parent.Enabled: true })
            {
                await SensorEventWriter.WriteAsync(db, sensor.Id, ok ? PNMTStatusCodes.DIAGNOSTICS_OK : PNMTStatusCodes.DIAGNOSTICS_FAILED, run.Summary,
                    AgentPlatformInfo.Of(connection.Platform).EventSource, ct);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
    }

    /// <summary>The run's outcome and its message: every check passed, or which failed and why.</summary>
    public static (DiagnosticOutcome Outcome, string Summary, bool Ok) Summarize(IReadOnlyList<DiagnosticCheckResult> results)
    {
        if (results.Count == 0)
        {
            return (DiagnosticOutcome.Error, "The run had no checks", false);
        }
        var bad = results.Where(r => r.Outcome != DiagnosticOutcome.Pass).ToList();
        if (bad.Count == 0)
        {
            return (DiagnosticOutcome.Pass, results.Count == 1 ? "The check passed" : $"All {results.Count} checks passed", true);
        }
        var outcome = bad.Any(r => r.Outcome == DiagnosticOutcome.Fail) ? DiagnosticOutcome.Fail : DiagnosticOutcome.Error;
        var what = string.Join(", ", bad.Take(5).Select(r => $"{r.Type.ToString().ToLowerInvariant()} {r.Name} ({r.Message})"));
        return (outcome, $"{bad.Count} of {results.Count} failed: {what}{(bad.Count > 5 ? ", …" : "")}", false);
    }
}
