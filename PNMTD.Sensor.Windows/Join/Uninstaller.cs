using PNMTD.Sensor.Windows.Native;
using PNMTD.Sensor.Windows.Platform;
using PNMTD.Sensor.Windows.Service;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Join;

/// <summary>
/// Uninstall (docs/windows-sensor-plan.md §10.4): stop and delete the service, say <c>bye</c> when PNMTD can be reached, delete
/// the key, the stored credentials and the data folder (the logs stay unless asked), and the program at the next reboot.
/// PNMT keeps the server and its history, marked uninstalled.
/// </summary>
internal static class Uninstaller
{
    public static async Task<IReadOnlyList<string>> UninstallAsync(bool deleteLogs, CancellationToken ct)
    {
        var done = new List<string>();
        ServiceControl.Delete();
        done.Add("The service is stopped and deleted.");
        try
        {
            await Scripts.LocalScripts.RemoveAllTasksAsync(ct);
            done.Add("The registered scripts' scheduled tasks are deleted.");
        }
        catch (Exception e)
        {
            done.Add($"The scheduled tasks in \\PNMT Sensor\\ could not be deleted: {e.Message}");
        }
        var config = AgentFiles.ReadConfig();
        if (config is not null)
        {
            if (await AgentBye.SayByeAsync(WindowsPlatform.Instance, config, $"uninstalled by {Environment.UserDomainName}\\{Environment.UserName}", ct))
            {
                done.Add("PNMT was told: it keeps the server and its history, marked uninstalled.");
            }
            else
            {
                done.Add("PNMT could not be reached: the server stays there and turns 460 without heartbeats.");
            }
            AgentKey.Delete(config.KeyName);
            AgentKey.DeleteEncryptionKey(config.AgentId);
            done.Add("The keys are deleted, and with the data folder the stored accounts.");
        }
        foreach (var entry in Directory.Exists(AgentInfo.DataFolder) ? Directory.GetFileSystemEntries(AgentInfo.DataFolder) : [])
        {
            if (!deleteLogs && string.Equals(Path.GetFullPath(entry), Path.GetFullPath(AgentInfo.LogFolder), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (IOException)
            {
            }
        }
        done.Add(deleteLogs ? "The data folder and the logs are deleted." : $"The data folder is deleted; the logs stay in {AgentInfo.LogFolder}.");
        if (File.Exists(AgentInfo.InstalledExecutable))
        {
            Win32.MoveFileEx(AgentInfo.InstalledExecutable, null, Win32.MoveFileDelayUntilReboot);
            Win32.MoveFileEx(AgentInfo.ProgramFolder, null, Win32.MoveFileDelayUntilReboot);
            done.Add("The program is deleted at the next reboot.");
        }
        return done;
    }
}
