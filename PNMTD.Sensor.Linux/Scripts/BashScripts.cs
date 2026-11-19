using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Linux.Collectors;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Scripts;

/// <summary>A run that passed every check: who it runs as, the parse it was checked against, and how long it may take.</summary>
internal sealed record CheckedRun(RunScriptRequest Run, PasswdEntry Account, ParsedScript Parsed, TimeSpan Timeout);



/// <summary>
/// Bash scripts on the agent (docs/linux-sensor-plan.md §8.3–§8.5). <b>Parsing</b> runs nothing: the header (<see cref="BashScriptHeader.Parse"/>),
/// <c>bash -n</c> for syntax errors with their lines, and <c>shellcheck -f json</c> when installed (the first 50 findings), on the text in a
/// root-only temporary file. <b>Running</b>, after the checks of §8.3 and §8.5 (<see cref="Check"/>): the script in
/// <c>/var/lib/pnmt-agent/runs/&lt;id&gt;/script.sh</c> (the folder the run-as account's, 0700; the file 0400), the arguments as
/// <c>PNMT_&lt;NAME&gt;</c> in a root-only environment file next to it (secrets decrypted with the agent's ECDH key), and
/// <c>systemd-run --wait --pipe</c> in a transient unit of its own, as that account, with <c>RuntimeMaxSec</c>: PID 1 starts it, outside the
/// agent's sandbox, and at the timeout stops its whole tree. Output (at most 1 MiB, stdout and stderr as they came), exit code and time.
/// </summary>
internal static partial class BashScripts
{
    /// <summary>
    /// The runner for a function (docs/connectors-plan.md §8.2), 0400: sources the script, then calls the function named in the environment
    /// file. Sourcing runs the script's top level, which the header's guard (<c>BASH_SOURCE</c>) keeps from its main part.
    /// </summary>
    public const string FunctionRunner = "set -euo pipefail\nsource \"$1\"\n\"$PNMT__FUNCTION\"\n";

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z_][a-z0-9_]{0,39}$")]
    private static partial System.Text.RegularExpressions.Regex FunctionName();

    public const int MaxOutputChars = 1024 * 1024;
    public const int MaxFindings = 50;

    private static readonly string[] BusSockets = ["/run/dbus/system_bus_socket", "/var/run/dbus/system_bus_socket"];

    [GeneratedRegex(@": line (\d+): (.*)$")]
    private static partial Regex BashError();

    // --- parsing (§8.3) ---------------------------------------------------------------------------------------------

    public static ParsedScript Parse(string text)
    {
        var parsed = BashScriptHeader.Parse(text);
        var errors = parsed.Errors.ToList();
        var findings = parsed.Findings.ToList();
        var folder = Folder($"parse-{Guid.NewGuid():N}", null);
        try
        {
            var script = Path.Combine(folder, "script.sh");
            WriteFile(script, Normalized(text), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            errors.AddRange(SyntaxErrors(script));
            findings.AddRange(Shellcheck(script));
        }
        finally
        {
            Delete(folder);
        }
        return parsed with { Errors = errors, Findings = findings };
    }

    /// <summary><c>bash -n</c>: <c>script.sh: line 7: syntax error near unexpected token `fi'</c> → the message and line 7. Nothing is run.</summary>
    public static List<ScriptSyntaxError> SyntaxErrors(string script)
    {
        var result = Tool.Run("bash", ["-n", script], TimeSpan.FromSeconds(15));
        return result.Ok ? [] : ParseBashErrors(result.Errors.Length > 0 ? result.Errors : result.Output);
    }

    public static List<ScriptSyntaxError> ParseBashErrors(string text)
    {
        var errors = new List<ScriptSyntaxError>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(20))
        {
            errors.Add(BashError().Match(line) is { Success: true } m
                ? new ScriptSyntaxError(m.Groups[2].Value, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), 1)
                : new ScriptSyntaxError(line.Contains(": ", StringComparison.Ordinal) ? line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..] : line, 1, 1));
        }
        return errors.Count > 0 ? errors : [new ScriptSyntaxError("bash -n failed without saying why.", 1, 1)];
    }

    /// <summary><c>shellcheck -f json -s bash</c> when installed: the first 50 findings as <c>SC2086</c> and its message.</summary>
    public static List<ScriptFinding> Shellcheck(string script)
    {
        if (!Tool.Exists("shellcheck"))
        {
            return [];
        }
        var result = Tool.Run("shellcheck", ["-f", "json", "-s", "bash", script], TimeSpan.FromSeconds(30));
        return ParseShellcheck(result.Output);
    }

    public static List<ScriptFinding> ParseShellcheck(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.Trim().Length == 0 ? "[]" : json);
            var items = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement
                : document.RootElement.TryGetProperty("comments", out var comments) ? comments : default;
            if (items.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            return items.EnumerateArray().Take(MaxFindings).Select(e => new ScriptFinding(
                $"SC{(e.TryGetProperty("code", out var c) ? c.GetRawText() : "?")}",
                $"{(e.TryGetProperty("level", out var l) ? l.GetString() + ": " : "")}{(e.TryGetProperty("message", out var m) ? m.GetString() : "")}",
                e.TryGetProperty("line", out var line) && line.TryGetInt32(out var n) ? n : null)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // --- the checks (§8.3, §8.5) ------------------------------------------------------------------------------------

    /// <summary>
    /// The checks before a run, in order; the first that fails refuses with its reason: scripts enabled; bash; the account in <c>run_as</c>
    /// (root only when named); the hash in <c>allowed_hashes</c> when that isn't empty; the timeout within <c>max_timeout_minutes</c>; the text
    /// matching its hash; a fresh parse of the header without errors; the arguments against it; <c>bash -n</c>; the account existing; D-Bus.
    /// </summary>
    public static (CheckedRun? Run, string? Refusal) Check(RunScriptRequest run, ScriptRights rights)
    {
        if (!rights.Enabled)
        {
            return (null, "Scripts are off on this server (config.yaml: scripts.enabled: false).");
        }
        if (!string.IsNullOrEmpty(run.Shell) && !run.Shell.Equals("bash", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"This agent runs bash scripts only, not {run.Shell}.");
        }
        var account = run.RunAs.Kind switch
        {
            RunAsKind.Service => "root",
            RunAsKind.User => run.RunAs.Account,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(account))
        {
            return (null, run.RunAs.Kind == RunAsKind.User
                ? "A run as a local account needs the account's name."
                : $"Linux runs scripts as root (the service) or a local account, not as {run.RunAs.Kind}.");
        }
        if (!rights.RunAs.Contains(account, StringComparer.Ordinal))
        {
            return (null, account == "root"
                ? "config.yaml's scripts.run_as doesn't name root: root must be named to be allowed."
                : $"config.yaml's scripts.run_as doesn't name {account}" + (rights.RunAs.Count == 0 ? " (it names nobody)." : $" ({string.Join(", ", rights.RunAs)})."));
        }
        if (rights.AllowedHashes.Count > 0 && !rights.AllowedHashes.Contains(run.Hash.ToLowerInvariant(), StringComparer.Ordinal))
        {
            return (null, $"config.yaml's scripts.allowed_hashes doesn't list this script ({run.Hash[..Math.Min(12, run.Hash.Length)]}…).");
        }
        var maximum = TimeSpan.FromMinutes(rights.MaxTimeoutMinutes);
        var timeout = TimeSpan.FromSeconds(Math.Max(10, run.TimeoutSeconds));
        if (timeout > maximum)
        {
            return (null, $"The timeout of {timeout.TotalMinutes:0.#} min is above config.yaml's scripts.max_timeout_minutes ({rights.MaxTimeoutMinutes}).");
        }
        if (!ScriptHashes.Of(run.Text).Equals(run.Hash, StringComparison.OrdinalIgnoreCase))
        {
            return (null, "The script's text doesn't match its hash.");
        }
        var parsed = BashScriptHeader.Parse(run.Text);
        if (parsed.Errors.Count > 0)
        {
            return (null, $"The script's # pnmt: header has errors: line {parsed.Errors[0].Line}: {parsed.Errors[0].Message}");
        }
        // docs/connectors-plan.md §8.2: a function the header declares, by its name, checked against this agent's own parse.
        if (run.Function is { } function)
        {
            if (!FunctionName().IsMatch(function) || parsed.Function(function) is not { } declared || declared.Name != function)
            {
                return (null, $"The script's # pnmt: header declares no function {function}.");
            }
        }
        var problems = ScriptArguments.Check(parsed, run.Function, run.Arguments, run.SecureArguments.Keys.ToList()).ToList();
        if (problems.Count > 0)
        {
            return (null, $"The arguments don't match the {(run.Function is null ? "script" : "function")}: {string.Join("; ", problems.Select(p => p.Message))}");
        }
        var folder = Folder($"check-{Guid.NewGuid():N}", null);
        try
        {
            var script = Path.Combine(folder, "script.sh");
            WriteFile(script, Normalized(run.Text), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (SyntaxErrors(script) is { Count: > 0 } syntax)
            {
                return (null, $"bash -n: line {syntax[0].Line}: {syntax[0].Message}");
            }
        }
        finally
        {
            Delete(folder);
        }
        var user = Accounts.ParsePasswd(File.ReadAllText("/etc/passwd")).FirstOrDefault(u => u.Name == account);
        if (user is null)
        {
            return (null, $"There is no account {account} on this server.");
        }
        if (!Tool.Exists("systemd-run"))
        {
            return (null, "systemd-run is not installed: scripts run in transient systemd units.");
        }
        if (!BusSockets.Any(File.Exists))
        {
            return (null, "Scripts need D-Bus (systemd-run talks to systemd over it): apt install dbus.");
        }
        return (new CheckedRun(run, user, parsed, timeout), null);
    }

    // --- running (§8.4) ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The values of the environment file: each argument as text (booleans <c>true</c>/<c>false</c>), secrets decrypted, defaults for what
    /// wasn't given, <c>false</c> for a bool without one; an optional parameter without a default stays unset.
    /// </summary>
    public static Dictionary<string, string> Values(CheckedRun run, Func<EncryptedSecret, string> decrypt)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in run.Parsed.ParametersOf(run.Run.Function) ?? [])
        {
            var given = run.Run.Arguments.FirstOrDefault(a => a.Key.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            var secret = run.Run.SecureArguments.FirstOrDefault(a => a.Key.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (secret.Key is not null)
            {
                values[parameter.Name] = decrypt(secret.Value);
            }
            else if (given.Key is not null)
            {
                values[parameter.Name] = given.Value.ValueKind switch
                {
                    JsonValueKind.String => given.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => given.Value.GetRawText(),
                };
            }
            else if (parameter.Default is { } fallback)
            {
                values[parameter.Name] = fallback;
            }
            else if (parameter.IsSwitch)
            {
                values[parameter.Name] = "false";
            }
        }
        return values;
    }

    public static ScriptResultMessage Run(CheckedRun run, Func<EncryptedSecret, string> decrypt)
    {
        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var id = run.Run.RunId.ToString("N");
        var unit = $"pnmt-script-{id}";
        var account = run.Account;
        string? folder = null;
        var environmentFile = Path.Combine(AgentInfo.RunsFolder, $"{id}.env");
        ScriptResultMessage Result(ScriptRunState state, int? code, string? output, string? message) =>
            new(run.Run.RunId, state, code, output, message, watch.Elapsed.TotalMilliseconds, started, DateTimeOffset.UtcNow);
        try
        {
            folder = Folder(id, account);
            var script = Path.Combine(folder, "script.sh");
            WriteFile(script, Normalized(run.Run.Text), UnixFileMode.UserRead);
            if (account.Uid != 0)
            {
                Chown(script, account);
                Chown(folder, account);
            }
            var values = Values(run, decrypt);
            var program = script;
            var programArguments = new List<string>();
            if (run.Run.Function is { } function)
            {
                // docs/connectors-plan.md §8.2: a fixed runner sources the script and calls the function, whose name travels in the
                // environment file (PNMT__FUNCTION: parameters start with a letter, so it can't collide); file names only on the command line.
                values["_function"] = function;
                program = Path.Combine(folder, "run.sh");
                WriteFile(program, FunctionRunner, UnixFileMode.UserRead);
                if (account.Uid != 0)
                {
                    Chown(program, account);
                }
                programArguments.Add(script);
            }
            WriteFile(environmentFile, BashScriptHeader.EnvironmentFile(values), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var home = account.Home.Length > 0 && Directory.Exists(account.Home) ? account.Home : "/";
            var arguments = new List<string>
            {
                Tool.Find("systemd-run")!, $"--unit={unit}", $"--description=PNMT script {run.Run.ScriptName}", "--wait", "--pipe", "--collect", "--quiet",
            };
            if (account.Uid != 0)
            {
                arguments.Add($"--uid={account.Name}");
                arguments.Add($"--gid={account.Gid}");
            }
            arguments.AddRange([
                "-p", $"EnvironmentFile={environmentFile}", "-p", $"WorkingDirectory={home}", "-p", $"RuntimeMaxSec={(int)run.Timeout.TotalSeconds}",
                "-p", "TimeoutStopSec=10", "-p", "PrivateTmp=yes", "/bin/bash", program, .. programArguments,
            ]);
            ToolResult result;
            try
            {
                // stdout and stderr into one pipe, so the output reads as it came; only file names on the command line (§8.4).
                result = Tool.Run("sh", ["-c", "exec \"$@\" 2>&1", "sh", .. arguments], run.Timeout + TimeSpan.FromSeconds(60));
            }
            catch (TimeoutException)
            {
                Stop(unit);
                return Result(ScriptRunState.TimedOut, null, null, $"The run did not end within {Duration(run.Timeout)} and was stopped.");
            }
            var output = result.Output.Length > MaxOutputChars ? result.Output[..MaxOutputChars] + "\n… (cut at 1 MiB)" : result.Output;
            if (result.ExitCode != 0 && output.Contains("Failed to connect to bus", StringComparison.Ordinal))
            {
                return Result(ScriptRunState.Failed, null, output, "Scripts need D-Bus (systemd-run talks to systemd over it): apt install dbus.");
            }
            if (result.ExitCode != 0 && watch.Elapsed >= run.Timeout - TimeSpan.FromSeconds(1))
            {
                // At RuntimeMaxSec systemd stops the whole tree, and systemd-run's exit code is just 1 (L0): told apart by the time.
                return Result(ScriptRunState.TimedOut, result.ExitCode, output, $"Stopped after {Duration(run.Timeout)} (the timeout), with its processes.");
            }
            return Result(ScriptRunState.Done, result.ExitCode, output, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException
                                      or Core.Collectors.NotAvailableException)
        {
            return Result(ScriptRunState.Failed, null, null, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            TryDelete(environmentFile);
            if (folder is not null)
            {
                Delete(folder);
            }
        }
    }

    private static string Duration(TimeSpan time) => time.TotalSeconds < 120 ? $"{time.TotalSeconds:0} s" : $"{time.TotalMinutes:0.#} min";

    private static void Stop(string unit)
    {
        try
        {
            Tool.Run("systemctl", ["stop", unit], TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is TimeoutException or Core.Collectors.NotAvailableException)
        {
        }
    }

    // --- files --------------------------------------------------------------------------------------------------

    /// <summary>The text as bash should get it: without a BOM, with LF line endings (what the hash is of).</summary>
    public static string Normalized(string text) => text.TrimStart('\uFEFF').Replace("\r\n", "\n");

    /// <summary>
    /// A run's folder in /var/lib/pnmt-agent/runs (§8.4): root's and 0700 for a parse; the account's and 0700 for a run. The runs folder (and
    /// the data folder above it) can be passed through by others but not listed, so the account reaches its own folder and nothing else.
    /// </summary>
    private static string Folder(string name, PasswdEntry? owner)
    {
        LinuxPlatform.SecureFolder(AgentInfo.DataFolder, traverse: true);
        Directory.CreateDirectory(AgentInfo.RunsFolder);
        if (Native.Stat(AgentInfo.RunsFolder, followLinks: false) is { } facts && (facts.Uid != 0 || facts.Gid != 0))
        {
            Native.ChownToRoot(AgentInfo.RunsFolder);
        }
        File.SetUnixFileMode(AgentInfo.RunsFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        var folder = Path.Combine(AgentInfo.RunsFolder, name);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    private static void WriteFile(string path, string text, UnixFileMode mode)
    {
        using (var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            stream.Write(new UTF8Encoding(false).GetBytes(text));
        }
        File.SetUnixFileMode(path, mode);
    }

    private static void Chown(string path, PasswdEntry account)
    {
        var fd = Native.Open(path, Native.ReadOnly | Native.NoFollow);
        if (fd < 0)
        {
            throw new IOException($"{path}: {Native.Describe(Native.LastError)}");
        }
        try
        {
            Native.Fchown(fd, (uint)account.Uid, (uint)account.Gid);
        }
        finally
        {
            Native.Close(fd);
        }
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

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
