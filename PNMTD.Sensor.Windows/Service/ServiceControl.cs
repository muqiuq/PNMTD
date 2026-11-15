using System.Diagnostics;
using System.ServiceProcess;

namespace PNMTD.Sensor.Windows.Service;

/// <summary>
/// The Windows service (docs/windows-sensor-plan.md §3.2): <c>PNMTSensor</c>, automatic (delayed) start, LocalSystem, restarted
/// after 1 min, 1 min, then 5 min, the failure count reset after a day, also after a non-crash exit.
/// </summary>
internal static class ServiceControl
{
    public static bool IsInstalled => ServiceController.GetServices().Any(s => s.ServiceName == AgentInfo.ServiceName);

    public static ServiceControllerStatus? Status()
    {
        try
        {
            using var service = new ServiceController(AgentInfo.ServiceName);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Copies the program to Program Files when it runs from elsewhere: the service's path.</summary>
    public static string InstallProgram()
    {
        var target = AgentInfo.InstalledExecutable;
        var current = AgentInfo.CurrentExecutable;
        if (!string.Equals(Path.GetFullPath(current), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(AgentInfo.ProgramFolder);
            File.Copy(current, target, overwrite: true);
        }
        return target;
    }

    public static void Install(string executable)
    {
        if (!IsInstalled)
        {
            Sc($"create {AgentInfo.ServiceName} binPath= \"\\\"{executable}\\\" service\" start= delayed-auto obj= LocalSystem DisplayName= \"{AgentInfo.DisplayName}\"");
        }
        else
        {
            Sc($"config {AgentInfo.ServiceName} binPath= \"\\\"{executable}\\\" service\" start= delayed-auto obj= LocalSystem");
        }
        Sc($"description {AgentInfo.ServiceName} \"Reports this server to PNMT: system, hardware, network, services, remote access, logins, the event log, diagnostics.\"");
        Sc($"failure {AgentInfo.ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/300000");
        Sc($"failureflag {AgentInfo.ServiceName} 1");
        try
        {
            if (!EventLog.SourceExists(AgentInfo.EventSource))
            {
                EventLog.CreateEventSource(AgentInfo.EventSource, "Application");
            }
        }
        catch (Exception)
        {
            // The Windows event log is for starts, stops, joins and crashes only; the log file has everything.
        }
    }

    public static void Start(TimeSpan? wait = null)
    {
        using var service = new ServiceController(AgentInfo.ServiceName);
        if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
        {
            if (service.Status == ServiceControllerStatus.StopPending)
            {
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            service.Start();
        }
        service.WaitForStatus(ServiceControllerStatus.Running, wait ?? TimeSpan.FromSeconds(30));
    }

    public static void Stop()
    {
        using var service = new ServiceController(AgentInfo.ServiceName);
        if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
        {
            service.Stop();
        }
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
    }

    public static void Restart()
    {
        Stop();
        Start();
    }

    public static void Delete()
    {
        if (IsInstalled)
        {
            try
            {
                Stop();
            }
            catch (Exception)
            {
            }
            Sc($"delete {AgentInfo.ServiceName}");
        }
    }

    private static void Sc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("sc.exe did not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe {arguments.Split(' ')[0]} failed ({process.ExitCode}): {output.Trim()}");
        }
    }
}
