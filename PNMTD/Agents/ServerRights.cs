using System.Text.Json;
using PNMTD.Lib.Agent;

namespace PNMTD.Agents;

/// <summary>
/// What a server's own configuration allows PNMT (docs/linux-sensor-plan.md §4.6), as its hello announces it
/// (<see cref="AgentRightsSummary"/>): bounded, kept on the agent row, and worded for the agent log as the agent words it
/// ("key removal allowed for deploy, www; scripts off"). A refused config.yaml grants nothing, and its heartbeats write 461.
/// </summary>
public static class ServerRights
{
    /// <summary><see cref="AgentRightsSummary.Source"/> of a file that grants nothing: unsafe or broken.</summary>
    public const string Refused = "refused";

    public static AgentRightsSummary? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            return AgentJson.Deserialize<AgentRightsSummary>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsRefused(AgentRightsSummary? rights) => rights is not null && string.Equals(rights.Source, Refused, StringComparison.OrdinalIgnoreCase);

    /// <summary>The hello's summary, its texts and lists capped, as it is stored.</summary>
    public static AgentRightsSummary Bounded(AgentRightsSummary rights)
    {
        static IReadOnlyList<string> List(IReadOnlyList<string>? values, int max = 50) =>
            (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Take(max).Select(v => Cuts.Cut(v.Trim(), 200)).ToList();
        return new AgentRightsSummary(Cuts.Cut(rights.Source ?? "", 20), Cuts.CutOrNull(rights.Problem, 500), rights.KeyRemoval, List(rights.KeyRemovalUsers),
            Math.Max(0, rights.ProtectedKeys), List(rights.WritablePaths), rights.Scripts, List(rights.ScriptRunAs), Math.Max(0, rights.AllowedScriptHashes),
            Math.Clamp(rights.MaxScriptTimeoutMinutes, 0, 24 * 60), rights.SudoCommands, rights.CronCommands,
            rights.Warnings is { Count: > 0 } warnings ? List(warnings, 20) : null);
    }

    /// <summary>
    /// One line, as the agent's own log and <c>pnmt-agent config check</c> say it: "key removal allowed for deploy, www (2 protected
    /// keys); scripts off"; a refused file: "config.yaml refused (not owned by root): everything off".
    /// </summary>
    public static string Describe(AgentRightsSummary rights)
    {
        if (IsRefused(rights))
        {
            return $"config.yaml refused{(rights.Problem is { } problem ? $" ({problem})" : "")}: everything off";
        }
        var keys = !rights.KeyRemoval ? "key removal off"
            : $"key removal allowed for {(rights.KeyRemovalUsers.Count == 0 ? "every account" : string.Join(", ", rights.KeyRemovalUsers))}"
              + (rights.ProtectedKeys > 0 ? $" ({rights.ProtectedKeys} protected keys)" : "");
        var scripts = !rights.Scripts ? "scripts off"
            : $"scripts as {(rights.ScriptRunAs.Count == 0 ? "no account" : string.Join(", ", rights.ScriptRunAs))}"
              + (rights.AllowedScriptHashes > 0 ? $", only {rights.AllowedScriptHashes} allowed scripts" : ", any script of PNMT's library")
              + $", at most {rights.MaxScriptTimeoutMinutes} min";
        var collect = new List<string>();
        if (rights.SudoCommands)
        {
            collect.Add("sudo's command lines");
        }
        if (rights.CronCommands)
        {
            collect.Add("crontab lines in full");
        }
        var defaults = string.Equals(rights.Source, "defaults", StringComparison.OrdinalIgnoreCase) ? " (no config.yaml: the defaults)" : "";
        return $"{keys}; {scripts}" + (collect.Count > 0 ? $"; collects {string.Join(" and ", collect)}" : "") + defaults;
    }
}
