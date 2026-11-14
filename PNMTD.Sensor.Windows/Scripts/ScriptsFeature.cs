using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Service;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Scripts;

/// <summary>
/// Scripts on the agent (docs/windows-sensor-plan.md §7): <c>parse-script</c> always (nothing is run for it), <c>run-script</c> only
/// where the window allowed it. Before a run: the signature (the session checked it), the local allowlists, the hash, a fresh
/// parse and the arguments against it. The script and its arguments go to a folder only SYSTEM can read; the wrapper splats them.
/// </summary>
internal sealed class ScriptsFeature(ProblemReporter problems, ILogger<ScriptsFeature> logger) : IAgentFeature
{
    public string Name => "scripts";

    public IReadOnlyList<string> Capabilities =>
        ScriptPolicy.Read().Enabled ? [AgentCapabilities.Parse, AgentCapabilities.Scripts] : [AgentCapabilities.Parse];

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
                var parse = AgentJson.Deserialize<ParseScriptRequest>(request.Body)!;
                ParsedScript? parsed = null;
                string? error = null;
                try
                {
                    parsed = await ParseAsync(parse.Text, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    error = e.Message;
                    problems.Report("scripts", "parsing", e);
                }
                await session.SendAsync(AgentMessageTypes.ParseResult, new ParseResultMessage(request.RequestId, parse.Hash, parsed, error), ct);
                return new Ack(request.RequestId, error is null, error);
            case AgentMessageTypes.RunScript:
                var run = AgentJson.Deserialize<RunScriptRequest>(request.Body)!;
                var refusal = await CheckAsync(run, ct);
                if (refusal is not null)
                {
                    logger.LogWarning("Script {Script} refused: {Reason}", run.ScriptName, refusal);
                    return new Ack(request.RequestId, false, refusal);
                }
                _ = Task.Run(() => RunAsync(session, run), CancellationToken.None);
                return new Ack(request.RequestId, true);
            default:
                return null;
        }
    }

    private static async Task<string?> CheckAsync(RunScriptRequest run, CancellationToken ct)
    {
        if (ScriptPolicy.Read().Refuses(run.Hash, run.RunAs) is { } refused)
        {
            return refused;
        }
        if (ScriptHashes.Of(run.Text) != run.Hash)
        {
            return "The script's text doesn't match its hash.";
        }
        var kind = EffectiveKind(run);
        if (kind == RunAsKind.Stored && run.RunAs.Password is null && (run.RunAs.Account is null || StoredAccounts.Password(run.RunAs.Account) is null))
        {
            return $"No password for {run.RunAs.Account} is stored on this server (the agent's window: Scripts, Stored accounts).";
        }
        var parsed = await ParseAsync(run.Text, ct);
        if (parsed.Errors.Count > 0)
        {
            return $"The script has syntax errors: {parsed.Errors[0].Message}";
        }
        var problems = ScriptArguments.Check(parsed, run.Arguments, run.SecureArguments.Keys.ToList()).ToList();
        return problems.Count == 0 ? null : $"The arguments don't match the script: {string.Join("; ", problems.Select(p => p.Message))}";
    }

    /// <summary>A script registered here runs as the kind it is registered with (its password stored here), whatever PNMT named.</summary>
    private static RunAsKind EffectiveKind(RunScriptRequest run) =>
        run.RunAs.Kind == RunAsKind.TypedPerRun && run.RunAs.Password is not null ? RunAsKind.TypedPerRun : LocalScripts.ByHash(run.Hash)?.Kind ?? run.RunAs.Kind;

    public static async Task<ParsedScript> ParseAsync(string text, CancellationToken ct)
    {
        var folder = Folder($"parse-{Guid.NewGuid():N}");
        try
        {
            var script = Path.Combine(folder, "script.ps1");
            await File.WriteAllTextAsync(script, text, new System.Text.UTF8Encoding(true), ct);
            var parser = Path.Combine(folder, "parser.ps1");
            await File.WriteAllTextAsync(parser, PowerShell.Parser, new System.Text.UTF8Encoding(true), ct);
            var (code, output, errors) = await PowerShell.RunFileAsync("powershell", parser, ["-Path", script], null, TimeSpan.FromSeconds(60), ct);
            if (code != 0 || string.IsNullOrWhiteSpace(output))
            {
                throw new InvalidOperationException($"The parser failed ({code}): {errors.Trim()}");
            }
            // Base64-encoded UTF-8: Windows PowerShell writes the console in the OEM code page.
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[^1]));
            return AgentJson.Deserialize<ParsedScript>(json) ?? throw new InvalidOperationException("The parser answered nothing.");
        }
        finally
        {
            Delete(folder);
        }
    }

    private async Task RunAsync(AgentSession session, RunScriptRequest run)
    {
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var folder = Folder(run.RunId.ToString("N"));
        ScriptResultMessage result;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "script.ps1"), run.Text, new System.Text.UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(folder, "wrapper.ps1"), PowerShell.Wrapper, new System.Text.UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(folder, "args.json"), JsonSerializer.Serialize(run.Arguments));
            string? password = null;
            if (run.SecureArguments.Count > 0 || run.RunAs.Password is not null)
            {
                using var key = AgentKey.EncryptionKey(session.AgentId);
                if (run.SecureArguments.Count > 0)
                {
                    var secure = run.SecureArguments.ToDictionary(p => p.Key, p => AgentSecrets.Decrypt(key, p.Value, run.RunId));
                    await File.WriteAllTextAsync(Path.Combine(folder, "secure.json"), JsonSerializer.Serialize(secure));
                }
                password = run.RunAs.Password is { } secret ? AgentSecrets.Decrypt(key, secret, run.RunId) : null;
            }
            var kind = EffectiveKind(run);
            if (kind == RunAsKind.Stored && password is null)
            {
                password = StoredAccounts.Password(run.RunAs.Account!);
            }
            var timeout = TimeSpan.FromSeconds(Math.Clamp(run.TimeoutSeconds, 10, 3600));
            int exitCode;
            if (kind == RunAsKind.Service)
            {
                (exitCode, _, var errors) = await PowerShell.RunFileAsync(run.Shell, Path.Combine(folder, "wrapper.ps1"), ["-Folder", folder], null, timeout, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(errors))
                {
                    await File.AppendAllTextAsync(Path.Combine(folder, "output.txt"), errors);
                }
            }
            else
            {
                var input = JsonSerializer.Serialize(new
                {
                    folder,
                    wrapper = Path.Combine(folder, "wrapper.ps1"),
                    shell = PowerShell.Executable(run.Shell),
                    account = run.RunAs.Account,
                    password,
                    kind = kind.ToString(),
                    timeout = (int)timeout.TotalSeconds,
                    runId = run.RunId.ToString("N"),
                });
                var runner = Path.Combine(folder, "runner.ps1");
                await File.WriteAllTextAsync(runner, PowerShell.TaskRunner, new System.Text.UTF8Encoding(true));
                var (code, output, errors) = await PowerShell.RunFileAsync("powershell", runner, [],
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(input)), timeout + TimeSpan.FromSeconds(60), CancellationToken.None);
                if (code != 0)
                {
                    throw new InvalidOperationException($"The scheduled task could not run as {run.RunAs.Account}: {errors.Trim()}");
                }
                exitCode = int.TryParse(output.Trim().Split('\n').LastOrDefault()?.Trim(), out var last) ? last : -1;
            }
            password = null;
            var outputFile = Path.Combine(folder, "output.txt");
            var text = File.Exists(outputFile) ? await File.ReadAllTextAsync(outputFile) : "";
            if (int.TryParse(File.Exists(Path.Combine(folder, "exitcode.txt")) ? (await File.ReadAllTextAsync(Path.Combine(folder, "exitcode.txt"))).Trim() : null, out var written))
            {
                exitCode = written;
            }
            if (text.Length > 1024 * 1024)
            {
                text = text[..(1024 * 1024)] + "\n… (cut at 1 MiB)";
            }
            result = new ScriptResultMessage(run.RunId, ScriptRunState.Done, exitCode, text, null, watch.Elapsed.TotalMilliseconds, started, DateTimeOffset.UtcNow);
        }
        catch (TimeoutException e)
        {
            result = new ScriptResultMessage(run.RunId, ScriptRunState.TimedOut, null, null, e.Message, watch.Elapsed.TotalMilliseconds, started, DateTimeOffset.UtcNow);
        }
        catch (Exception e)
        {
            problems.Report("scripts", $"running {run.ScriptName}", e);
            result = new ScriptResultMessage(run.RunId, ScriptRunState.Failed, null, null, $"{e.GetType().Name}: {e.Message}", watch.Elapsed.TotalMilliseconds,
                started, DateTimeOffset.UtcNow);
        }
        finally
        {
            Delete(folder);
        }
        logger.LogInformation("Script {Script} as {Account}: {State}, exit code {Code}", run.ScriptName, run.RunAs.Account ?? "the service", result.State, result.ExitCode);
        Program.WriteEventLog($"PNMT ran the script {run.ScriptName} ({run.Hash[..12]}) as {run.RunAs.Account ?? "LocalSystem"}: {result.State}, exit code {result.ExitCode}.",
            result.State == ScriptRunState.Done ? EventLogEntryType.Information : EventLogEntryType.Warning);
        try
        {
            await session.SendAsync(AgentMessageTypes.ScriptResult, result, CancellationToken.None);
        }
        catch (Exception e)
        {
            problems.Report("scripts", "sending a result", e);
        }
    }

    /// <summary>A run's folder under state\runs, for SYSTEM and Administrators only.</summary>
    private static string Folder(string name)
    {
        var folder = Path.Combine(AgentInfo.StateFolder, "runs", name);
        var info = Directory.CreateDirectory(folder);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        info.SetAccessControl(security);
        return folder;
    }

    private static void Delete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
