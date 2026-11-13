using PNMTD.Lib.Agent;

namespace PNMTD.Agents;

/// <summary>
/// The agents' paths (docs/windows-sensor-plan.md §9.1) on PNMT's existing address and port: no cookie authentication, no
/// CSRF, no ProblemDetails, as the sensor API's paths. Each request authenticates by its join token or the agent's key.
/// </summary>
public static class AgentPaths
{
    public static bool IsAgentPath(PathString path) => path.StartsWithSegments("/agent", StringComparison.OrdinalIgnoreCase);

    public static bool IsWebSocket(PathString path) => path.Equals(AgentProtocol.WebSocketPath, StringComparison.OrdinalIgnoreCase);
}
