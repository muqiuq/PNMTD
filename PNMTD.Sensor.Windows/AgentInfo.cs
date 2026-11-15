using System.Reflection;
using System.Runtime.InteropServices;

namespace PNMTD.Sensor.Windows;

/// <summary>Where the agent lives and who it is (docs/windows-sensor-plan.md §3.2).</summary>
internal static class AgentInfo
{
    public const string ServiceName = "PNMTSensor";
    public const string DisplayName = "PNMT Sensor";
    public const string EventSource = "PNMT Sensor";
    public const string ExecutableName = "pnmt-sensor.exe";

    /// <summary>What <c>version</c> answers first, and what an update's version resource must name (docs/agent-update-plan.md §6.3).</summary>
    public const string Product = "PNMT Sensor";

    public static string Version { get; } =
        typeof(AgentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AgentInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string Architecture => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => "x64",
    };

    public static string ProgramFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PNMT Sensor");

    public static string InstalledExecutable => Path.Combine(ProgramFolder, ExecutableName);

    /// <summary>The program before the last update or rollback (docs/agent-update-plan.md §6.3): <c>update --rollback</c> puts it back.</summary>
    public static string PreviousExecutable => Path.Combine(ProgramFolder, "pnmt-sensor.previous.exe");

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PNMT Sensor");

    public static string ConfigFile => Path.Combine(DataFolder, "agent.json");

    public static string StatusFile => Path.Combine(DataFolder, "status.json");

    public static string StateFolder => Path.Combine(DataFolder, "state");

    public static string LogFolder => Path.Combine(DataFolder, "logs");

    /// <summary>Where the update downloads the new executable: in the state folder, SYSTEM and Administrators only.</summary>
    public static string UpdateFolder => Path.Combine(StateFolder, "update");

    /// <summary>Written when the window asks for Collect now; the service picks it up within 10 s.</summary>
    public static string CollectNowFile => Path.Combine(StateFolder, "collect-now");

    /// <summary>Written before a crash exits: the next start reports "restarted after a crash".</summary>
    public static string CrashMarkerFile => Path.Combine(StateFolder, "crashed");

    public static string CurrentExecutable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExecutableName);

    /// <summary>The same paths for the shared core (docs/linux-sensor-plan.md §3.2): <c>AgentFiles.Use(AgentInfo.Layout)</c> at start.</summary>
    public static AgentLayout Layout => new(DataFolder, StateFolder, LogFolder, InstalledExecutable, "pnmt-sensor");
}
