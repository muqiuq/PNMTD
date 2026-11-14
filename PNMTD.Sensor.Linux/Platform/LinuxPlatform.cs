using Microsoft.Extensions.Hosting.Systemd;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Linux.Rights;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>
/// The Linux agent's side of the shared core (docs/linux-sensor-plan.md §3.2, §4): the paths of §4.2, the version check of §4.8, the
/// names and <c>/etc/machine-id</c> for the join, the key files of §4.5, folders 0700 root, the journal, systemd, and in the service
/// loop systemd's watchdog (<c>WATCHDOG=1</c> every 10 s: a hung process is killed, §4.3) and config.yaml's <c>log_level</c>.
/// </summary>
internal sealed class LinuxPlatform : IAgentPlatform
{
    public static LinuxPlatform Instance { get; } = new();

    private readonly Lock gate = new();
    private readonly SystemdNotifier notifier = new();
    private (AgentOs Os, DateTimeOffset At)? os;

    private LinuxPlatform()
    {
    }

    /// <summary>The service's rights, for config.yaml's log level; set when the service's parts are put together.</summary>
    public LinuxRights? Rights { get; set; }

    public AgentPlatform Platform => AgentPlatform.Linux;

    public string Product => AgentInfo.Product;

    public string UserAgent => AgentInfo.Name;

    public string Version => AgentInfo.Version;

    public string Architecture => AgentInfo.Architecture;

    public string JoinHint => "pnmt-agent join --url … --token … joins (or re-joins) the server";

    public AgentLayout Paths => AgentInfo.Layout;

    /// <summary>At every start and every connect, from the files (pveversion at most once an hour: it takes about a second).</summary>
    public AgentOs ReadOs()
    {
        lock (gate)
        {
            if (os is { } known && DateTimeOffset.UtcNow - known.At < TimeSpan.FromHours(1))
            {
                return known.Os;
            }
            var read = LinuxOs.ToAgentOs(LinuxOs.Read());
            os = (read, DateTimeOffset.UtcNow);
            return read;
        }
    }

    public string? MachineId() => Tool.ReadValue("/etc/machine-id");

    /// <summary>The host name, the FQDN (<c>hostname -f</c>, when it has a dot) and the realm when joined to one (§4.4 step 6).</summary>
    public JoinNames Names()
    {
        var hostname = Tool.ReadValue("/proc/sys/kernel/hostname") ?? Environment.MachineName;
        return new JoinNames(hostname, Fqdn(hostname), Realm());
    }

    public static string? Fqdn(string hostname)
    {
        try
        {
            var result = Tool.Run("hostname", ["-f"], TimeSpan.FromSeconds(5));
            var fqdn = result.Output.Trim();
            return result.Ok && fqdn.Contains('.') && !fqdn.Contains(' ') ? fqdn : hostname.Contains('.') ? hostname : null;
        }
        catch (Exception e) when (e is Core.Collectors.NotAvailableException or TimeoutException)
        {
            return hostname.Contains('.') ? hostname : null;
        }
    }

    /// <summary>The realm (realmd: <c>realm list --name-only</c>), else SSSD's domains, else Samba's realm with <c>security = ads</c>; null when joined to none.</summary>
    public static string? Realm()
    {
        try
        {
            if (Tool.Exists("realm"))
            {
                var result = Tool.Run("realm", ["list", "--name-only"], TimeSpan.FromSeconds(10));
                if (result.Ok && result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { } realm)
                {
                    return realm;
                }
            }
        }
        catch (Exception e) when (e is Core.Collectors.NotAvailableException or TimeoutException)
        {
        }
        return IniValue(Tool.ReadFile("/etc/sssd/sssd.conf"), "sssd", "domains")?.Split(',')[0].Trim() is { Length: > 0 } domain ? domain
            : IniValue(Tool.ReadFile("/etc/samba/smb.conf"), "global", "security")?.Equals("ads", StringComparison.OrdinalIgnoreCase) == true
                ? IniValue(Tool.ReadFile("/etc/samba/smb.conf"), "global", "realm")
                : null;
    }

    /// <summary>A value of an INI file's section (sssd.conf, smb.conf), or null.</summary>
    public static string? IniValue(string? text, string section, string key)
    {
        if (text is null)
        {
            return null;
        }
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
            }
            else if (inSection && line.Split('=', 2) is [var name, var value] && name.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim();
            }
        }
        return null;
    }

    public IAgentKeyStore Keys { get; } = new FileKeyStore(AgentInfo.KeysFolder);

    /// <summary>/var/lib/pnmt-agent with state/ and keys/, and /var/log/pnmt-agent: root's, 0700.</summary>
    public void SecureFolders()
    {
        foreach (var folder in new[] { AgentInfo.DataFolder, AgentInfo.StateFolder, AgentInfo.KeysFolder, AgentInfo.LogFolder })
        {
            SecureFolder(folder);
        }
    }

    public static void SecureFolder(string folder)
    {
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (Native.Stat(folder, followLinks: false) is { } facts && (facts.Uid != 0 || facts.Gid != 0))
        {
            Native.ChownToRoot(folder);
        }
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void SystemLog(string message, bool warning = false) => Journal.Write(message, warning);

    public IServiceManager Service { get; } = new Systemd();

    /// <summary>systemd's watchdog (<c>WatchdogSec=5min</c>): told every 10 s that the service is alive.</summary>
    public void ServiceTick()
    {
        try
        {
            if (notifier.IsEnabled)
            {
                notifier.Notify(new ServiceState("WATCHDOG=1"));
            }
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
        }
    }

    /// <summary>agent.json's <c>logLevel</c> as on Windows, or config.yaml's <c>log_level: debug</c>.</summary>
    public bool DebugLogging(AgentConfigFile config) =>
        config.LogLevel?.Equals("Debug", StringComparison.OrdinalIgnoreCase) == true || Rights?.Debug == true;
}
