using System.Net.WebSockets;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Native;
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
        var config = AgentFiles.ReadConfig();
        if (config is not null)
        {
            if (await SayByeAsync(config, ct))
            {
                done.Add("PNMT was told: it keeps the server and its history, marked uninstalled.");
            }
            else
            {
                done.Add("PNMT could not be reached: the server stays there and turns 460 without heartbeats.");
            }
            AgentKey.Delete(config.KeyName);
            done.Add("The key is deleted.");
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

    private static async Task<bool> SayByeAsync(AgentConfigFile config, CancellationToken ct)
    {
        try
        {
            using var agentKey = AgentKey.Open(config.KeyName);
            using var pnmtdKey = AgentSigning.ImportPublicKey(config.PnmtdKey)!;
            var url = new Uri(config.Url);
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(AgentProtocol.Subprotocol);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await socket.ConnectAsync(new UriBuilder(new Uri(url, AgentProtocol.WebSocketPath)) { Scheme = url.Scheme == "https" ? "wss" : "ws" }.Uri, timeout.Token);
            var identity = new AgentIdentity(config.AgentId, agentKey, pnmtdKey, AgentSigning.Origin(url), AgentInfo.Version, AgentInfo.Architecture,
                WindowsVersion.Read(), []);
            await using var session = await AgentSession.HandshakeAsync(socket, identity, false, timeout.Token);
            await session.SendAsync(AgentMessageTypes.Bye, new Bye($"uninstalled by {Environment.UserDomainName}\\{Environment.UserName}"), timeout.Token);
            await Task.Delay(500, timeout.Token);
            await session.CloseAsync("uninstalled", timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
