using System.Text.Json;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Core.State;

/// <summary>
/// <c>agent.json</c> (docs/windows-sensor-plan.md §3.2; docs/linux-sensor-plan.md §4.2, the same record): the URL, the agent's
/// identity, PNMTD's pinned key, the configuration last verified, and the log level. Written by the join; read by the service.
/// <see cref="KeyName"/> names the key in the platform's store (a CNG key on Windows, the PEM files on Linux).
/// </summary>
public sealed record AgentConfigFile(
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
public sealed record AgentStatusFile(
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
    DateTimeOffset StartedAt,
    IReadOnlyList<SectionStatus>? Sections = null);

/// <summary>A section for the window: when it was collected, its size and errors, when it was last sent, and whether that was this one.</summary>
public sealed record SectionStatus(string Name, DateTimeOffset? CollectedAt, int? Bytes, int Errors, DateTimeOffset? SentAt, bool SentCurrent);

public sealed record DiagnosticsStatus(Guid AssignmentId, string SetName, string Mode, int IntervalMinutes, bool Enabled, DateTimeOffset? LastRunAt,
    string? LastOutcome, string? LastMessage);

/// <summary>
/// The agent's files (§3.2, §3.8 rule 7), where the platform keeps them (<see cref="Use"/>, once at start): agent.json, status.json,
/// and the state files, each written atomically; a damaged one is renamed <c>.broken</c>. On Linux they are created 0600.
/// </summary>
public static class AgentFiles
{
    private static readonly JsonSerializerOptions Indented = new(AgentJson.Options) { WriteIndented = true };

    private static AgentLayout? layout;

    /// <summary>Where the files are: the platform's <see cref="IAgentPlatform.Paths"/>, set first thing in <c>Main</c>.</summary>
    public static AgentLayout Paths => layout ?? throw new InvalidOperationException("AgentFiles.Use(the platform's paths) comes first.");

    public static void Use(AgentLayout paths) => layout = paths;

    public static AgentConfigFile? ReadConfig()
    {
        try
        {
            return File.Exists(Paths.ConfigFile) ? JsonSerializer.Deserialize<AgentConfigFile>(File.ReadAllText(Paths.ConfigFile), AgentJson.Options) : null;
        }
        catch (JsonException)
        {
            // Rule 7 (§3.8): a damaged file is renamed, logged, and the window offers Re-join.
            Quarantine(Paths.ConfigFile);
            return null;
        }
    }

    public static void WriteConfig(AgentConfigFile config) => WriteAtomic(Paths.ConfigFile, JsonSerializer.Serialize(config, Indented));

    public static AgentStatusFile? ReadStatus()
    {
        try
        {
            return File.Exists(Paths.StatusFile) ? JsonSerializer.Deserialize<AgentStatusFile>(File.ReadAllText(Paths.StatusFile), AgentJson.Options) : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public static void WriteStatus(AgentStatusFile status) => WriteAtomic(Paths.StatusFile, JsonSerializer.Serialize(status, Indented));

    public static T? ReadState<T>(string name) where T : class
    {
        var path = Path.Combine(Paths.StateFolder, name);
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

    public static void WriteState<T>(string name, T value) => WriteAtomic(Path.Combine(Paths.StateFolder, name), JsonSerializer.Serialize(value, AgentJson.Options));

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
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(temporary, text);
        }
        else
        {
            // Root's only (docs/linux-sensor-plan.md §4.2): agent.json, status.json and the state are 0600.
            using var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(text);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
