using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;

namespace PNMTD.Windows;

/// <summary>
/// What an agent is configured with (docs/windows-sensor-plan.md §3.6): the intervals, the access settings and its
/// diagnostics assignments. Its version is the installation's; whatever changes it raises the version and pushes
/// <c>config</c> to the agents concerned.
/// </summary>
public static class WindowsConfig
{
    public static async Task<AgentConfig> BuildAsync(PnmtdDbContext db, AgentKeyStore keys, WindowsOptions options, Guid sensorId, CancellationToken ct)
    {
        var settings = await keys.SettingsAsync(db, ct);
        IReadOnlyList<DiagnosticsAssignment> diagnostics = [];
        return new AgentConfig(settings.ConfigVersion, Math.Clamp(options.HeartbeatSeconds, 15, 600), Math.Clamp(options.FullUpdateHours, 1, 48),
            new AccessConfig(options.FailedLoginWindowMinutes), diagnostics);
    }
}
