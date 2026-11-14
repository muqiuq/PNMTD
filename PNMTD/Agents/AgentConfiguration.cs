using PNMTD.Data;
using Microsoft.EntityFrameworkCore;
using PNMTD.Lib.Agent;
using PNMTD.Windows;

namespace PNMTD.Agents;

/// <summary>
/// What an agent is configured with (docs/windows-sensor-plan.md §3.6): the intervals, the access settings, its
/// diagnostics assignments and, on Windows, its WMI settings. Its version is the installation's; whatever changes it raises
/// the version and pushes <c>config</c> to the agents concerned.
/// </summary>
public static class AgentConfiguration
{
    public static async Task<AgentConfig> BuildAsync(PnmtdDbContext db, AgentKeyStore keys, AgentOptions options, Guid sensorId, CancellationToken ct)
    {
        var settings = await keys.SettingsAsync(db, ct);
        var diagnostics = await Diagnostics.DiagnosticsConfig.ForAgentAsync(db, sensorId, ct);
        var wmi = await db.Agents.AsNoTracking().Where(a => a.SensorId == sensorId).Select(a => a.WmiSettings).SingleOrDefaultAsync(ct);
        return new AgentConfig(settings.ConfigVersion, Math.Clamp(options.HeartbeatSeconds, 15, 600), Math.Clamp(options.FullUpdateHours, 1, 48),
            new AccessConfig(options.FailedLoginWindowMinutes), diagnostics, Wmi: WindowsWmi.Settings(wmi));
    }
}
