using System.Reflection;
using System.Runtime.InteropServices;
using PNMTD.Sensor.Core;

namespace PNMTD.Sensor.Linux;

/// <summary>Where the agent lives and who it is (docs/linux-sensor-plan.md §4.2).</summary>
internal static class AgentInfo
{
    public const string Product = "PNMT agent";

    /// <summary>The program's name, the unit's, and the journal's identifier.</summary>
    public const string Name = "pnmt-agent";

    public const string Unit = "pnmt-agent.service";

    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AgentInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary><c>x64</c> or <c>arm64</c>: the two builds (§4.1).</summary>
    public static string Architecture => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        var other => other.ToString().ToLowerInvariant(),
    };

    /// <summary>root, 0755; join and update copy the program here.</summary>
    public const string Program = "/usr/local/sbin/pnmt-agent";

    public const string UnitFile = "/etc/systemd/system/pnmt-agent.service";

    /// <summary>The rights (§4.6): written by root, never by PNMT. The folder 0755, the file 0600.</summary>
    public const string ConfigFolder = "/etc/pnmt-agent";

    public const string ConfigFile = "/etc/pnmt-agent/config.yaml";

    /// <summary>agent.json and status.json; 0700 root, as are the folders below.</summary>
    public const string DataFolder = "/var/lib/pnmt-agent";

    public const string StateFolder = "/var/lib/pnmt-agent/state";

    /// <summary>The signing and encryption keys as PKCS#8 PEM, 0600 (§4.5).</summary>
    public const string KeysFolder = "/var/lib/pnmt-agent/keys";

    /// <summary>Script runs, one folder each, deleted after the run (§8.4).</summary>
    public const string RunsFolder = "/var/lib/pnmt-agent/runs";

    public const string LogFolder = "/var/log/pnmt-agent";

    public static AgentLayout Layout { get; } = new(DataFolder, StateFolder, LogFolder, Program, Name);

    public static string CurrentExecutable => Environment.ProcessPath ?? Program;
}
