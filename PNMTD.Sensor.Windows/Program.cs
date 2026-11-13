using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Collectors;
using PNMTD.Sensor.Windows.Join;
using PNMTD.Sensor.Windows.Logging;
using PNMTD.Sensor.Windows.Resilience;
using PNMTD.Sensor.Windows.Service;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows;

/// <summary>
/// One executable, three modes (docs/windows-sensor-plan.md §3.1): the window (a double-click, elevated), the service
/// (<c>pnmt-sensor.exe service</c>, started by Windows) and the command line (<c>join</c>, <c>status</c>, <c>uninstall</c>,
/// <c>collect</c>) for unattended installs.
/// </summary>
internal static partial class Program
{
    private const string Usage = """
        pnmt-sensor.exe                       the window: join, the service's status, uninstall
        pnmt-sensor.exe join --url <PNMT URL> --token <xxxx-xxxx> [--name <name>] [--quiet]
                                              joins, installs and starts the service; exit 0 when connected
        pnmt-sensor.exe status                the service and the connection, as status.json says
        pnmt-sensor.exe uninstall [--delete-logs]
                                              stops and deletes the service, the key and the data; PNMT keeps the server
        pnmt-sensor.exe collect               collects every section once and prints them as JSON (for support)
        pnmt-sensor.exe version
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Ui.WindowApp.Run();
        }
        if (args[0] == "service")
        {
            return RunService();
        }
        AttachConsole(-1);
        try
        {
            return args[0] switch
            {
                "join" => Join(args[1..]),
                "status" => Status(),
                "uninstall" => Uninstall(args[1..]),
                "collect" => Collect(),
                "version" or "--version" => Print($"PNMT Sensor {AgentInfo.Version} ({AgentInfo.Architecture})", 0),
                _ => Print(Usage, args[0] is "help" or "--help" or "-h" or "/?" ? 0 : 2),
            };
        }
        catch (AgentJoinException e)
        {
            return Print($"Refused: {e.Message}", 1);
        }
        catch (Exception e)
        {
            return Print($"Failed: {e.Message}", 1);
        }
    }

    // --- the service ----------------------------------------------------------------------------------------

    private static int RunService()
    {
        Directory.CreateDirectory(AgentInfo.LogFolder);
        var logs = new DailyFileLoggerProvider(AgentInfo.LogFolder, "pnmt-sensor");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashed(logs, e.ExceptionObject as Exception);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddWindowsService(o => o.ServiceName = AgentInfo.ServiceName);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning);
        builder.Logging.AddProvider(logs);
        AddAgent(builder.Services, logs);
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentService>());
        using var host = builder.Build();
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            host.Services.GetRequiredService<ProblemReporter>().Report("agent", "background task", e.Exception);
            e.SetObserved();
        };
        WriteEventLog($"PNMT Sensor {AgentInfo.Version} started.", EventLogEntryType.Information);
        host.Run();
        WriteEventLog("PNMT Sensor stopped.", EventLogEntryType.Information);
        return 0;
    }

    /// <summary>The agent's parts, for the service and for <c>collect</c>.</summary>
    internal static void AddAgent(IServiceCollection services, DailyFileLoggerProvider logs)
    {
        services.AddSingleton(logs);
        services.AddSingleton<ProblemReporter>();
        services.AddSingleton<Collector, SystemCollector>();
        services.AddSingleton<Collector, HardwareCollector>();
        services.AddSingleton<Collector, NetworkCollector>();
        services.AddSingleton<SectionScheduler>();
        services.AddSingleton<HeartbeatSampler>();
        services.AddSingleton<AgentService>();
    }

    /// <summary>Rule 5 (§3.8): logged as fatal, the log flushed, a marker for the next start, and exit; Windows starts it again.</summary>
    private static void Crashed(DailyFileLoggerProvider logs, Exception? exception)
    {
        try
        {
            logs.CreateLogger("agent").LogCritical(exception, "Crashed: {Message}", exception?.Message);
            Directory.CreateDirectory(AgentInfo.StateFolder);
            File.WriteAllText(AgentInfo.CrashMarkerFile, $"{exception?.GetType().Name}: {exception?.Message}");
            WriteEventLog($"PNMT Sensor crashed: {exception}", EventLogEntryType.Error);
            logs.Dispose();
        }
        finally
        {
            Environment.Exit(1);
        }
    }

    internal static void WriteEventLog(string message, EventLogEntryType type)
    {
        try
        {
            EventLog.WriteEntry(AgentInfo.EventSource, message, type);
        }
        catch (Exception)
        {
        }
    }

    // --- the command line -------------------------------------------------------------------------------------

    private static int Join(string[] args)
    {
        string? url = null, token = null, name = null;
        var quiet = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length:
                    url = args[++i];
                    break;
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                case "--quiet":
                    quiet = true;
                    break;
                default:
                    return Print(Usage, 2);
            }
        }
        if (url is null || token is null)
        {
            return Print(Usage, 2);
        }
        var steps = new Progress<string>(s =>
        {
            if (!quiet)
            {
                Console.WriteLine($"… {s}");
            }
        });
        var result = Joiner.JoinAsync(url, token, name, steps, CancellationToken.None).GetAwaiter().GetResult();
        if (!quiet)
        {
            Console.WriteLine($"Joined as {result.Grant.HostName} / {result.Grant.SensorName}.");
            Console.WriteLine($"This server's key: {result.AgentFingerprint}");
            Console.WriteLine($"PNMT's key:        {result.PnmtdFingerprint}");
        }
        WriteEventLog($"Joined PNMT as {result.Grant.HostName} / {result.Grant.SensorName} (key {result.AgentFingerprint}).", EventLogEntryType.Information);
        return result.Connected ? 0 : Print("Installed, but not connected within 60 s: see the log.", 3);
    }

    private static int Status()
    {
        var config = AgentFiles.ReadConfig();
        var status = AgentFiles.ReadStatus();
        Console.WriteLine($"PNMT Sensor {AgentInfo.Version}");
        Console.WriteLine($"Service:    {ServiceControl.Status()?.ToString() ?? "not installed"}");
        if (config is null)
        {
            Console.WriteLine("Not joined.");
            return 1;
        }
        Console.WriteLine($"Server:     {config.HostName} / {config.SensorName} at {config.Url}");
        Console.WriteLine($"Agent:      {config.AgentId}, key {config.AgentKeyFingerprint}");
        Console.WriteLine($"PNMT's key: {config.PnmtdFingerprint}");
        if (status is not null)
        {
            Console.WriteLine($"Connection: {(status.Connected ? $"connected since {status.ConnectedSince:u}" : status.State)}; last heartbeat {status.LastHeartbeatAt:u}");
            foreach (var p in status.Problems.Take(5))
            {
                Console.WriteLine($"Problem:    {p.Component}, {p.Operation}: {p.Message} ({p.Count}×)");
            }
        }
        return status?.Connected == true ? 0 : 1;
    }

    private static int Uninstall(string[] args)
    {
        foreach (var line in Uninstaller.UninstallAsync(args.Contains("--delete-logs"), CancellationToken.None).GetAwaiter().GetResult())
        {
            Console.WriteLine(line);
        }
        WriteEventLog("PNMT Sensor uninstalled.", EventLogEntryType.Information);
        return 0;
    }

    private static int Collect()
    {
        using var logs = new DailyFileLoggerProvider(AgentInfo.LogFolder, "pnmt-sensor-ui");
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        AddAgent(services, logs);
        using var provider = services.BuildServiceProvider();
        var sections = provider.GetRequiredService<SectionScheduler>().CollectOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(sections, new JsonSerializerOptions(AgentJson.Options) { WriteIndented = true }));
        return 0;
    }

    private static int Print(string text, int code)
    {
        Console.WriteLine(text);
        return code;
    }

    /// <summary>A WinExe has no console: the command line writes to the one it was started from.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
