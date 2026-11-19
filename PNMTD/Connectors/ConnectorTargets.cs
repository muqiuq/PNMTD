using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Connectors;

/// <summary>A server of a connector's platform, as a call resolves the target's name (§3.3).</summary>
public sealed record ConnectorServer(Guid SensorId, string Name, string HostName, AgentPlatform Platform, string? EncryptionKey,
    IReadOnlyList<string> Capabilities, string? Rights, bool Online);

/// <summary>A warning about a connector's target (§3.3): what would refuse a call now. Computed, never stored; it doesn't block a save.</summary>
public sealed record ConnectorWarning(string Code, string Message);

/// <summary>
/// A script connector's target as text (docs/connectors-plan.md §3.3, D6): the server's name resolved to exactly one server of the
/// platform with an agent, the parse of the connector's own text, and the warnings that say what a server would refuse.
/// </summary>
public static class ConnectorTargets
{
    public static AgentPlatform PlatformOf(ConnectorType type) => type == ConnectorType.LinuxScript ? AgentPlatform.Linux : AgentPlatform.Windows;

    public static ScriptLanguage LanguageOf(ConnectorType type) => type == ConnectorType.LinuxScript ? ScriptLanguage.Bash : ScriptLanguage.PowerShell;

    public static ConnectorType TypeOf(ScriptLanguage language) => language == ScriptLanguage.Bash ? ConnectorType.LinuxScript : ConnectorType.WindowsScript;

    public static string Label(ConnectorType type) => type == ConnectorType.LinuxScript ? "Linux Server Script" : "Windows Server Script";

