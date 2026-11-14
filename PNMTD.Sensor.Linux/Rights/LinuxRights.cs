using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Logging;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Core.Service;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Rights;

/// <summary>
/// The rights on this server, as config.yaml grants them now (docs/linux-sensor-plan.md §4.6), for the service and its features:
/// <see cref="Current"/>, and <see cref="Version"/>, which grows with each change of the effective rights, so the service reconnects
/// and its hello announces them. Read at start, again within a few seconds of a change in <c>/etc/pnmt-agent/</c> (one inotify watch
/// on the folder, set up once the folder exists), and every minute anyway (a missed event). A refused file grants nothing and is a
/// problem; a change is logged and written to the journal ("Rights changed on the server: …"). <c>log_level</c> takes effect at once,
/// joined or not (agent.json's <c>logLevel</c> Debug, as on Windows, wins).
/// </summary>
internal sealed class LinuxRights : IAgentRightsSource, IAgentFeature
{
    private readonly ProblemReporter problems;
    private readonly DailyFileLoggerProvider logs;
    private readonly ILogger<LinuxRights> logger;
    private readonly SemaphoreSlim changed = new(0);
    private LinuxConfig current;
    private string fingerprint;
    private int version;

    public LinuxRights(ProblemReporter problems, DailyFileLoggerProvider logs, ILogger<LinuxRights> logger)
    {
        this.problems = problems;
        this.logs = logs;
        this.logger = logger;
        current = ConfigYaml.Read();
        fingerprint = Fingerprint(current);
    }

    public AgentRights Current => Volatile.Read(ref current).Rights;

    /// <summary>config.yaml's <c>log_level: debug</c>.</summary>
    public bool Debug => Volatile.Read(ref current).Debug;

    public int Version => Volatile.Read(ref version);

    public string Name => "config.yaml";

    /// <summary>None yet: <c>keys.remove.v1</c> (L6) and <c>scripts.v1</c> (L7) will follow these rights.</summary>
    public IReadOnlyList<string> Capabilities => [];

    public void Configure(AgentConfig config)
    {
    }

    public Task ReportAsync(AgentSession session, CancellationToken ct) => Task.CompletedTask;

    public Task<Ack?> HandleAsync(AgentSession session, SignedContent request, CancellationToken ct) => Task.FromResult<Ack?>(null);

    public async Task RunAsync(CancellationToken ct)
    {
        ApplyLogLevel(current);
        Announce(current, null);
        var watcher = Watch();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // A change wakes the loop at once; editors write in several steps, so it settles for two seconds first.
                if (await changed.WaitAsync(TimeSpan.FromMinutes(1), ct))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    while (changed.CurrentCount > 0)
                    {
                        await changed.WaitAsync(ct);
                    }
                }
                watcher ??= Watch();
                Reload();
            }
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    /// <summary>Reads the file again; a change in the effective rights raises <see cref="Version"/>.</summary>
    public void Reload()
    {
        var next = ConfigYaml.Read();
        var previous = Interlocked.Exchange(ref current, next);
        ApplyLogLevel(next);
        var nextFingerprint = Fingerprint(next);
        if (nextFingerprint == fingerprint)
        {
            if (next.Rights.Source == AgentRights.Refused)
            {
                // Still refused: reported again (the reporter keeps it to once an hour).
                problems.Report("config.yaml", "reading", $"{AgentInfo.ConfigFile} is refused: {next.Rights.Problem}. Everything is off.");
            }
            if (previous.Debug != next.Debug)
            {
                logger.LogInformation("config.yaml: log_level {Level}", next.Debug ? "debug" : "info");
            }
            return;
        }
        fingerprint = nextFingerprint;
        Interlocked.Increment(ref version);
        Announce(next, previous);
    }

    private void Announce(LinuxConfig config, LinuxConfig? previous)
    {
        var rights = config.Rights;
        foreach (var warning in rights.Warnings)
        {
            logger.LogWarning("config.yaml: {Warning}", warning);
        }
        if (rights.Source == AgentRights.Refused)
        {
            problems.Report("config.yaml", "reading", $"{AgentInfo.ConfigFile} is refused: {rights.Problem}. Everything is off.");
        }
        var text = rights.Source switch
        {
            AgentRights.Refused => $"everything off: {AgentInfo.ConfigFile} is refused ({rights.Problem})",
            AgentRights.FromDefaults => $"{rights.Describe()} (no {AgentInfo.ConfigFile}: the defaults)",
            _ => rights.Describe(),
        };
        if (previous is null)
        {
            logger.LogInformation("Rights on the server: {Rights}", text);
            return;
        }
        logger.LogWarning("Rights changed on the server: {Rights}", text);
        Journal.Write($"Rights changed on the server: {text}.", warning: true);
    }

    private void ApplyLogLevel(LinuxConfig config) =>
        logs.MinimumLevel = config.Debug || AgentFiles.ReadConfig()?.LogLevel?.Equals("Debug", StringComparison.OrdinalIgnoreCase) == true
            ? LogLevel.Debug
            : LogLevel.Information;

    /// <summary>One watch on /etc/pnmt-agent/ (inotify), when the folder exists; the minute's re-read covers the rest.</summary>
    private FileSystemWatcher? Watch()
    {
        if (!Directory.Exists(AgentInfo.ConfigFolder))
        {
            return null;
        }
        try
        {
            var watcher = new FileSystemWatcher(AgentInfo.ConfigFolder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Attributes | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler wake = (_, _) => changed.Release();
            watcher.Changed += wake;
            watcher.Created += wake;
            watcher.Deleted += wake;
            watcher.Renamed += (_, _) => changed.Release();
            watcher.Error += (_, e) => logger.LogDebug("config.yaml's watch: {Message}", e.GetException().Message);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning("config.yaml is not watched ({Message}): it is read again every minute.", e.Message);
            return null;
        }
    }

    /// <summary>What counts as a change: the hello's summary (rights, problem, warnings).</summary>
    private static string Fingerprint(LinuxConfig config) => AgentJson.Serialize(config.Rights.Summary());
}
