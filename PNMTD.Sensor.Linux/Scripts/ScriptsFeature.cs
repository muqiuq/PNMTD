using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Core.Service;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Scripts;

/// <summary>
/// Bash scripts on the connection (docs/linux-sensor-plan.md §8, the flow of W§7.1): <c>parse.v1</c> always (parsing runs nothing),
/// <c>scripts.v1</c> only while config.yaml's <c>scripts.enabled</c> is true (a change of the rights reconnects). <c>parse-script</c> answers
/// <c>parse-result</c> and the ack. <c>run-script</c> is checked (<see cref="BashScripts.Check"/>): refused, the ack says why and nothing runs;
/// accepted, the ack comes at once and <c>script-result</c> when the run ended. Each run is logged in the log file and the journal.
/// </summary>
internal sealed class ScriptsFeature(ProblemReporter problems, IEnumerable<IAgentRightsSource> rightsSources, ILogger<ScriptsFeature> logger) : IAgentFeature
{
    private readonly IAgentRightsSource? rights = rightsSources.FirstOrDefault();

    public string Name => "scripts";

    public IReadOnlyList<string> Capabilities =>
        rights?.Current.Scripts.Enabled == true ? [AgentCapabilities.Parse, AgentCapabilities.Scripts] : [AgentCapabilities.Parse];

    public void Configure(AgentConfig config)
    {
    }

    public Task RunAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ReportAsync(AgentSession session, CancellationToken ct) => Task.CompletedTask;

    public async Task<Ack?> HandleAsync(AgentSession session, SignedContent request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case AgentMessageTypes.ParseScript:
                var parse = AgentJson.Deserialize<ParseScriptRequest>(request.Body);
                if (parse is null)
                {
                    return new Ack(request.RequestId, false, "The request has no body.");
                }
                ParsedScript? parsed = null;
                string? error = null;
                try
                {
                    parsed = await Task.Run(() => BashScripts.Parse(parse.Text), ct);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    error = e.Message;
                    problems.Report("scripts", "parsing", e);
                }
                await session.SendAsync(AgentMessageTypes.ParseResult, new ParseResultMessage(request.RequestId, parse.Hash, parsed, error), ct);
                return new Ack(request.RequestId, error is null, error);
            case AgentMessageTypes.RunScript:
                var run = AgentJson.Deserialize<RunScriptRequest>(request.Body);
                if (run is null)
                {
                    return new Ack(request.RequestId, false, "The request has no body.");
                }
                var (checkedRun, refusal) = await Task.Run(() => BashScripts.Check(run, (rights?.Current ?? AgentRights.Defaults).Scripts), ct);
                if (checkedRun is null)
                {
                    logger.LogWarning("Script {Script} refused: {Reason}", run.ScriptName, refusal);
                    Journal.Write($"PNMT asked to run the script {run.ScriptName} ({Short(run.Hash)}): refused. {refusal}", warning: true);
                    return new Ack(request.RequestId, false, refusal);
                }
                var agentId = session.AgentId;
                _ = Task.Run(() => RunAsync(session, checkedRun, agentId), CancellationToken.None);
                return new Ack(request.RequestId, true);
            default:
                return null;
        }
    }

    private async Task RunAsync(AgentSession session, CheckedRun run, Guid agentId)
    {
        ScriptResultMessage result;
        try
        {
            using var key = run.Run.SecureArguments.Count > 0 ? LinuxPlatform.Instance.Keys.EncryptionKey(agentId) : null;
            // A run may take as long as config.yaml allows: on a thread of its own, not the pool's.
            result = await Task.Factory.StartNew(() => BashScripts.Run(run, secret => AgentSecrets.Decrypt(key!, secret, run.Run.RunId)), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch (Exception e)
        {
            problems.Report("scripts", $"running {run.Run.ScriptName}", e);
            result = new ScriptResultMessage(run.Run.RunId, ScriptRunState.Failed, null, null, $"{e.GetType().Name}: {e.Message}", null, null, DateTimeOffset.UtcNow);
        }
        Log(logger, run, result);
        try
        {
            await session.SendAsync(AgentMessageTypes.ScriptResult, result, CancellationToken.None);
        }
        catch (Exception e)
        {
            problems.Report("scripts", "sending a result", e);
        }
    }

    /// <summary>"PNMT ran the script rotate-logs (3f2a…) as deploy: exit 0", in the log file and the journal (§8.4).</summary>
    public static void Log(ILogger logger, CheckedRun run, ScriptResultMessage result)
    {
        var outcome = result.State switch
        {
            ScriptRunState.Done => $"exit {result.ExitCode}",
            ScriptRunState.TimedOut => "timed out",
            _ => $"failed: {result.Message}",
        };
        var text = $"PNMT ran the script {run.Run.ScriptName} ({Short(run.Run.Hash)}) as {run.Account.Name}: {outcome}"
                   + $" ({TimeSpan.FromMilliseconds(result.DurationMs ?? 0).TotalSeconds:0.#} s).";
        logger.LogInformation("{Message}", text);
        Journal.Write(text, warning: result.State != ScriptRunState.Done || result.ExitCode != 0);
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;
}
