using System.Text.Json;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// What is set for the WMI queries (docs/windows-sensor-plan.md §16, "WMI"): PNMT's settings from the configuration (built-in
/// queries off or with another timeout, the own queries), and what a local administrator switched off in the window
/// (<c>state\wmi-local.json</c>, read again when it changes). Off on the server wins.
/// </summary>
internal static class WmiSettings
{
    private const string LocalFile = "wmi-local.json";

    private static readonly Lock Gate = new();
    private static (DateTime Written, HashSet<string> Off) local = (DateTime.MinValue, new(StringComparer.OrdinalIgnoreCase));

    /// <summary>PNMT's settings, from the configuration last received.</summary>
    public static WmiConfig Pnmt { get; set; } = WmiConfig.Empty;

    /// <summary>A query's (a built-in one's id, an own one's Guid as text) switch, who switched it off, and PNMT's timeout for it.</summary>
    public static (bool Enabled, string? OffBy, TimeSpan? Timeout) For(string id)
    {
        if (LocallyOff().Contains(id))
        {
            return (false, "server", Timeout(id));
        }
        var setting = Pnmt.Overrides.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        return setting is { Enabled: false } ? (false, "pnmt", Timeout(id)) : (true, null, Timeout(id));
    }

    private static TimeSpan? Timeout(string id) =>
        Pnmt.Overrides.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase))?.TimeoutSeconds is { } seconds and > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, 600))
            : null;

    /// <summary>What the window switched off on this server; the file is read again when it changed.</summary>
    public static IReadOnlySet<string> LocallyOff()
    {
        var path = Path.Combine(AgentInfo.StateFolder, LocalFile);
        try
        {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            lock (Gate)
            {
                if (written != local.Written)
                {
                    var names = written == DateTime.MinValue ? [] : JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), AgentJson.Options) ?? [];
                    local = (written, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
                }
                return local.Off;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return local.Off;
        }
    }

    /// <summary>The window: switches a query off on this server, or lets PNMT decide again.</summary>
    public static void SetLocallyOff(string id, bool off)
    {
        var names = new HashSet<string>(LocallyOff(), StringComparer.OrdinalIgnoreCase);
        if (off)
        {
            names.Add(id);
        }
        else
        {
            names.Remove(id);
        }
        AgentFiles.WriteState(LocalFile, names.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
