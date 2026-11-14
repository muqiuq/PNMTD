using System.Net.WebSockets;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.State;

namespace PNMTD.Sensor.Core.Join;

/// <summary>
/// Uninstalling's word to PNMT (docs/windows-sensor-plan.md §10.4; docs/linux-sensor-plan.md §4.1): a connection of its own, the
/// handshake, <c>bye</c> with who uninstalled it, and closed. PNMT keeps the server and its history, marked uninstalled, and writes
/// no 460. Best effort, at most 20 s: when PNMT can't be reached, the server stays and turns 460 without heartbeats.
/// </summary>
public static class AgentBye
{
    public static async Task<bool> SayByeAsync(IAgentPlatform platform, AgentConfigFile config, string reason, CancellationToken ct)
    {
        try
        {
            using var agentKey = platform.Keys.Open(config.KeyName);
            using var pnmtdKey = AgentSigning.ImportPublicKey(config.PnmtdKey)!;
            var url = new Uri(config.Url);
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol(AgentProtocol.Subprotocol);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await socket.ConnectAsync(new UriBuilder(new Uri(url, AgentProtocol.WebSocketPath)) { Scheme = url.Scheme == "https" ? "wss" : "ws" }.Uri, timeout.Token);
            var identity = new AgentIdentity(config.AgentId, agentKey, pnmtdKey, AgentSigning.Origin(url), platform.Version, platform.Architecture,
                platform.ReadOs(), [], Platform: platform.Platform);
            await using var session = await AgentSession.HandshakeAsync(socket, identity, false, timeout.Token);
            await session.SendAsync(AgentMessageTypes.Bye, new Bye(reason), timeout.Token);
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
