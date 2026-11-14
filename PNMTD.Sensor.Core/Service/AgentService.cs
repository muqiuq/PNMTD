using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Logging;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Core.State;

namespace PNMTD.Sensor.Core.Service;

/// <summary>
/// The service (docs/windows-sensor-plan.md §3.5, §3.8; docs/linux-sensor-plan.md §3.2, §4.7): collects, connects and reports.
/// The connection loop never ends: any failure backs off (1 s doubling to 5 min, ±20 %) and reconnects; only a revocation (4403)
/// stops it until the next join. A watchdog restarts the loop when no heartbeat went out for three intervals; above 300 MB the
/// agent exits and the platform's service manager (Windows' recovery, systemd's <c>Restart=always</c>) starts it again. When the
/// server's own rights change (<see cref="IAgentRightsSource"/>), it reconnects, so the hello announces them.
/// </summary>
public sealed class AgentService(
    ILogger<AgentService> logger,
    IAgentPlatform platform,
    ProblemReporter problems,
    DailyFileLoggerProvider logs,
    SectionScheduler sections,
    IHeartbeatSampler sampler,
    IEnumerable<IAgentFeature> features,
    IEnumerable<IAgentRightsSource> rightsSources) : BackgroundService, IAgentSessionHandler
{
    private const long MaxWorkingSet = 300L * 1024 * 1024;

    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly List<IAgentFeature> extras = features.ToList();
    private readonly IAgentRightsSource? rights = rightsSources.FirstOrDefault();
    private AgentLayout Paths => platform.Paths;
    private AgentConfigFile? config;
    private AgentSession? session;
    private DateTimeOffset? connectedSince;
    private DateTimeOffset? lastHeartbeatAt;
    private string? state = "starting";

    public AgentConfigFile? Config => config;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var os = platform.ReadOs();
        logger.LogInformation("{Product} {Version} ({Architecture}) on {Caption} {Build}", platform.Product, platform.Version, platform.Architecture, os.Caption, os.Build);
        if (!os.Supported)
        {
            logger.LogWarning("{Caption} {Build} is not a supported version: the agent runs anyway (§3.9).", os.Caption, os.Build);
            problems.Report("agent", "version check", $"{os.Caption} {os.Build} ({os.Edition}) is not a supported version; the agent runs anyway.");
        }
        var crashed = File.Exists(Paths.CrashMarkerFile);
        if (crashed)
        {
            var text = SafeRead(Paths.CrashMarkerFile);
            problems.Report("agent", "start", $"Restarted after a crash: {text}", fatal: true);
            TryDelete(Paths.CrashMarkerFile);
        }
        try
        {
            platform.SecureFolders();
        }
        catch (Exception e)
        {
            problems.Report("agent", "securing the data folder", e);
        }

        var background = new List<Task>
        {
            Guarded("collectors", sections.RunAsync, stoppingToken),
            Guarded("status file", WriteStatusAsync, stoppingToken),
            Guarded("memory watchdog", WatchMemoryAsync, stoppingToken),
        };
        background.AddRange(extras.Select(f => Guarded(f.Name, f.RunAsync, stoppingToken)));

        var backoff = TimeSpan.FromSeconds(1);
        var notJoinedLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            config = AgentFiles.ReadConfig();
            if (config is null)
            {
                state = "not joined";
                if (!notJoinedLogged)
                {
                    logger.LogWarning("Not joined: no agent.json. {Hint}; the service waits.", platform.JoinHint);
                    notJoinedLogged = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }
            notJoinedLogged = false;
            logs.MinimumLevel = platform.DebugLogging(config) ? LogLevel.Debug : LogLevel.Information;
            if (config.Config is { } known)
            {
                ApplyConfig(known);
            }
            try
            {
                var end = await ConnectAndRunAsync(config, crashed, stoppingToken);
                crashed = false;
                backoff = TimeSpan.FromSeconds(1);
                if (end.Revoked)
                {
                    state = $"revoked by PNMT: {end.Reason}";
                    logger.LogError("PNMT revoked this agent ({Reason}). It stops connecting until it is joined again.", end.Reason);
                    await WaitForNewJoinAsync(config, stoppingToken);
                    continue;
                }
                logger.LogInformation("Disconnected: {Code} {Reason}", end.CloseCode, end.Reason);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (AgentHandshakeException e) when (e.CloseCode == AgentCloseCodes.Revoked)
            {
                state = $"revoked by PNMT: {e.Reason}";
                logger.LogError("PNMT refused this agent: revoked ({Reason}). It stops connecting until it is joined again.", e.Reason);
                await WaitForNewJoinAsync(config, stoppingToken);
                continue;
            }
            catch (Exception e)
            {
                state = $"offline: {e.Message}";
                logger.LogWarning("The connection failed: {Message}", e.Message);
                if (e is AgentHandshakeException { CloseCode: AgentCloseCodes.Unauthorized })
                {
                    problems.Report("connection", "authentication", e.Message);
                }
            }
            finally
            {
                session = null;
                connectedSince = null;
            }
            var jitter = 1 + (Random.Shared.NextDouble() * 0.4 - 0.2);
            var delay = TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * jitter);
            logger.LogInformation("Reconnecting in {Seconds:0} s", delay.TotalSeconds);
            await Task.Delay(delay, stoppingToken);
            backoff = TimeSpan.FromSeconds(Math.Min(300, backoff.TotalSeconds * 2));
        }
        await Task.WhenAll(background.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
    }

    private async Task<AgentSessionEnd> ConnectAndRunAsync(AgentConfigFile current, bool crashed, CancellationToken ct)
    {
        state = "connecting";
        using var agentKey = platform.Keys.Open(current.KeyName);
        using var pnmtdKey = AgentSigning.ImportPublicKey(current.PnmtdKey) ?? throw new InvalidOperationException("agent.json holds no valid PNMTD key: re-join.");
        var url = new Uri(current.Url);
        // The rights as they are now (docs/linux-sensor-plan.md §4.6); a later change reconnects (ReportAsync).
        var rightsVersion = rights?.Version;
        var identity = new AgentIdentity(current.AgentId, agentKey, pnmtdKey, AgentSigning.Origin(url), platform.Version, platform.Architecture,
            platform.ReadOs(), Capabilities(), EncryptionKey(current.AgentId), platform.Platform, rights?.Current.Summary());

        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(AgentProtocol.Subprotocol);
        socket.Options.KeepAliveInterval = AgentProtocol.KeepAlive;
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            await socket.ConnectAsync(WebSocketUrl(url), connectTimeout.Token);
        }
        await using var opened = await AgentSession.HandshakeAsync(socket, identity, crashed, ct);
        session = opened;
        connectedSince = DateTimeOffset.UtcNow;
        state = "connected";
        logger.LogInformation("Connected to {Url}; configuration {Version}", current.Url, opened.Config.ConfigVersion);
        await OnConfigAsync(opened.Config, ct);
        sections.ResendAll();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiving = opened.RunAsync(this, stop.Token);
        var reporting = ReportAsync(opened, rightsVersion, stop.Token);
        var first = await Task.WhenAny(receiving, reporting);
        stop.Cancel();
        if (first == reporting)
        {
            // The watchdog or a send failure: the loop reconnects.
            try
            {
                await reporting;
            }
            catch (RightsChangedException e)
            {
                logger.LogInformation("{Message}: reconnecting, so the hello announces them.", e.Message);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning("Reporting stopped: {Message}", e.Message);
            }
            await opened.CloseAsync("reconnecting", CancellationToken.None);
            return new AgentSessionEnd(null, "the agent reconnects");
        }
        try
        {
            await reporting;
        }
        catch (Exception)
        {
        }
        return await receiving;
    }

    /// <summary>The agent's ECDH key's public half for the hello (§3.4, W6); null when the key store refuses it.</summary>
    private string? EncryptionKey(Guid agentId)
    {
        try
        {
            using var key = platform.Keys.EncryptionKey(agentId);
            return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            problems.Report("agent", "the encryption key", e);
            return null;
        }
    }

    private static Uri WebSocketUrl(Uri baseUrl) =>
        new UriBuilder(new Uri(baseUrl, AgentProtocol.WebSocketPath)) { Scheme = baseUrl.Scheme == "https" ? "wss" : "ws" }.Uri;

    private List<string> Capabilities() => [AgentCapabilities.Collect, .. extras.SelectMany(f => f.Capabilities)];

    /// <summary>Heartbeats, sections, problems and the features' reports, while connected.</summary>
    private async Task ReportAsync(AgentSession current, int? rightsVersion, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(current.Config.HeartbeatSeconds, 15, 600));
        var nextHeartbeat = DateTimeOffset.UtcNow;
        var lastSent = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            if (rights is not null && rights.Version != rightsVersion)
            {
                throw new RightsChangedException("The rights on the server changed");
            }
            if (File.Exists(Paths.CollectNowFile))
            {
                // The window's Collect now (a time stamp: every section), or section names, one per line (scripts after a change).
                var names = ReadCollectNow();
                TryDelete(Paths.CollectNowFile);
                sections.Request(names);
            }
            foreach (var message in sections.TakeForSending())
            {
                var started = DateTimeOffset.UtcNow;
                await current.SendAsync(AgentMessageTypes.Section, message, ct);
                sections.Sent(message);
                logger.LogDebug("Sent the section {Section} to PNMT: {Bytes} bytes, hash {Hash}, {Ms:0} ms", message.Name, SectionScheduler.Bytes(message), message.Hash,
                    (DateTimeOffset.UtcNow - started).TotalMilliseconds);
            }
            var pending = problems.Take();
            if (pending.Count > 0)
            {
                try
                {
                    await current.SendAsync(AgentMessageTypes.Problem, new ProblemBatch(pending), ct);
                }
                catch
                {
                    problems.Return(pending);
                    throw;
                }
            }
            foreach (var feature in extras)
            {
                await feature.ReportAsync(current, ct);
            }
            if (DateTimeOffset.UtcNow >= nextHeartbeat)
            {
                var heartbeat = sampler.Sample(current.Config.ConfigVersion, problems.TakeSinceHeartbeat(), sections.Hashes);
                await current.SendAsync(AgentMessageTypes.Heartbeat, heartbeat, ct);
                lastHeartbeatAt = lastSent = DateTimeOffset.UtcNow;
                nextHeartbeat = lastSent + interval;
            }
            // Rule 4 (§3.8): no heartbeat for three intervals restarts the loop.
            if (DateTimeOffset.UtcNow - lastSent > interval * 3)
            {
                throw new TimeoutException("No heartbeat went out for three intervals: the watchdog restarts the connection.");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    public Task OnConfigAsync(AgentConfig received, CancellationToken ct)
    {
        ApplyConfig(received);
        if (config is { } current && current.Config != received)
        {
            config = current with { Config = received };
            try
            {
                AgentFiles.WriteConfig(config);
            }
            catch (Exception e)
            {
                problems.Report("agent", "saving the configuration", e);
            }
        }
        logger.LogInformation("Configuration {Version}: heartbeat {Heartbeat} s, full update {Hours} h, {Diagnostics} diagnostics assignments",
            received.ConfigVersion, received.HeartbeatSeconds, received.FullUpdateHours, received.Diagnostics.Count);
        return Task.CompletedTask;
    }

    private void ApplyConfig(AgentConfig received)
    {
        sections.FullUpdate = TimeSpan.FromHours(Math.Clamp(received.FullUpdateHours, 1, 48));
        // The platform's part (Windows: WMI, §16).
        platform.ApplyConfig(received, sections, logger);
        foreach (var feature in extras)
        {
            feature.Configure(received);
        }
    }

    public async Task<Ack> OnRequestAsync(AgentSession current, SignedContent request, CancellationToken ct)
    {
        logger.LogInformation("Request {Type} ({Id})", request.Type, request.RequestId);
        switch (request.Type)
        {
            case AgentMessageTypes.Collect:
                var names = AgentJson.Deserialize<CollectRequest>(request.Body)?.Sections.Where(SectionNames.IsKnown).ToList() ?? [];
                var since = DateTimeOffset.UtcNow;
                sections.Request(names);
                var until = DateTimeOffset.UtcNow.AddSeconds(25);
                while (!sections.CollectedSince(names, since) && DateTimeOffset.UtcNow < until)
                {
                    await Task.Delay(500, ct);
                }
                return new Ack(request.RequestId, true);
            case AgentMessageTypes.SendLog:
                var lines = Math.Clamp(AgentJson.Deserialize<SendLogRequest>(request.Body)?.Lines ?? 100, 1, 500);
                await current.SendAsync(AgentMessageTypes.Log, new LogLines(request.RequestId, logs.Tail(lines)), ct);
                return new Ack(request.RequestId, true);
            default:
                foreach (var feature in extras)
                {
                    if (await feature.HandleAsync(current, request, ct) is { } ack)
                    {
                        return ack;
                    }
                }
                return new Ack(request.RequestId, false, $"This agent does not know \"{request.Type}\".");
        }
    }

    public void OnIgnored(string type, string reason) => logger.LogWarning("Ignored a message \"{Type}\": {Reason}", type, reason);

    private async Task WaitForNewJoinAsync(AgentConfigFile revoked, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            if (AgentFiles.ReadConfig() is { } next && next.JoinedAt != revoked.JoinedAt)
            {
                logger.LogInformation("Joined again: connecting.");
                return;
            }
        }
    }

    private async Task WriteStatusAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Debug logging switched in the window (or config.yaml) takes effect here, without a restart.
                if (AgentFiles.ReadConfig() is { } current)
                {
                    logs.MinimumLevel = platform.DebugLogging(current) ? LogLevel.Debug : LogLevel.Information;
                }
                AgentFiles.WriteStatus(new AgentStatusFile(DateTimeOffset.UtcNow, platform.Version, session?.IsOpen == true, connectedSince, config?.Url,
                    lastHeartbeatAt, state, problems.Recent, extras.SelectMany(f => f.Diagnostics).ToList(), platform.StatusGaps(sections), startedAt,
                    sections.Status()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("status.json: {Message}", e.Message);
            }
            platform.ServiceTick();
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    /// <summary>Rule 6 (§3.8): above 300 MB the agent logs it, reports it and exits; Windows' recovery (systemd) starts it again.</summary>
    private async Task WatchMemoryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            using var process = Process.GetCurrentProcess();
            if (process.WorkingSet64 > MaxWorkingSet)
            {
                var message = $"The working set reached {process.WorkingSet64 / (1024 * 1024)} MB (above 300 MB): the agent restarts itself.";
                logger.LogCritical("{Message}", message);
                File.WriteAllText(Paths.CrashMarkerFile, message);
                Environment.Exit(3);
            }
        }
    }

    /// <summary>Rule 1 (§3.8): a background loop that fails is reported and started again after a minute.</summary>
    private Task Guarded(string name, Func<CancellationToken, Task> loop, CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await loop(ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                problems.Report(name, "loop", e);
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }
    }, ct);

    private static string SafeRead(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return "(unknown)";
        }
    }

    private List<string> ReadCollectNow()
    {
        try
        {
            var lines = File.ReadAllLines(Paths.CollectNowFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            // Anything else (the window's time stamp) asks for every section.
            return lines.All(SectionNames.IsKnown) ? lines.Distinct().ToList() : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The server's own rights changed (docs/linux-sensor-plan.md §4.6): the loop reconnects, so the hello announces them.</summary>
internal sealed class RightsChangedException(string message) : Exception(message);

/// <summary>
/// What the later phases plug into the service: logins and connections (W3), diagnostics (W4), scripts (W6); on Linux config.yaml's
/// watch (L3). Each runs its own loop, reports while connected, handles its requests, and adds to status.json. A platform registers
/// its features as singletons of this interface; the order of registration is the order of their capabilities in the hello.
/// </summary>
public interface IAgentFeature
{
    string Name { get; }

    IReadOnlyList<string> Capabilities { get; }

    void Configure(AgentConfig config);

    Task RunAsync(CancellationToken ct);

    Task ReportAsync(AgentSession session, CancellationToken ct);

    /// <summary>The answer to a request, or null when it isn't this feature's.</summary>
    Task<Ack?> HandleAsync(AgentSession session, SignedContent request, CancellationToken ct);

    IEnumerable<DiagnosticsStatus> Diagnostics => [];

    IEnumerable<string> AuditGaps => [];
}