    /// <summary>The servers of a platform with an agent, with their name as PNMT shows it ("WEB01 / Windows") and whether they are online.</summary>
    public static async Task<IReadOnlyList<ConnectorServer>> ServersAsync(PnmtdDbContext db, AgentHub hub, AgentPlatform platform, CancellationToken ct)
    {
        var rows = await db.Agents.AsNoTracking().Where(a => a.Platform == platform && AgentPlatformInfo.SensorTypes.Contains(a.Sensor.Type) && a.PublicKey != null)
            .Select(a => new { a.SensorId, Host = a.Sensor.Parent.Name, Sensor = a.Sensor.Name, a.EncryptionKey, a.Capabilities, a.Rights })
            .ToListAsync(ct);
        return rows.Select(r =>
        {
            var connection = hub.Connection(r.SensorId);
            return new ConnectorServer(r.SensorId, $"{r.Host} / {r.Sensor}", r.Host, platform, r.EncryptionKey,
                connection?.Capabilities ?? r.Capabilities, r.Rights, connection is not null);
        }).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The servers the name finds, ignoring case: by the full name ("WEB01 / Windows"), else by the host's name alone.</summary>
    public static IReadOnlyList<ConnectorServer> Match(IReadOnlyList<ConnectorServer> servers, string name)
    {
        var wanted = name.Trim();
        var full = servers.Where(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return full.Count > 0 ? full : servers.Where(s => string.Equals(s.HostName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static ParsedScript? Parsed(ConnectorScriptEntity script) =>
        script.Parsed is null ? null : JsonSerializer.Deserialize<ParsedScript>(script.Parsed, AgentJson.Options);

    /// <summary>The parse a call can use: bash's header at once, PowerShell's once an agent parsed it. Null while pending.</summary>
    public static ParsedScript? Usable(ConnectorType type, ConnectorScriptEntity script)
    {
        var parsed = Parsed(script);
        if (parsed is null)
        {
            return type == ConnectorType.LinuxScript ? BashScriptHeader.Parse(script.ScriptText) : null;
        }
        return type == ConnectorType.LinuxScript || script.ParseState == ScriptParseState.Parsed ? parsed : null;
    }

    /// <summary>"Runs Clear-ShopCache in Shop-Tools.ps1 (revision 3) on WEB01 / Windows as svc-shop".</summary>
    public static string Sentence(ConnectorType type, ConnectorScriptEntity s) =>
        $"Runs {(s.Function is { } f ? $"{f} in " : "")}{s.Name} (revision {s.ScriptRevision}) on {s.Server} as {RunAsName(s.RunAsKind, s.Account, type == ConnectorType.LinuxScript)}";

    public static string RunAsName(RunAsKind kind, string? account, bool linux = false) => kind switch
    {
        RunAsKind.Service => linux ? "root (the service)" : "the service (LocalSystem)",
        RunAsKind.Gmsa => $"{account} (gMSA)",
        RunAsKind.User => account ?? "",
        _ => $"{account} (stored on the server)",
    };

    /// <summary>
    /// What would refuse a call now (§3.3): the name finding no server or several, the agent offline or without scripts, the server's
    /// own rights (config.yaml on Linux, the agent window's policy on Windows), the parse, the function, the arguments, the wait.
    /// </summary>
    public static async Task<IReadOnlyList<ConnectorWarning>> WarningsAsync(PnmtdDbContext db, AgentHub hub, ConnectorEntity connector,
        ConnectorScriptEntity script, IReadOnlyList<ConnectorServer>? servers, CancellationToken ct)
    {
        var warnings = new List<ConnectorWarning>();
        var linux = connector.Type == ConnectorType.LinuxScript;
        servers ??= await ServersAsync(db, hub, PlatformOf(connector.Type), ct);
        var found = Match(servers, script.Server);
        ConnectorServer? server = null;
        if (found.Count == 0)
        {
            warnings.Add(new("server_not_found", $"\"{script.Server}\" finds no {(linux ? "Linux" : "Windows")} server with an agent: calls are refused."));
        }
        else if (found.Count > 1)
        {
            warnings.Add(new("server_ambiguous", $"\"{script.Server}\" finds {found.Count} servers ({string.Join(", ", found.Select(s => s.Name))}): calls are refused. Use the full name."));
        }
        else
        {
            server = found[0];
            if (!server.Online)
            {
                warnings.Add(new("server_offline", $"{server.Name}'s agent is offline: calls wait in the queue until it connects."));
            }
            else if (!server.Capabilities.Contains(AgentCapabilities.Scripts))
            {
                warnings.Add(new("scripts_off", linux
                    ? $"Scripts are off on {server.Name}: \"scripts: enabled: true\" in /etc/pnmt-agent/config.yaml would allow them."
                    : $"Scripts are off on {server.Name}: an administrator turns them on in the agent's window there."));
            }
            if (linux)
            {
                if (AgentScriptService.LinuxRefusal(ServerRights.Read(server.Rights), server.Capabilities, server.Name,
                        script.RunAsKind == RunAsKind.Service ? "root" : script.Account, script.TimeoutSeconds) is { } refusal)
                {
                    warnings.Add(new("server_refuses", refusal));
                }
                else
                {
                    warnings.Add(new("allowed_hashes", $"If {server.Name}'s config.yaml lists allowed_hashes, this text's hash {script.Hash[..12]}… must be among them: the agent checks that, PNMT can't."));
                }
            }
            else if (await AgentSections.ReadAsync<ScriptsSection>(db, server.SensorId, SectionNames.Scripts, ct) is { } section)
            {
                if (WindowsRefusal(section, script, server.Name) is { } refusal)
                {
                    warnings.Add(new("server_refuses", refusal));
                }
            }
            if (script.Function is not null && server.Online && !server.Capabilities.Contains(AgentCapabilities.ScriptFunctions))
            {
                warnings.Add(new("agent_too_old", $"The agent on {server.Name} can't call functions: update it. Calls are refused until then."));
            }
        }
        var parsed = Usable(connector.Type, script);
        if (parsed is null)
        {
            warnings.Add(new("parameters_pending", script.ParseState == ScriptParseState.Failed
                ? $"No agent could parse the text{(script.ParseError is { } e ? $": {e}" : "")}."
                : "The text's parameters are pending: a connected Windows agent parses it. Calls wait in the queue until then."));
        }
        else
        {
            if (parsed.Errors.Count > 0)
            {
                warnings.Add(new("script_invalid", $"The text has errors: {parsed.Errors[0].Message} (line {parsed.Errors[0].Line}). Calls are refused."));
            }
            if (script.Function is { } function && parsed.Function(function) is null)
            {
                warnings.Add(new("function_missing", parsed.Functions is null
                    ? $"The text's parse lists no functions yet, so {function} can't be checked: an agent that lists functions parses it again."
                    : $"The text defines no function {function} at its top level: calls are refused."));
            }
        }
        if (script.NeedsReview)
        {
            warnings.Add(new("arguments_need_review", "The text changed, and an argument's choice no longer fits it: save the arguments again. Calls are refused until then."));
        }
        if (connector.Mode == ConnectorMode.Wait && script.TimeoutSeconds > connector.WaitSeconds)
        {
            warnings.Add(new("wait_shorter", $"Calls wait up to {connector.WaitSeconds} s, and a run may take {script.TimeoutSeconds} s: a call that runs longer is answered with its ticket."));
        }
        return warnings;
    }

    /// <summary>Why a Windows server's policy (the agent's window, §7.2) refuses the connector's text and account, as far as its section says.</summary>
    public static string? WindowsRefusal(ScriptsSection section, ConnectorScriptEntity script, string server)
    {
        if (!section.Allowed)
        {
            return $"Scripts from PNMT are off on {server}: an administrator turns them on in the agent's window there.";
        }
        var registered = section.Scripts.FirstOrDefault(s => s.Sha256 == script.Hash && s.Matches);
        if (registered is not null)
        {
            var same = registered.RunAs == RunAsKind.Service
                ? script.RunAsKind == RunAsKind.Service
                : script.RunAsKind != RunAsKind.Service && string.Equals(registered.Account, script.Account, StringComparison.OrdinalIgnoreCase);
            return same ? null : $"The text is registered on {server} as {RunAsName(registered.RunAs, registered.Account)}: it runs only as that account there.";
        }
        if (!section.AnyScript)
        {
            var changed = section.Scripts.FirstOrDefault(s => string.Equals(s.Name, script.Name, StringComparison.OrdinalIgnoreCase));
            return changed is not null
                ? $"{server} runs only registered scripts, and its {changed.Name} differs from this text: take it again with From the server…."
                : $"{server} runs only registered scripts, and this text isn't registered there: register it in the agent's window, or take a registered one with From the server….";
        }
        var name = script.RunAsKind == RunAsKind.Service ? "service" : script.Account ?? "";
        if (script.RunAsKind == RunAsKind.Service && !section.RunAs.Contains("service", StringComparer.OrdinalIgnoreCase))
        {
            return $"Running as the service itself must be allowed by name (\"service\") on {server}.";
        }
        if (section.RunAs.Count > 0 && !section.RunAs.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return $"{name} is not on {server}'s list of allowed accounts ({string.Join(", ", section.RunAs)}).";
        }
        return null;
    }
}
