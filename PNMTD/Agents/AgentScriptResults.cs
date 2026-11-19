using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Agents;

/// <summary>
/// The agents' side of scripts on PNMTD (docs/windows-sensor-plan.md §7.1, §7.3; docs/linux-sensor-plan.md §8.3): parsing pending
/// versions on an agent of their platform that can (parse.v1; never runs them): PowerShell on a Windows agent, bash's syntax check
/// (<c>bash -n</c>, shellcheck) on a Linux agent, merged into the header PNMTD read at the save; storing what the parser found, and
/// the runs' results.
/// </summary>
public sealed class AgentScriptResults(IDbContextFactory<PnmtdDbContext> dbs, AgentKeyStore keys, AgentHub hub, Connectors.ConnectorQueue connectorQueue,
    ILogger<AgentScriptResults> logger)
{
    /// <summary>A run's output is cut here.</summary>
    public const int MaxOutputChars = 1024 * 1024;

    /// <summary>The newest parses by hash and platform, for a text that isn't stored yet (a connector's form, docs/connectors-plan.md §11).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Hash, AgentPlatform Platform), ParsedScript> recent = new();

    /// <summary>A recent parse of a text by an agent of the platform, or null.</summary>
    public ParsedScript? Recent(string hash, AgentPlatform platform) => recent.TryGetValue((hash, platform), out var parsed) ? parsed : null;

    /// <summary>Asks a connected agent that can to parse one text that isn't stored (its result lands in <see cref="Recent"/>); false without one.</summary>
    public async Task<bool> ParseTextAsync(ScriptLanguage language, string text, CancellationToken ct)
    {
        var parser = hub.Connections.FirstOrDefault(c => CanParse(c, language));
        if (parser is null)
        {
            return false;
        }
        var signed = await keys.SignAsync(AgentMessageTypes.ParseScript, parser.AgentId, new ParseScriptRequest(ScriptHashes.Of(text), text), ct,
            AgentProtocol.RequestLifetime, session: parser.Session);
        await parser.SendAsync(AgentMessageTypes.ParseScript, signed, ct, AgentSigning.NewRequestId());
        return true;
    }

    /// <summary>
    /// Sends pending versions (at most 20 per language) to a connected agent that can parse them: the one given (its platform's
    /// language only), or for each language any connected agent of its platform.
    /// </summary>
    public async Task ParsePendingAsync(AgentConnection? connection, CancellationToken ct)
    {
        if (connection is not null && !connection.Capabilities.Contains(AgentCapabilities.Parse))
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        foreach (var language in new[] { ScriptLanguage.PowerShell, ScriptLanguage.Bash })
        {
            var parser = connection ?? hub.Connections.FirstOrDefault(c => CanParse(c, language));
            if (parser is null || !CanParse(parser, language))
            {
                continue;
            }
            var pending = await db.AgentScriptVersions.AsNoTracking().Where(v => v.ParseState == ScriptParseState.Pending)
                .Join(db.AgentScripts.Where(s => s.Language == language), v => v.ScriptId, s => s.Id, (v, _) => v)
                .OrderBy(v => v.CreatedAt).Take(20).Select(v => new { v.Hash, v.Text }).ToListAsync(ct);
            // docs/connectors-plan.md §3.3: a connector's own copy of a text is parsed like a library version.
            var type = Connectors.ConnectorTargets.TypeOf(language);
            var functions = parser.Capabilities.Contains(AgentCapabilities.ParseFunctions);
            pending.AddRange(await db.ConnectorScripts.AsNoTracking().Where(s => s.ParseState == ScriptParseState.Pending)
                .Join(db.Connectors.Where(c => c.Type == type), s => s.ConnectorId, c => c.Id, (s, _) => s)
                .OrderBy(s => s.ParsedAt).Take(20).Select(s => new { s.Hash, Text = s.ScriptText }).ToListAsync(ct));
            // §8.3: PowerShell versions parsed without functions, again by an agent that lists them.
            if (functions && language == ScriptLanguage.PowerShell)
            {
                pending.AddRange(await db.AgentScriptVersions.AsNoTracking()
                    .Where(v => v.ParseState == ScriptParseState.Parsed && v.Parsed != null && !EF.Functions.JsonExists(v.Parsed, "functions"))
                    .Join(db.AgentScripts.Where(s => s.Language == language), v => new { v.ScriptId, v.Number }, s => new { ScriptId = s.Id, Number = s.CurrentVersion }, (v, _) => v)
                    .Take(20).Select(v => new { v.Hash, v.Text }).ToListAsync(ct));
                pending.AddRange(await db.ConnectorScripts.AsNoTracking()
                    .Where(s => s.ParseState == ScriptParseState.Parsed && s.Parsed != null && !EF.Functions.JsonExists(s.Parsed, "functions"))
                    .Join(db.Connectors.Where(c => c.Type == type), s => s.ConnectorId, c => c.Id, (s, _) => s)
                    .Take(20).Select(s => new { s.Hash, Text = s.ScriptText }).ToListAsync(ct));
            }
            foreach (var version in pending.DistinctBy(v => v.Hash))
            {
                try
                {
                    var signed = await keys.SignAsync(AgentMessageTypes.ParseScript, parser.AgentId, new ParseScriptRequest(version.Hash, version.Text), ct,
                        AgentProtocol.RequestLifetime, session: parser.Session);
                    await parser.SendAsync(AgentMessageTypes.ParseScript, signed, ct, AgentSigning.NewRequestId());
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Asking {Agent} to parse a script failed", parser.AgentName);
                }
            }
        }
    }

    /// <summary>The platform whose agents parse a language: PowerShell on Windows, bash on Linux.</summary>
    public static AgentPlatform PlatformOf(ScriptLanguage language) => language == ScriptLanguage.Bash ? AgentPlatform.Linux : AgentPlatform.Windows;

    private static bool CanParse(AgentConnection c, ScriptLanguage language) => c.Platform == PlatformOf(language) && c.Capabilities.Contains(AgentCapabilities.Parse);

    /// <summary>Whether a connected agent can parse a language's scripts now.</summary>
    public bool IsParserAvailable(ScriptLanguage language) => hub.Connections.Any(c => CanParse(c, language));

    /// <summary>Whether a connected agent can parse PowerShell now.</summary>
    public bool ParserAvailable => IsParserAvailable(ScriptLanguage.PowerShell);

    public async Task ResultAsync(AgentConnection connection, string type, JsonElement body, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (type == AgentMessageTypes.ParseResult)
        {
            var result = AgentJson.Deserialize<ParseResultMessage>(body) ?? throw new JsonException("An empty parse result.");
            if (result.Parsed is not null)
            {
                if (recent.Count > 200)
                {
                    recent.Clear();
                }
                recent[(result.Hash, connection.Platform)] = Capped(result.Parsed);
            }
            // Only versions of the language this agent's platform parses: a Linux agent's parse never replaces a PowerShell one's.
            var language = connection.Platform == AgentPlatform.Linux ? ScriptLanguage.Bash : ScriptLanguage.PowerShell;
            // A result with functions replaces a parse without them (docs/connectors-plan.md §8.3).
            var withFunctions = result.Parsed?.Functions is not null;
            var versions = await db.AgentScriptVersions
                .Where(v => v.Hash == result.Hash && (v.ParseState != ScriptParseState.Parsed || (withFunctions && !EF.Functions.JsonExists(v.Parsed!, "functions"))))
                .Join(db.AgentScripts.Where(s => s.Language == language), v => v.ScriptId, s => s.Id, (v, _) => v).ToListAsync(ct);
            foreach (var version in versions)
            {
                version.ParseState = result.Parsed is null ? ScriptParseState.Failed : ScriptParseState.Parsed;
                version.Parsed = language == ScriptLanguage.Bash
                    // docs/linux-sensor-plan.md §8.3: PNMTD's header parse stays; the agent's syntax errors and findings join it.
                    ? JsonSerializer.Serialize(Capped(Merge(BashScriptHeader.Parse(version.Text), result.Parsed)), AgentJson.Options)
                    : result.Parsed is null ? null : JsonSerializer.Serialize(Capped(result.Parsed), AgentJson.Options);
                version.ParseError = Cuts.CutOrNull(result.Error, 1000);
                version.ParsedAt = Clock.UtcNow;
                version.ParsedOn = Cuts.Cut(connection.AgentName, 500);
            }
            if (await Connectors.ConnectorParses.ApplyAsync(db, language, result, connection.AgentName, ct) > 0)
            {
                await db.SaveChangesAsync(ct);
                connectorQueue.Nudge();
                return;
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
        // docs/connectors-plan.md §9.3: a connector's call may be final now, and a script's turn free.
        connectorQueue.Nudge();
    }

    /// <summary>
    /// A bash version's parse (docs/linux-sensor-plan.md §8.3): the parameters and help from PNMTD's header parse; its errors and
    /// findings with the agent's (<c>bash -n</c>'s errors, shellcheck's findings), each once.
    /// </summary>
    public static ParsedScript Merge(ParsedScript header, ParsedScript? agent) => agent is null ? header : header with
    {
        Errors = header.Errors.Concat(agent.Errors).DistinctBy(e => (e.Line, e.Message)).ToList(),
        Findings = header.Findings.Concat(agent.Findings).DistinctBy(f => (f.Rule, f.Line, f.Message)).ToList(),
    };

    public static ParsedScript Capped(ParsedScript parsed) => parsed with
    {
        Parameters = parsed.Parameters.Take(100).ToList(),
        Errors = parsed.Errors.Take(50).ToList(),
        Findings = parsed.Findings.Take(50).ToList(),
        Functions = parsed.Functions?.Take(50).Select(f => f with { Parameters = f.Parameters.Take(100).ToList() }).ToList(),
    };
}
