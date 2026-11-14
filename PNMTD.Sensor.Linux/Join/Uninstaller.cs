using PNMTD.Sensor.Core.Join;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Join;

/// <summary>
/// <c>pnmt-agent uninstall [--purge]</c> (docs/linux-sensor-plan.md §4.1): the service stopped, disabled and its unit deleted; <c>bye</c>
/// to PNMT when it can be reached (the shared core's), which keeps the server and its history, marked uninstalled; the keys and
/// <c>/var/lib/pnmt-agent</c> deleted. config.yaml and the logs stay unless <c>--purge</c>, as <c>apt remove</c> and <c>apt purge</c> do.
/// The program goes last, after everything is said (a single-file binary loads its assemblies from its own file).
/// </summary>
internal static class Uninstaller
{
    public static async Task<IReadOnlyList<string>> UninstallAsync(bool purge, CancellationToken ct)
    {
        var done = new List<string>();
        if (Systemd.IsInstalled)
        {
            try
            {
                Systemd.Systemctl("disable", "--now", AgentInfo.Unit);
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or Core.Collectors.NotAvailableException)
            {
                done.Add($"systemctl disable --now failed: {e.Message}");
            }
            File.Delete(AgentInfo.UnitFile);
            try
            {
                Systemd.Systemctl("daemon-reload");
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or Core.Collectors.NotAvailableException)
            {
            }
            done.Add("The service is stopped, disabled and its unit deleted.");
        }
        var config = AgentFiles.ReadConfig();
        if (config is not null)
        {
            var who = Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } sudo ? $"{sudo} (sudo)" : Environment.UserName;
            done.Add(await AgentBye.SayByeAsync(LinuxPlatform.Instance, config, $"uninstalled by {who} on {config.HostName}", ct)
                ? "PNMT was told: it keeps the server and its history, marked uninstalled."
                : "PNMT could not be reached: the server stays there and turns 460 without heartbeats.");
        }
        if (Directory.Exists(AgentInfo.DataFolder))
        {
            Directory.Delete(AgentInfo.DataFolder, recursive: true);
            done.Add($"The keys and {AgentInfo.DataFolder} are deleted.");
        }
        if (purge)
        {
            foreach (var folder in new[] { AgentInfo.ConfigFolder, AgentInfo.LogFolder })
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            done.Add($"{AgentInfo.ConfigFile} and the logs are deleted (--purge).");
        }
        else
        {
            done.Add($"{AgentInfo.ConfigFile} and the logs in {AgentInfo.LogFolder} stay (--purge deletes them).");
        }
        // The program goes last, in Program.Uninstall, after everything is printed.
        return done;
    }
}
