using PNMTD.Agents;
using PNMTD.Data;
using Microsoft.EntityFrameworkCore;
using PNMTD.Lib.Agent;

namespace PNMTD.Windows;

/// <summary>
/// What an agent is configured with (docs/windows-sensor-plan.md §3.6): the intervals, the access settings, its
/// diagnostics assignments and its WMI settings. Its version is the installation's; whatever changes it raises the version and pushes
/// <c>config</c> to the agents concerned.
/// </summary>
public static class WindowsConfig
{
    public static async Task<AgentConfig> BuildAsync(PnmtdDbContext db, AgentKeyStore keys, WindowsOptions options, Guid sensorId, CancellationToken ct)
    {
        var settings = await keys.SettingsAsync(db, ct);
        var diagnostics = await Diagnostics.DiagnosticsConfig.ForAgentAsync(db, sensorId, ct);
        var wmi = await db.WindowsAgents.AsNoTracking().Where(a => a.SensorId == sensorId).Select(a => a.WmiSettings).SingleOrDefaultAsync(ct);
        return new AgentConfig(settings.ConfigVersion, Math.Clamp(options.HeartbeatSeconds, 15, 600), Math.Clamp(options.FullUpdateHours, 1, 48),
            new AccessConfig(options.FailedLoginWindowMinutes), diagnostics, Wmi: Wmi(wmi));
    }

    /// <summary>A server's WMI settings (§16), or none.</summary>
    public static WmiConfig Wmi(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return WmiConfig.Empty;
        }
        try
        {
            return AgentJson.Deserialize<WmiConfig>(json) ?? WmiConfig.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return WmiConfig.Empty;
        }
    }
}
