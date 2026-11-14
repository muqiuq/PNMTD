using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Collectors;
using PNMTD.Sensor.Windows.Resilience;

namespace PNMTD.Sensor.Windows.Scripts;

/// <summary>
/// <c>scripts</c> (docs/windows-sensor-plan.md §7.2): the scripts registered in the window, with their text and the parser's view
/// of it (once per text: the parser is a PowerShell process), their account, schedule and task, and whether PNMT may run
/// scripts here. Every 5 minutes for the tasks' last runs; the window asks for it at once after a change (collect-now).
/// </summary>
internal sealed class RegisteredScriptsCollector(ILogger<RegisteredScriptsCollector> logger) : Collector
{
    /// <summary>Longer texts are listed without their text: PNMT's library takes at most this much too.</summary>
    public const int MaxTextChars = 256 * 1024;

    public override string Name => SectionNames.Scripts;

    public override TimeSpan Interval => TimeSpan.FromMinutes(5);

    /// <summary>A parser run per new text, and the Task Scheduler.</summary>
    public override TimeSpan Timeout => TimeSpan.FromMinutes(3);

    private readonly ConcurrentDictionary<string, (ParsedScript? Parsed, string? Error)> parsed = new(StringComparer.OrdinalIgnoreCase);

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var policy = ScriptPolicy.Read();
        var scripts = LocalScripts.All();
        var tasks = scripts.Any(s => s.Schedule is not null)
            ? Isolated.Part(errors, "scheduled tasks", () => LocalScripts.TasksAsync(ct).GetAwaiter().GetResult()) ?? []
            : [];
        var registered = new List<RegisteredScript>();
        foreach (var script in scripts)
        {
            ct.ThrowIfCancellationRequested();
            string? text = null;
            var matches = false;
            try
            {
                if (File.Exists(script.File))
                {
                    var current = File.ReadAllText(script.File);
                    matches = ScriptHashes.Of(current) == script.Sha256;
                    text = matches && current.Length <= MaxTextChars ? current : null;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{script.Name}: {e.Message}");
            }
            var (parse, parseError) = text is null ? (null, null) : Parse(script.Sha256, text, ct);
            var task = tasks.FirstOrDefault(t => t.Name.Equals(script.Name, StringComparison.OrdinalIgnoreCase));
            registered.Add(new RegisteredScript(script.Name, script.Sha256, matches, script.Kind, script.Account, script.Schedule?.Describe(),
                task is null ? null : new ScheduledTaskState(task.State, task.LastRun, task.LastResult, task.NextRun),
                script.AddedAt, script.AddedBy, script.UpdatedAt, text, parse, parseError));
        }
        logger.LogDebug("Registered scripts: {Count} ({Matching} matching their hash), {Tasks} tasks, scripts from PNMT {Policy}", registered.Count,
            registered.Count(r => r.Matches), tasks.Count, policy.Enabled ? policy.AnyScript ? "any" : "registered only" : "off");
        return new ScriptsSection(policy.Enabled, policy.AnyScript, policy.RunAs, registered);
    }

    /// <summary>The parser's view of a text, once per hash; after a failure it is tried again on the next pass.</summary>
    private (ParsedScript? Parsed, string? Error) Parse(string hash, string text, CancellationToken ct)
    {
        if (parsed.TryGetValue(hash, out var known) && known.Parsed is not null)
        {
            return known;
        }
        try
        {
            var started = DateTimeOffset.UtcNow;
            var result = ScriptsFeature.ParseAsync(text, ct).GetAwaiter().GetResult();
            logger.LogDebug("Parsed the registered script {Hash} in {Ms:0} ms: {Parameters} parameters, {Errors} syntax errors", hash[..12],
                (DateTimeOffset.UtcNow - started).TotalMilliseconds, result.Parameters.Count, result.Errors.Count);
            return parsed[hash] = (result, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Parsing the registered script {Hash} failed", hash[..12]);
            return parsed[hash] = (null, e.Message);
        }
    }
}
