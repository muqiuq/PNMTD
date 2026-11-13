using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Windows.State;

/// <summary>
/// <c>agent.json</c> (docs/windows-sensor-plan.md §3.2): the URL, the agent's identity, PNMTD's pinned key, the configuration
/// last verified, and the log level. Written by the join; read by the service.
/// </summary>
internal sealed record AgentConfigFile(
    string Url,
    Guid AgentId,
    Guid SensorId,
    string SensorName,
    string HostName,
    string KeyName,
    string AgentKeyFingerprint,
    string PnmtdKey,
    string PnmtdFingerprint,
    DateTimeOffset JoinedAt,
    AgentConfig? Config,
    string? LogLevel = null,
    bool PlainHttp = false);

/// <summary>What the service tells the window, every 10 s (§3.2): a file, not a pipe, so there is no second interface to secure.</summary>
internal sealed record AgentStatusFile(
    DateTimeOffset UpdatedAt,
    string Version,
    bool Connected,
    DateTimeOffset? ConnectedSince,
    string? Url,
    DateTimeOffset? LastHeartbeatAt,
    string? State,
    IReadOnlyList<AgentProblem> Problems,
    IReadOnlyList<DiagnosticsStatus> Diagnostics,
    IReadOnlyList<string> AuditGaps,
    DateTimeOffset StartedAt);

internal sealed record DiagnosticsStatus(Guid AssignmentId, string SetName, string Mode, int IntervalMinutes, bool Enabled, DateTimeOffset? LastRunAt,
    string? LastOutcome, string? LastMessage);

internal static class AgentFiles
{
    private static readonly JsonSerializerOptions Indented = new(AgentJson.Options) { WriteIndented = true };

    public static AgentConfigFile? ReadConfig()
    {
        try
        {
            return File.Exists(AgentInfo.ConfigFile) ? JsonSerializer.Deserialize<AgentConfigFile>(File.ReadAllText(AgentInfo.ConfigFile), AgentJson.Options) : null;
        }
        catch (JsonException)
        {
            // Rule 7 (§3.8): a damaged file is renamed, logged, and the window offers Re-join.
            Quarantine(AgentInfo.ConfigFile);
            return null;
        }
    }

    public static void WriteConfig(AgentConfigFile config) => WriteAtomic(AgentInfo.ConfigFile, JsonSerializer.Serialize(config, Indented));

    public static AgentStatusFile? ReadStatus()
    {
        try
        {
            return File.Exists(AgentInfo.StatusFile) ? JsonSerializer.Deserialize<AgentStatusFile>(File.ReadAllText(AgentInfo.StatusFile), AgentJson.Options) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public static void WriteStatus(AgentStatusFile status) => WriteAtomic(AgentInfo.StatusFile, JsonSerializer.Serialize(status, Indented));

    public static T? ReadState<T>(string name) where T : class
    {
        var path = Path.Combine(AgentInfo.StateFolder, name);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), AgentJson.Options) : null;
        }
        catch (JsonException)
        {
            Quarantine(path);
            return null;
        }
    }

    public static void WriteState<T>(string name, T value) => WriteAtomic(Path.Combine(AgentInfo.StateFolder, name), JsonSerializer.Serialize(value, AgentJson.Options));

    /// <summary>Rule 7: <c>.broken</c>, and started anew.</summary>
    public static void Quarantine(string path)
    {
        try
        {
            File.Move(path, path + ".broken", overwrite: true);
        }
        catch (IOException)
        {
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The data folder and the state folder: SYSTEM and Administrators only (§3.2).</summary>
    public static void Secure()
    {
        foreach (var folder in new[] { AgentInfo.DataFolder, AgentInfo.StateFolder, AgentInfo.LogFolder })
        {
            var info = Directory.CreateDirectory(folder);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
    }
}
