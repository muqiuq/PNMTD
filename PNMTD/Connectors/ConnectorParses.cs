using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Connectors;

/// <summary>
/// A connector's text in the parse pipeline (docs/connectors-plan.md §3.3, §5.5, §10.6): a parse result reaches every connector whose
/// text has that hash, as it reaches library versions, and the connector's arguments follow the new parameters.
/// </summary>
public static class ConnectorParses
{
    /// <summary>Applies an agent's parse to the connectors' texts of that hash and language; how many it changed (not saved).</summary>
    public static async Task<int> ApplyAsync(PnmtdDbContext db, ScriptLanguage language, ParseResultMessage result, string agentName, CancellationToken ct)
    {
        var type = ConnectorTargets.TypeOf(language);
        var withFunctions = result.Parsed?.Functions is not null;
        var scripts = await db.ConnectorScripts
            .Where(s => s.Hash == result.Hash && (s.ParseState != ScriptParseState.Parsed || (withFunctions && !EF.Functions.JsonExists(s.Parsed!, "functions"))))
            .Join(db.Connectors.Where(c => c.Type == type), s => s.ConnectorId, c => c.Id, (s, _) => s).ToListAsync(ct);
        foreach (var script in scripts)
        {
            var parsed = language == ScriptLanguage.Bash
                ? AgentScriptResults.Capped(AgentScriptResults.Merge(BashScriptHeader.Parse(script.ScriptText), result.Parsed))
                : result.Parsed is null ? null : AgentScriptResults.Capped(result.Parsed);
            script.ParseState = result.Parsed is null ? ScriptParseState.Failed : ScriptParseState.Parsed;
            script.Parsed = parsed is null ? null : JsonSerializer.Serialize(parsed, AgentJson.Options);
            script.ParseError = Cuts.CutOrNull(result.Error, 1000);
            script.ParsedAt = Clock.UtcNow;
            if (parsed is not null && parsed.ParametersOf(script.Function) is { } parameters)
            {
                var rows = await db.ConnectorArguments.Where(a => a.ConnectorId == script.ConnectorId).ToListAsync(ct);
                var reconciled = ConnectorArguments.Reconcile(script.ConnectorId, rows, parameters);
                db.ConnectorArguments.RemoveRange(reconciled.Removed);
                db.ConnectorArguments.AddRange(reconciled.Added);
                script.NeedsReview = script.NeedsReview || reconciled.NeedsReview;
            }
        }
        return scripts.Count;
    }

    /// <summary>
    /// A text's parse at the save (§3.3): bash's header at once (the agent's syntax check joins it later); for PowerShell, a parse of the
    /// same text that exists already, in the library or another connector; else pending, for a connected agent.
    /// </summary>
    public static async Task ParseAtSaveAsync(PnmtdDbContext db, ConnectorType type, ConnectorScriptEntity script, CancellationToken ct)
    {
        script.Hash = ScriptHashes.Of(script.ScriptText);
        script.ParsedAt = Clock.UtcNow;
        script.ParseError = null;
        if (type == ConnectorType.LinuxScript)
        {
            script.Parsed = JsonSerializer.Serialize(AgentScriptResults.Capped(BashScriptHeader.Parse(script.ScriptText)), AgentJson.Options);
            script.ParseState = ScriptParseState.Pending;
            return;
        }
        var known = await db.AgentScriptVersions.AsNoTracking().Where(v => v.Hash == script.Hash && v.ParseState == ScriptParseState.Parsed && v.Parsed != null)
            .Join(db.AgentScripts.Where(s => s.Language == ScriptLanguage.PowerShell), v => v.ScriptId, s => s.Id, (v, _) => v.Parsed)
            .FirstOrDefaultAsync(ct)
            ?? await db.ConnectorScripts.AsNoTracking().Where(s => s.Hash == script.Hash && s.ConnectorId != script.ConnectorId && s.ParseState == ScriptParseState.Parsed && s.Parsed != null)
                .Join(db.Connectors.Where(c => c.Type == ConnectorType.WindowsScript), s => s.ConnectorId, c => c.Id, (s, _) => s.Parsed)
                .FirstOrDefaultAsync(ct);
        script.Parsed = known;
        script.ParseState = known is null ? ScriptParseState.Pending : ScriptParseState.Parsed;
    }
}
