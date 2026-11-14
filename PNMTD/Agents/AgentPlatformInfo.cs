using PNMTD.Lib.Agent;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Agents;

/// <summary>
/// What differs between the platforms on PNMTD's side (docs/linux-sensor-plan.md §3.3, §9.1, §9.2): the sensor type a join
/// creates and its default name, the event codes (Windows 25x and 45x, Linux 26x and 46x, as each device type has its block),
/// the events' source, which logins page, and the heartbeat's messages. Everything else in <c>PNMTD/Agents/</c> is the same
/// for both. An agent's platform is its row's, from the join token.
/// </summary>
public sealed record AgentPlatformInfo(
    AgentPlatform Platform,
    SensorType SensorType,
    string Name,
    string EventSource,
    int OkCode,
    int RestartedCode,
    int ChangedCode,
    int AdminLoginCode,
    int ProblemCode,
    int CriticalCode,
    int LoginFailuresCode,
    IReadOnlyList<LogonKind> AdminLoginKinds,
    string? ClockNote)
{
    public static readonly AgentPlatformInfo Windows = new(AgentPlatform.Windows, SensorType.WINDOWS, "Windows", "windows-agent",
        PNMTStatusCodes.WINDOWS_OK, PNMTStatusCodes.WINDOWS_RESTARTED, PNMTStatusCodes.WINDOWS_CHANGED, PNMTStatusCodes.WINDOWS_ADMIN_LOGIN,
        PNMTStatusCodes.WINDOWS_AGENT_PROBLEM, PNMTStatusCodes.WINDOWS_CRITICAL_EVENT, PNMTStatusCodes.WINDOWS_LOGIN_FAILURES,
        [LogonKind.Console, LogonKind.Rdp, LogonKind.Ssh, LogonKind.WinRm], "Kerberos fails beyond 5 min");

    /// <summary>Linux (docs/linux-sensor-plan.md §5.6, §9.2): admin logins by SSH and at the console page.</summary>
    public static readonly AgentPlatformInfo Linux = new(AgentPlatform.Linux, SensorType.LINUX, "Linux", "linux-agent",
        PNMTStatusCodes.LINUX_OK, PNMTStatusCodes.LINUX_RESTARTED, PNMTStatusCodes.LINUX_CHANGED, PNMTStatusCodes.LINUX_ADMIN_LOGIN,
        PNMTStatusCodes.LINUX_AGENT_PROBLEM, PNMTStatusCodes.LINUX_CRITICAL_ENTRY, PNMTStatusCodes.LINUX_LOGIN_FAILURES,
        [LogonKind.Console, LogonKind.Ssh], null);

    /// <summary>The sensor types that have an agent row: a server joined with a token.</summary>
    public static readonly SensorType[] SensorTypes = [SensorType.WINDOWS, SensorType.LINUX];

    public static AgentPlatformInfo Of(AgentPlatform platform) => platform == AgentPlatform.Linux ? Linux : Windows;

    public static bool IsAgentType(SensorType type) => type is SensorType.WINDOWS or SensorType.LINUX;

    /// <summary>
    /// Stable from heartbeat to heartbeat, so they collapse by the dedupe: "SRV01: Windows Server 2019 Standard 17763, agent 1.3.0";
    /// "web-01: Debian GNU/Linux 12 (bookworm), kernel 6.1.0-28-amd64, agent 1.3.0" (the build is the kernel's release there).
    /// </summary>
    public string OkMessage(AgentEntity agent) => Platform == AgentPlatform.Linux
        ? $"{agent.Hostname}: {agent.OsCaption ?? Name}{(agent.OsBuild is { } kernel ? ", kernel " + kernel : "")}, agent {agent.AgentVersion}"
        : $"{agent.Hostname}: {agent.OsCaption ?? Name}{(agent.OsBuild is { } build ? " " + build : "")}, agent {agent.AgentVersion}";
}
