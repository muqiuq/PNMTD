using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Service;
using PNMTD.Sensor.Core.State;

namespace PNMTD.Sensor.Core;

/// <summary>
/// What a platform provides to the shared core (docs/linux-sensor-plan.md §3.2): who the agent is, where its files are, the version
/// check, the machine's names and id, the key store, securing the folders, the system log, and installing the service. The service
/// loop's platform-specific parts are the hooks at the end, which do nothing unless a platform needs them (Windows: WMI's switches,
/// the audit policy's gaps, the section a WMI query belongs to; Linux: systemd's watchdog, config.yaml's log level).
/// </summary>
public interface IAgentPlatform
{
    AgentPlatform Platform { get; }

    /// <summary>The product's name in the log and the system log: <c>PNMT Sensor</c>, <c>PNMT agent</c>.</summary>
    string Product { get; }

    /// <summary>The program's name in the join's user agent: <c>pnmt-sensor</c>, <c>pnmt-agent</c>.</summary>
    string UserAgent { get; }

    string Version { get; }

    /// <summary><c>x64</c>, <c>arm64</c> (or <c>x86</c> on Windows).</summary>
    string Architecture { get; }

    /// <summary>How to join, for the service's "not joined" line: the window; <c>pnmt-agent join</c>.</summary>
    string JoinHint { get; }

    AgentLayout Paths { get; }

    /// <summary>The version check (docs/windows-sensor-plan.md §3.9, docs/linux-sensor-plan.md §4.8): another version runs anyway, flagged.</summary>
    AgentOs ReadOs();

    /// <summary>Windows' MachineGuid; Linux's <c>/etc/machine-id</c>.</summary>
    string? MachineId();

    /// <summary>The names the join sends: the host name, the FQDN, and the domain (Windows' domain or workgroup; a Linux realm).</summary>
    JoinNames Names();

    IAgentKeyStore Keys { get; }

    /// <summary>The data, state and log folders: SYSTEM and Administrators only (Windows' ACL); root only, 0700 (Linux).</summary>
    void SecureFolders();

    /// <summary>Starts, stops, joins, crashes: Windows' event log (Application), Linux's journal (identifier <c>pnmt-agent</c>).</summary>
    void SystemLog(string message, bool warning = false);

    IServiceManager Service { get; }

    // --- the service loop's hooks (docs/linux-sensor-plan.md §3.2): nothing unless the platform needs them -------------

    /// <summary>The configuration's platform part, applied with every configuration (Windows: WMI's switches and own queries).</summary>
    void ApplyConfig(AgentConfig config, SectionScheduler sections, ILogger logger)
    {
    }

    /// <summary>The logging gaps for status.json (Windows: the audit policy's, from the latest access section).</summary>
    IReadOnlyList<string> StatusGaps(SectionScheduler sections) => [];

    /// <summary>On a collector's own thread, its section's name before it runs and null after (Windows: which section a WMI query is for).</summary>
    void OnCollectorThread(string? section)
    {
    }

    /// <summary>Every 10 s while the service runs, with status.json (Linux: <c>WATCHDOG=1</c> to systemd, so a hung process is killed).</summary>
    void ServiceTick()
    {
    }

    /// <summary>Whether to log at debug level (agent.json's <c>logLevel</c>; on Linux also config.yaml's <c>log_level</c>), without a restart.</summary>
    bool DebugLogging(AgentConfigFile config) => config.LogLevel?.Equals("Debug", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Where an agent keeps its files (docs/windows-sensor-plan.md §3.2, docs/linux-sensor-plan.md §4.2): agent.json and status.json in
/// <see cref="DataFolder"/>, the buffers, bookmarks and flags in <see cref="StateFolder"/>, the daily log files (<see cref="LogPrefix"/>
/// and the date) in <see cref="LogFolder"/>, and the installed program.
/// </summary>
public sealed record AgentLayout(string DataFolder, string StateFolder, string LogFolder, string Program, string LogPrefix)
{
    public string ConfigFile => Path.Combine(DataFolder, "agent.json");

    public string StatusFile => Path.Combine(DataFolder, "status.json");

    /// <summary>Written by the window or <c>pnmt-agent collect-now</c>; the service picks it up within a second while connected.</summary>
    public string CollectNowFile => Path.Combine(StateFolder, "collect-now");

    /// <summary>Written before a crash exits: the next start reports "restarted after a crash".</summary>
    public string CrashMarkerFile => Path.Combine(StateFolder, "crashed");
}

/// <summary>The names a join sends (docs/windows-sensor-plan.md §3.3 step 5, docs/linux-sensor-plan.md §4.4 step 6).</summary>
public sealed record JoinNames(string Hostname, string? Fqdn, string? Domain);

/// <summary>
/// The agent's keys (docs/windows-sensor-plan.md §3.4, docs/linux-sensor-plan.md §4.5): the ECDSA P-256 signing key, created at the
/// join under a name agent.json keeps, and the ECDH P-256 encryption key per agent id, created on first use. Windows: CNG keys in the
/// machine store, non-exportable; Linux: PKCS#8 PEM files, 0600, in a 0700 folder.
/// </summary>
public interface IAgentKeyStore
{
    string NewName();

    ECDsa Create(string name);

    ECDsa Open(string name);

    void Delete(string name);

    /// <summary>Opens the encryption key, or creates it the first time. Fails with a <see cref="CryptographicException"/>.</summary>
    ECDiffieHellman EncryptionKey(Guid agentId);

    void DeleteEncryptionKey(Guid agentId);
}

/// <summary>The service as the join needs it (docs/windows-sensor-plan.md §3.3 step 8, docs/linux-sensor-plan.md §4.4 step 9).</summary>
public interface IServiceManager
{
    /// <summary>Copies the program to its place, installs the service, and starts it (or restarts it when it runs).</summary>
    void InstallAndStart();
}
