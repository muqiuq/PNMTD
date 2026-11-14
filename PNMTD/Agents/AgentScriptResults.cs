using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Agents;

/// <summary>
/// The agents' side of scripts on PNMTD (docs/windows-sensor-plan.md §7.1, §7.3): parsing pending versions on an agent that can
/// (any connected Windows agent with parse.v1; never runs them), storing what the parser found, and the runs' results. The
/// library's scripts are PowerShell until bash comes with docs/linux-sensor-plan.md §8 (L7).
/// </summary>
public sealed class AgentScriptResults(IDbContextFactory<PnmtdDbContext> dbs, AgentKeyStore keys, AgentHub hub, ILogger<AgentScriptResults> logger)
{
    /// <summary>A run's output is cut here.</summary>
    public const int MaxOutputChars = 1024 * 1024;

    /// <summary>Sends pending versions (at most 20) to a connected agent that can parse them: PowerShell, on Windows.</summary>
    public async Task ParsePendingAsync(AgentConnection? connection, CancellationToken ct)
    {
        connection ??= hub.Connections.FirstOrDefault(CanParse);
        if (connection is null || !CanParse(connection))
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var pending = await db.AgentScriptVersions.AsNoTracking().Where(v => v.ParseState == ScriptParseState.Pending)
            .OrderBy(v => v.CreatedAt).Take(20).Select(v => new { v.Hash, v.Text }).ToListAsync(ct);
        foreach (var version in pending.DistinctBy(v => v.Hash))
        {
            try
            {
                var signed = await keys.SignAsync(AgentMessageTypes.ParseScript, connection.AgentId, new ParseScriptRequest(version.Hash, version.Text), ct,
                    AgentProtocol.RequestLifetime);
                await connection.SendAsync(AgentMessageTypes.ParseScript, signed, ct, AgentSigning.NewRequestId());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Asking {Agent} to parse a script failed", connection.AgentName);
            }
        }
    }

    private static bool CanParse(AgentConnection c) => c.Platform == AgentPlatform.Windows && c.Capabilities.Contains(AgentCapabilities.Parse);

    /// <summary>Whether a connected agent can parse the library's scripts now.</summary>
    public bool ParserAvailable => hub.Connections.Any(CanParse);

    public async Task ResultAsync(AgentConnection connection, string type, JsonElement body, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (type == AgentMessageTypes.ParseResult)
        {
            var result = AgentJson.Deserialize<ParseResultMessage>(body) ?? throw new JsonException("An empty parse result.");
            var versions = await db.AgentScriptVersions.Where(v => v.Hash == result.Hash && v.ParseState != ScriptParseState.Parsed).ToListAsync(ct);
            foreach (var version in versions)
            {
                version.ParseState = result.Parsed is null ? ScriptParseState.Failed : ScriptParseState.Parsed;
                version.Parsed = result.Parsed is null ? null : JsonSerializer.Serialize(Capped(result.Parsed), AgentJson.Options);
                version.ParseError = Cuts.CutOrNull(result.Error, 1000);
                version.ParsedAt = Clock.UtcNow;
                version.ParsedOn = Cuts.Cut(connection.AgentName, 500);
            }
            await db.SaveChangesAsync(ct);
            return;
        }
        var run = AgentJson.Deserialize<ScriptResultMessage>(body) ?? throw new JsonException("An empty script result.");
        var row = await db.AgentScriptRuns.SingleOrDefaultAsync(r => r.Id == run.RunId && r.SensorId == connection.SensorId, ct);
        if (row is null)
        {
            logger.LogInformation("Agent {Agent}: a result for an unknown run {Run}", connection.AgentName, run.RunId);
            return;
        }
        row.State = run.State;
        row.ExitCode = run.ExitCode;
        row.Output = run.Output is { Length: > MaxOutputChars } text ? text[..MaxOutputChars] + "\n… (cut at 1 MiB)" : run.Output;
        row.Message = Cuts.CutOrNull(run.Message, 2000);
        row.DurationMs = run.DurationMs;
        if (run.State is not ScriptRunState.Running)
        {
            row.FinishedAt = run.FinishedAt?.UtcDateTime ?? Clock.UtcNow;
        }
        AgentLog.Add(db, connection.SensorId, connection.AgentName, AgentLogKind.Request,
            $"Script {row.ScriptName} v{row.VersionNumber} as {row.RunAsName}: {run.State.ToString().ToLowerInvariant()}{(run.ExitCode is { } code ? $", exit code {code}" : "")}{(run.Message is { } m ? $": {Cuts.Cut(m, 300)}" : "")}",
            address: connection.Address);
        await db.SaveChangesAsync(ct);
    }

    private static ParsedScript Capped(ParsedScript parsed) => parsed with
    {
        Parameters = parsed.Parameters.Take(100).ToList(),
        Errors = parsed.Errors.Take(50).ToList(),
        Findings = parsed.Findings.Take(50).ToList(),
    };
}
