using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Collectors;
using PNMTD.Sensor.Windows.Service;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Platform;

/// <summary>
/// The Windows agent's side of the shared core (docs/linux-sensor-plan.md §3.2): what moved there in L2 does here what it did before.
/// The paths of <see cref="AgentInfo"/>, <see cref="WindowsVersion"/>, the CNG keys of <see cref="AgentKey"/>, the folders' ACL, the
/// event log, <c>sc.exe</c> (<see cref="ServiceControl"/>), and in the service loop WMI's switches, the audit policy's gaps and the
/// section a WMI query runs for.
/// </summary>
internal sealed class WindowsPlatform : IAgentPlatform
{
    public static WindowsPlatform Instance { get; } = new();

    private WindowsPlatform()
    {
    }

    public AgentPlatform Platform => AgentPlatform.Windows;

    public string Product => AgentInfo.DisplayName;

    public string UserAgent => "pnmt-sensor";

    public string Version => AgentInfo.Version;

    public string Architecture => AgentInfo.Architecture;

    public string JoinHint => "The window joins (or re-joins) the server";

    public AgentLayout Paths { get; } = AgentInfo.Layout;

    public AgentOs ReadOs() => WindowsVersion.Read();

    public string? MachineId() => WindowsVersion.MachineGuid();

    public JoinNames Names()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        return new JoinNames(Environment.MachineName,
            string.IsNullOrEmpty(properties.DomainName) ? null : $"{properties.HostName}.{properties.DomainName}",
            string.IsNullOrEmpty(properties.DomainName) ? Environment.UserDomainName : properties.DomainName);
    }

    public IAgentKeyStore Keys { get; } = new WindowsKeyStore();

    public void SecureFolders() => Secure();

    public void SystemLog(string message, bool warning = false) =>
        Program.WriteEventLog(message, warning ? EventLogEntryType.Warning : EventLogEntryType.Information);

    public IServiceManager Service { get; } = new WindowsServiceManager();

    /// <summary>The data folder and the state folder: SYSTEM and Administrators only (docs/windows-sensor-plan.md §3.2).</summary>
    public static void Secure()
    {
        foreach (var folder in new[] { AgentInfo.DataFolder, AgentInfo.StateFolder, AgentInfo.LogFolder })
        {
            var info = Directory.CreateDirectory(folder);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
    }

    // --- the service loop's hooks -----------------------------------------------------------------------------------------

    /// <summary>WMI (docs/windows-sensor-plan.md §16): another switch or timeout counts from the sections' next run, so they run now; new own queries run now too.</summary>
    public void ApplyConfig(AgentConfig received, SectionScheduler sections, ILogger logger)
    {
        var wmi = received.Wmi ?? WmiConfig.Empty;
        var before = WmiSettings.Pnmt;
        WmiSettings.Pnmt = wmi;
        if (!before.Overrides.SequenceEqual(wmi.Overrides))
        {
            logger.LogInformation("WMI: {Off} built-in queries switched off by PNMT, {Timeouts} with another timeout", wmi.Overrides.Count(o => !o.Enabled),
                wmi.Overrides.Count(o => o.TimeoutSeconds is not null));
            sections.Request([]);
        }
        else if (!before.Custom.SequenceEqual(wmi.Custom))
        {
            logger.LogInformation("WMI: {Count} own queries from PNMT", wmi.Custom.Count);
            sections.Request([SectionNames.Wmi]);
        }
    }

    /// <summary>What the audit policy misses, from the latest access section, for the window.</summary>
    public IReadOnlyList<string> StatusGaps(SectionScheduler sections)
    {
        try
        {
            return sections.Latest(SectionNames.Access) is { } message ? AgentJson.Deserialize<AccessSection>(message.Data)?.Gaps.ToList() ?? [] : [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>The section whose collector runs on this thread, for the WMI tabs.</summary>
    public void OnCollectorThread(string? section) => Wmi.CurrentSection = section;

    /// <summary>The CNG keys in the machine store (docs/windows-sensor-plan.md §3.4): <see cref="AgentKey"/>.</summary>
    private sealed class WindowsKeyStore : IAgentKeyStore
    {
        public string NewName() => AgentKey.NewName();

        public ECDsa Create(string name) => AgentKey.Create(name);

        public ECDsa Open(string name) => AgentKey.Open(name);

        public void Delete(string name) => AgentKey.Delete(name);

        public ECDiffieHellman EncryptionKey(Guid agentId) => AgentKey.EncryptionKey(agentId);

        public void DeleteEncryptionKey(Guid agentId) => AgentKey.DeleteEncryptionKey(agentId);
    }

    /// <summary>The service <c>PNMTSensor</c> (docs/windows-sensor-plan.md §3.2): the program in Program Files, <c>sc.exe</c>, and a restart when it runs.</summary>
    private sealed class WindowsServiceManager : IServiceManager
    {
        public void InstallAndStart()
        {
            var executable = ServiceControl.InstallProgram();
            ServiceControl.Install(executable);
            if (ServiceControl.Status() == ServiceControllerStatus.Running)
            {
                ServiceControl.Restart();
            }
            else
            {
                ServiceControl.Start();
            }
        }
    }
}
