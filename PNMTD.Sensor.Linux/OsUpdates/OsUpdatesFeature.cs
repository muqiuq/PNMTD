using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Core.Service;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.OsUpdates;

/// <summary>
/// OS updates on the connection (docs/linux-os-updates-plan.md §4): <c>os-updates.v1</c>. The daily <c>apt-get update</c> at this server's
/// slot inside PNMT's window (also while PNMT is away; a missed day catches up), and the runs PNMT asks for (<c>os-update-run</c>: update,
/// upgrade, full upgrade), each checked against config.yaml's <c>os_updates</c> and refused with its reason (busy, dpkg interrupted, too
/// little space, the removals changed). A run lives in its own unit (<see cref="AptRunner"/>); this feature follows its output, streams the
/// cleaned log (<c>os-update-output</c>), and reports its end (<c>os-update-result</c>) until PNMTD acknowledges it, across restarts and
/// disconnections. It also reports other runs from history.log and the changelogs of pending source versions.
/// </summary>
internal sealed class OsUpdatesFeature : IAgentFeature
{
    /// <summary>How often the loop looks at the run, the schedule and the flag.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan HistoryEvery = TimeSpan.FromMinutes(5);

    private const string HistoryLog = "/var/log/apt/history.log";

    private readonly ProblemReporter problems;
    private readonly IAgentRightsSource? rights;
    private readonly SectionScheduler sections;
    private readonly ILogger<OsUpdatesFeature> logger;
    private readonly OsUpdatesStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim changelogsWanted = new(0, 1);
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly List<AptHistoryEntry> historyOutbox = [];
    private readonly List<PackageChangelog> changelogOutbox = [];
    private OsUpdatesConfig? config;
    private AgentSession? lastSession;
    private DateTimeOffset nextHistoryScan = DateTimeOffset.MinValue;

    public OsUpdatesFeature(ProblemReporter problems, IEnumerable<IAgentRightsSource> rightsSources, SectionScheduler sections, ILogger<OsUpdatesFeature> logger)
        : this(problems, rightsSources, sections, logger, OsUpdatesStore.Default)
    {
    }

    internal OsUpdatesFeature(ProblemReporter problems, IEnumerable<IAgentRightsSource> rightsSources, SectionScheduler sections, ILogger<OsUpdatesFeature> logger,
        OsUpdatesStore store)
    {
        this.problems = problems;
        rights = rightsSources.FirstOrDefault();
        this.sections = sections;
        this.logger = logger;
        this.store = store;
    }

    public string Name => "os-updates";

    public IReadOnlyList<string> Capabilities => [AgentCapabilities.OsUpdates];

    private OsUpdateRights Rights => (rights?.Current ?? AgentRights.Defaults).OsUpdates;

    public void Configure(AgentConfig received) => config = received.OsUpdates;

    // --- the loop ---------------------------------------------------------------------------------------------------------

    public async Task RunAsync(CancellationToken ct)
    {
        store.Ensure();
        OsUpdatesCollector.FeatureState = State;
        var changelogs = ChangelogsLoopAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            await gate.WaitAsync(ct);
            try
            {
                Step();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                problems.Report("os-updates", "following the runs", e);
            }
            finally
            {
                gate.Release();
            }
            if (DateTimeOffset.UtcNow >= nextHistoryScan)
            {
                nextHistoryScan = DateTimeOffset.UtcNow + HistoryEvery;
                try
                {
                    await ScanHistoryAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    problems.Report("os-updates", "reading history.log", e);
                }
            }
            await Task.Delay(Tick, ct);
        }
        await changelogs;
    }

    /// <summary>One look: the run in progress moves on (or ends); without one, the flag of <c>pnmt-agent os-updates update</c> or the schedule may start one.</summary>
    private void Step()
    {
        if (store.Current() is { } current)
        {
            Advance(current);
            return;
        }
        if (File.Exists(store.UpdateNowFlag))
        {
            OsUpdatesStore.TryDelete(store.UpdateNowFlag);
            if (!Rights.Update)
            {
                logger.LogWarning("pnmt-agent os-updates update: apt-get update is off in config.yaml (os_updates.update: false).");
                return;
            }
            Start(Guid.NewGuid(), OsUpdateKind.Update, null, OsUpdateTrigger.Server, "root on the server");
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var schedule = store.Schedule();
        if (Due(now, schedule, config, Rights, AgentId(), startedAt) is { } due)
        {
            store.Save(schedule with { LastScheduledDay = due });
            if (schedule.LastUpdateAt is { } last && now - last < TimeSpan.FromHours(2))
            {
                // Refreshed less than two hours ago (by hand, or PNMT): today's slot counts as done.
                return;
            }
            Start(Guid.NewGuid(), OsUpdateKind.Update, null, OsUpdateTrigger.Schedule, null);
        }
    }

    /// <summary>
    /// The day whose daily update is due now, or null (§4.2): PNMT's switch and window, config.yaml's update right, and this server's slot
    /// (the window's start plus a stable offset from its agent id, in its local time). The slot passed and today hasn't run: due. Or the
    /// last update is more than 25 hours old (the server was off at the slot): due at once. Not in the first two minutes after a start.
    /// </summary>
    public static DateOnly? Due(DateTimeOffset now, ScheduleState schedule, OsUpdatesConfig? config, OsUpdateRights rights, Guid? agentId, DateTimeOffset startedAt,
        TimeZoneInfo? zone = null)
    {
        if (config is not { Enabled: true, DailyUpdate: true } || !rights.Update || agentId is null || now - startedAt < TimeSpan.FromMinutes(2))
        {
            return null;
        }
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        if (schedule.LastScheduledDay == today)
        {
            return null;
        }
        var slot = Slot(today, config, agentId.Value, zone);
        var overdue = schedule.LastUpdateAt is not { } last || now - last > TimeSpan.FromHours(25);
        return now >= slot || overdue ? today : null;
    }

    /// <summary>The slot of a day: the window's start (HH:mm, local) plus the agent's offset inside the window.</summary>
    public static DateTimeOffset Slot(DateOnly day, OsUpdatesConfig config, Guid agentId, TimeZoneInfo zone)
    {
        var start = TimeOnly.TryParseExact(config.WindowStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : new TimeOnly(4, 0);
        var window = Math.Clamp(config.WindowMinutes, 1, 24 * 60);
        // A stable number from the id: its first four bytes.
        var offset = (int)(BitConverter.ToUInt32(agentId.ToByteArray(), 0) % (uint)window);
        var local = day.ToDateTime(start).AddMinutes(offset);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        // A time that doesn't exist (a DST gap) moves an hour on.
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }

    /// <summary>When the next daily update runs, for the section and the tab; null while it is off.</summary>
    public DateTimeOffset? NextUpdateAt()
    {
        if (config is not { Enabled: true, DailyUpdate: true } || !Rights.Update || AgentId() is not { } id)
        {
            return null;
        }
        var zone = TimeZoneInfo.Local;
        var schedule = store.Schedule();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var slot = Slot(today, config, id, zone);
        return schedule.LastScheduledDay == today || DateTimeOffset.UtcNow > slot ? Slot(today.AddDays(1), config, id, zone) : slot;
    }

    private static Guid? AgentId() => AgentFiles.ReadConfig()?.AgentId;

    private (OsUpdateRunRef? Current, OsUpdateRunRef? LastUpdate, DateTimeOffset? NextUpdateAt, int RepositoriesFailed) State()
    {
        var current = store.Current();
        var schedule = store.Schedule();
        var last = schedule.LastUpdateRunId is { } id && schedule.LastUpdateAt is { } at && schedule.LastUpdateState is { } state
            ? new OsUpdateRunRef(id, OsUpdateKind.Update, null, at, state)
            : null;
        return (current?.Ref(), last, NextUpdateAt(), schedule.RepositoriesFailed);
    }

    // --- starting a run -----------------------------------------------------------------------------------------------

    /// <summary>Starts a run; answers why not. A scheduled run that can't start is recorded as failed, so PNMT sees it.</summary>
    private string? Start(Guid runId, OsUpdateKind kind, OsUpgradeMode? mode, OsUpdateTrigger trigger, string? requestedBy)
    {
        var run = new RunRecord
        {
            RunId = runId,
            Kind = kind,
            Mode = kind == OsUpdateKind.Upgrade ? mode ?? OsUpgradeMode.Upgrade : null,
            Trigger = trigger,
            RequestedByName = requestedBy is null ? null : Cut(requestedBy, 200),
            StartedAt = DateTimeOffset.UtcNow,
            Command = AptRunner.CommandText(runId, kind, kind == OsUpdateKind.Upgrade ? mode ?? OsUpgradeMode.Upgrade : null),
            Unit = AptRunner.UnitName(runId),
        };
        string? refusal;
        try
        {
            refusal = AptRunner.Start(store, run);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            refusal = $"{e.GetType().Name}: {e.Message}";
        }
        if (refusal is not null)
        {
            logger.LogWarning("OS updates: {Command} didn't start: {Reason}", run.Command, refusal);
            if (trigger == OsUpdateTrigger.Person)
            {
                return refusal;
            }
            store.Save(run with { FinishedAt = DateTimeOffset.UtcNow, State = OsUpdateState.Failed, Message = refusal, Summary = OsUpdateSummary.Empty, LogSha256 = OsUpdatesStore.Sha256(store.CleanLog(runId)) });
            return refusal;
        }
        store.Save(run);
        logger.LogInformation("OS updates: started {Command} ({Trigger}{Who})", run.Command, trigger.ToString().ToLowerInvariant(),
            requestedBy is null ? "" : $", for {requestedBy}");
        sections.CollectSoon([SectionNames.OsUpdates]);
        return null;
    }

    /// <summary>
    /// The checks before a run PNMT asked for (§4.4), in order: the rights, one run at a time, and for an upgrade dpkg's state, the free space,
    /// and for a full upgrade the removals the person saw. Answers the refusal, or null.
    /// </summary>
    private string? Check(OsUpdateRunRequest request)
    {
        var granted = Rights;
        if (request.Kind == OsUpdateKind.Update && !granted.Update)
        {
            return "Not allowed on this server: os_updates.update is false in /etc/pnmt-agent/config.yaml.";
        }
        if (request.Kind == OsUpdateKind.Upgrade)
        {
            var mode = request.Mode ?? OsUpgradeMode.Upgrade;
            if (mode == OsUpgradeMode.Upgrade && !granted.Upgrade)
            {
                return "Not allowed on this server: os_updates.upgrade is false in /etc/pnmt-agent/config.yaml.";
            }
            if (mode == OsUpgradeMode.FullUpgrade && !granted.FullUpgrade)
            {
                return "Not allowed on this server: os_updates.full_upgrade is false in /etc/pnmt-agent/config.yaml.";
            }
        }
        if (store.Current() is { } current)
        {
            return $"A run is in progress: {current.Command}, since {current.StartedAt:HH:mm} UTC.";
        }
        if (AptRunner.AnyActive())
        {
            return "A run of PNMT's is still active on the server.";
        }
        if (request.Kind == OsUpdateKind.Update)
        {
            return null;
        }
        try
        {
            if (Tool.Run("dpkg", ["--audit"], TimeSpan.FromSeconds(60)).Output.Trim().Length > 0)
            {
                return "dpkg was interrupted: run dpkg --configure -a on the server first.";
            }
            var mode = request.Mode ?? OsUpgradeMode.Upgrade;
            AptState.Forget();
            var simulation = AptParsers.ParseSimulation(AptState.Simulate(mode));
            if (simulation.Changes.Count == 0)
            {
                return "Nothing to install: the server is up to date.";
            }
            if (mode == OsUpgradeMode.FullUpgrade && request.RemovalsHash is { } seen)
            {
                var now = AptPlan.HashOf(simulation.Removals.Select(r => r.Name));
                if (now != seen)
                {
                    return "The plan changed since you looked at it (other packages would be removed): review it again.";
                }
            }
            var (download, _) = AptParsers.ParseSizes(AptState.Sizes(mode));
            if (Available("/var/cache/apt/archives") is { } archives && archives < (download ?? 0) + 200_000_000)
            {
                return $"Too little space in /var/cache/apt/archives: {Megabytes(archives)} free, the download needs {Megabytes(download ?? 0)} plus 200 MB.";
            }
            var kernel = simulation.Installs.Any(c => c.Name.StartsWith("linux-image-", StringComparison.Ordinal) || c.Name.StartsWith("proxmox-kernel-", StringComparison.Ordinal)
                                                     || c.Name.StartsWith("pve-kernel-", StringComparison.Ordinal));
            if (kernel && Available("/boot") is { } boot && boot < 150_000_000)
            {
                return $"Too little space in /boot for the new kernel: {Megabytes(boot)} free, 150 MB needed.";
            }
        }
        catch (Exception e) when (e is Core.Collectors.NotAvailableException or TimeoutException or InvalidOperationException)
        {
            return $"The checks before the upgrade failed: {e.Message}";
        }
        return null;
    }

    /// <summary>Free bytes where a path is, as <c>df</c> counts them for root.</summary>
    private static long? Available(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }
        var lines = Tool.Text("df", ["-B1", "--output=avail", path], TimeSpan.FromSeconds(15)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length >= 2 && long.TryParse(lines[1].Trim(), out var bytes) ? bytes : null;
    }

    private static string Megabytes(long bytes) => $"{bytes / 1_000_000d:0.#} MB";

    // --- following a run ------------------------------------------------------------------------------------------------

    /// <summary>Reads what the run wrote since the last look (whole lines; all of it once it ended), and ends it when its unit did.</summary>
    private void Advance(RunRecord run)
    {
        var status = AptRunner.Status(store, run);
        run = ReadOutput(run, everything: !status.Running);
        if (status.Running)
        {
            store.Save(run);
            return;
        }
        Finish(run, status);
    }

    private RunRecord ReadOutput(RunRecord run, bool everything)
    {
        var raw = store.RawLog(run.RunId);
        if (!File.Exists(raw))
        {
            return run;
        }
        using var stream = new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = run.RawRead;
        var clean = run.CleanBytes;
        while (read < stream.Length)
        {
            stream.Seek(read, SeekOrigin.Begin);
            var buffer = new byte[(int)Math.Min(1024 * 1024, stream.Length - read)];
            var count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (count == 0)
            {
                break;
            }
            // Whole lines only while it runs: a line still being written (dpkg's progress) waits for its end.
            var end = everything && read + count >= stream.Length ? count : Array.LastIndexOf(buffer, (byte)'\n', count - 1) + 1;
            if (end <= 0)
            {
                break;
            }
            clean = store.AppendLog(run.RunId, AptParsers.Clean(Encoding.UTF8.GetString(buffer, 0, end)), clean);
            read += end;
            if (end < count)
            {
                break;
            }
        }
        return run with { RawRead = read, CleanBytes = clean };
    }

    private void Finish(RunRecord run, UnitStatus status)
    {
        var log = File.Exists(store.CleanLog(run.RunId)) ? File.ReadAllText(store.CleanLog(run.RunId)) : "";
        OsUpdateState state;
        OsUpdateSummary summary;
        string? message = status.Problem;
        var rebootRequired = File.Exists("/run/reboot-required");
        if (run.Kind == OsUpdateKind.Update)
        {
            var output = AptParsers.ParseUpdate(log);
            state = status.ExitCode is null ? OsUpdateState.Failed : AptParsers.UpdateState(status.ExitCode, output);
            summary = new OsUpdateSummary([], [], [], [], [], rebootRequired, output.Warnings, output.Errors, output.Reached, output.Failed, output.Fetched);
            message ??= state switch
            {
                OsUpdateState.Warnings when output.Failed > 0 => $"{output.Failed} of {output.Reached + output.Failed} repositories failed: {output.Errors.Concat(output.Warnings).FirstOrDefault()}",
                OsUpdateState.Warnings => output.Errors.Concat(output.Warnings).FirstOrDefault(),
                OsUpdateState.Failed => output.Errors.FirstOrDefault() ?? $"apt-get update ended with exit code {status.ExitCode}.",
                _ => null,
            };
            var schedule = store.Schedule();
            store.Save(schedule with { LastUpdateAt = DateTimeOffset.UtcNow, LastUpdateState = state, LastUpdateRunId = run.RunId, RepositoriesFailed = output.Failed });
        }
        else
        {
            var history = ReadHistoryEntries(includeRotated: false).LastOrDefault(e => e.Commandline?.Contains($"PNMT::Run={run.RunId:N}", StringComparison.Ordinal) == true);
            var (kept, services, warnings, errors) = AptParsers.ParseUpgrade(log);
            summary = new OsUpdateSummary(history?.Upgraded ?? [], history?.Installed ?? [], history?.Removed ?? [], kept, services, rebootRequired, warnings, errors);
            state = status.ExitCode == 0 ? (warnings.Count > 0 ? OsUpdateState.Warnings : OsUpdateState.Succeeded) : OsUpdateState.Failed;
            message ??= state == OsUpdateState.Failed
                ? errors.FirstOrDefault() ?? history?.Error ?? $"apt-get ended with exit code {status.ExitCode}."
                : null;
        }
        var finished = run with
        {
            FinishedAt = DateTimeOffset.UtcNow,
            State = state,
            ExitCode = status.ExitCode,
            Message = message is null ? null : Cut(message, 2000),
            Summary = Bounded(summary),
            LogSha256 = OsUpdatesStore.Sha256(store.CleanLog(run.RunId)),
        };
        store.Save(finished);
        AptRunner.Forget(run);
        var line = Describe(finished);
        logger.LogInformation("{Message}", line);
        Journal.Write(line, warning: state is OsUpdateState.Failed);
        AptState.Forget();
        sections.Request([SectionNames.OsUpdates, SectionNames.System]);
        if (run.Kind == OsUpdateKind.Update && changelogsWanted.CurrentCount == 0)
        {
            try
            {
                changelogsWanted.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already asked for.
            }
        }
    }

    /// <summary>"PNMT ran apt-get full-upgrade for Philipp Albrecht: 23 upgraded, 1 installed, 1 removed, exit 0." (§4.5)</summary>
    public static string Describe(RunRecord run)
    {
        var what = run.Kind == OsUpdateKind.Update ? "apt-get update" : run.Mode == OsUpgradeMode.FullUpgrade ? "apt-get full-upgrade" : "apt-get upgrade";
        var who = run.Trigger switch
        {
            OsUpdateTrigger.Schedule => " (daily)",
            OsUpdateTrigger.Server => " (from the command line)",
            _ => run.RequestedByName is { } name ? $" for {name}" : "",
        };
        var summary = run.Summary ?? OsUpdateSummary.Empty;
        var counts = run.Kind == OsUpdateKind.Update
            ? $"{summary.RepositoriesReached} repositories reached{(summary.RepositoriesFailed > 0 ? $", {summary.RepositoriesFailed} failed" : "")}"
            : $"{summary.Upgraded.Count} upgraded, {summary.Installed.Count} installed, {summary.Removed.Count} removed";
        var exit = run.ExitCode is { } code ? $"exit {code}" : run.State.ToString().ToLowerInvariant();
        return $"PNMT ran {what}{who}: {counts}, {exit}{(run.Message is { } m && run.State != OsUpdateState.Succeeded ? $" ({Cut(m, 200)})" : "")}.";
    }

    private static OsUpdateSummary Bounded(OsUpdateSummary s) => s with
    {
        Upgraded = s.Upgraded.Take(5000).ToList(),
        Installed = s.Installed.Take(5000).ToList(),
        Removed = s.Removed.Take(5000).ToList(),
        ConfigFilesKept = s.ConfigFilesKept.Take(200).ToList(),
        ServicesToRestart = s.ServicesToRestart.Take(200).ToList(),
        Warnings = s.Warnings.Take(50).Select(w => Cut(w, 500)).ToList(),
        Errors = s.Errors.Take(50).Select(e => Cut(e, 500)).ToList(),
    };

    // --- reporting ------------------------------------------------------------------------------------------------------

    public async Task ReportAsync(AgentSession session, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        List<RunRecord> runs;
        try
        {
            runs = store.Runs().Where(r => !r.Acknowledged).ToList();
            if (!ReferenceEquals(session, lastSession))
            {
                // A new connection: starts and results again; the log goes on where it was (PNMTD asks for what it misses).
                lastSession = session;
                foreach (var run in runs)
                {
                    store.Save(run with { StartedSent = false, ResultSent = false });
                }
                runs = store.Runs().Where(r => !r.Acknowledged).ToList();
            }
        }
        finally
        {
            gate.Release();
        }
        foreach (var run in runs)
        {
            await SendRunAsync(session, run, ct);
        }
        await SendOutboxesAsync(session, ct);
    }

    private async Task SendRunAsync(AgentSession session, RunRecord run, CancellationToken ct)
    {
        if (!run.StartedSent)
        {
            await session.SendAsync(AgentMessageTypes.OsUpdateStarted,
                new OsUpdateStartedMessage(run.RunId, run.Kind, run.Mode, run.Trigger, run.RequestedByName, run.StartedAt, run.Command), ct);
            await UpdateAsync(run.RunId, r => r with { StartedSent = true }, ct);
        }
        // At most four chunks per second, so a long log doesn't crowd out the heartbeat.
        var sent = run.SentBytes;
        for (var i = 0; i < 4 && sent < run.CleanBytes; i++)
        {
            var text = store.ReadLog(run.RunId, sent, OsUpdateOutputMessage.MaxBytes);
            if (text.Length == 0)
            {
                break;
            }
            await session.SendAsync(AgentMessageTypes.OsUpdateOutput, new OsUpdateOutputMessage(run.RunId, sent, text), ct);
            sent += Encoding.UTF8.GetByteCount(text);
            var reached = sent;
            await UpdateAsync(run.RunId, r => r with { SentBytes = Math.Max(r.SentBytes, reached) }, ct);
        }
        if (run.Finished && !run.ResultSent && sent >= run.CleanBytes)
        {
            await session.SendAsync(AgentMessageTypes.OsUpdateResult, run.Result(), ct);
            await UpdateAsync(run.RunId, r => r with { ResultSent = true }, ct);
        }
    }

    private async Task UpdateAsync(Guid runId, Func<RunRecord, RunRecord> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (store.Runs().FirstOrDefault(r => r.RunId == runId) is { } current)
            {
                store.Save(change(current));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SendOutboxesAsync(AgentSession session, CancellationToken ct)
    {
        List<AptHistoryEntry> entries;
        List<PackageChangelog> changelogs;
        lock (historyOutbox)
        {
            entries = historyOutbox.Take(200).ToList();
        }
        if (entries.Count > 0)
        {
            await session.SendAsync(AgentMessageTypes.OsUpdateHistory, new OsUpdateHistoryMessage(entries), ct);
            lock (historyOutbox)
            {
                historyOutbox.RemoveRange(0, Math.Min(entries.Count, historyOutbox.Count));
            }
            await gate.WaitAsync(ct);
            try
            {
                var state = store.History();
                store.Save(state with { Reported = [.. state.Reported, .. entries.Select(e => e.Id)] });
            }
            finally
            {
                gate.Release();
            }
        }
        lock (changelogOutbox)
        {
            changelogs = changelogOutbox.Take(20).ToList();
        }
        if (changelogs.Count > 0)
        {
            await session.SendAsync(AgentMessageTypes.OsChangelogs, new OsChangelogsMessage(changelogs), ct);
            lock (changelogOutbox)
            {
                changelogOutbox.RemoveRange(0, Math.Min(changelogs.Count, changelogOutbox.Count));
            }
        }
    }

    // --- requests -------------------------------------------------------------------------------------------------------

    public async Task<Ack?> HandleAsync(AgentSession session, SignedContent request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case AgentMessageTypes.OsUpdateRun:
                var run = AgentJson.Deserialize<OsUpdateRunRequest>(request.Body);
                if (run is null)
                {
                    return new Ack(request.RequestId, false, "The request has no body.");
                }
                await gate.WaitAsync(ct);
                try
                {
                    if (store.Runs().Any(r => r.RunId == run.RunId))
                    {
                        // The same request again (a resend): it runs once.
                        return new Ack(request.RequestId, true);
                    }
                    var refusal = await Task.Run(() => Check(run), ct) ?? Start(run.RunId, run.Kind, run.Mode, OsUpdateTrigger.Person, run.RequestedByName);
                    if (refusal is not null)
                    {
                        logger.LogWarning("OS updates: PNMT asked for {Kind} {Mode}: refused. {Reason}", run.Kind, run.Mode, refusal);
                        Journal.Write($"PNMT asked to run apt-get {(run.Kind == OsUpdateKind.Update ? "update" : run.Mode == OsUpgradeMode.FullUpgrade ? "full-upgrade" : "upgrade")}"
                                      + $"{(run.RequestedByName is { } who ? $" for {who}" : "")}: refused. {refusal}", warning: true);
                        return new Ack(request.RequestId, false, refusal);
                    }
                    return new Ack(request.RequestId, true);
                }
                finally
                {
                    gate.Release();
                }
            case AgentMessageTypes.OsUpdateLog:
                var resend = AgentJson.Deserialize<OsUpdateLogRequest>(request.Body);
                if (resend is null)
                {
                    return new Ack(request.RequestId, false, "The request has no body.");
                }
                await gate.WaitAsync(ct);
                try
                {
                    if (store.Runs().FirstOrDefault(r => r.RunId == resend.RunId) is not { } known)
                    {
                        return new Ack(request.RequestId, false, "The server no longer has this run's log.");
                    }
                    store.Save(known with { SentBytes = Math.Clamp(resend.FromOffset, 0, known.CleanBytes), ResultSent = false });
                    return new Ack(request.RequestId, true);
                }
                finally
                {
                    gate.Release();
                }
            case AgentMessageTypes.OsUpdateReceived:
                var received = AgentJson.Deserialize<OsUpdateReceivedRequest>(request.Body);
                await gate.WaitAsync(ct);
                try
                {
                    foreach (var id in received?.RunIds ?? [])
                    {
                        if (store.Runs().FirstOrDefault(r => r.RunId == id) is { Finished: true } done)
                        {
                            store.Save(done with { Acknowledged = true });
                        }
                    }
                    store.Prune();
                    return new Ack(request.RequestId, true);
                }
                finally
                {
                    gate.Release();
                }
            default:
                return null;
        }
    }

    // --- history.log (§4.5) ----------------------------------------------------------------------------------------------

    /// <summary>Other runs: history.log's entries that aren't PNMT's (they carry its tag), not reported yet; the first time the last 30 days.</summary>
    private async Task ScanHistoryAsync(CancellationToken ct)
    {
        if (!File.Exists(HistoryLog))
        {
            return;
        }
        HistoryState state;
        await gate.WaitAsync(ct);
        try
        {
            state = store.History();
        }
        finally
        {
            gate.Release();
        }
        var reported = state.Reported.ToHashSet(StringComparer.Ordinal);
        lock (historyOutbox)
        {
            reported.UnionWith(historyOutbox.Select(e => e.Id));
        }
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var fresh = ReadHistoryEntries(includeRotated: !state.FirstPassDone)
            .Where(e => e.Commandline?.Contains("PNMT::Run=", StringComparison.Ordinal) != true)
            .Where(e => !reported.Contains(e.Id))
            .Where(e => e.StartedAt >= since)
            .Select(e => e with
            {
                Commandline = e.Commandline is null ? null : Cut(e.Commandline, 1000),
                RequestedBy = e.RequestedBy is null ? null : Cut(e.RequestedBy, 200),
                Upgraded = e.Upgraded.Take(2000).ToList(),
                Installed = e.Installed.Take(2000).ToList(),
                Removed = e.Removed.Take(2000).ToList(),
                Error = e.Error is null ? null : Cut(e.Error, 1000),
            })
            .ToList();
        if (fresh.Count > 0)
        {
            lock (historyOutbox)
            {
                historyOutbox.AddRange(fresh);
            }
        }
        if (!state.FirstPassDone)
        {
            await gate.WaitAsync(ct);
            try
            {
                store.Save(store.History() with { FirstPassDone = true });
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>history.log's entries, oldest first; with the rotated <c>history.log.1.gz</c> … when asked (the first pass).</summary>
    private static List<AptHistoryEntry> ReadHistoryEntries(bool includeRotated)
    {
        var texts = new List<string>();
        if (includeRotated)
        {
            foreach (var rotated in Enumerable.Range(1, 4).Select(i => $"{HistoryLog}.{i}.gz").Where(File.Exists).Reverse())
            {
                try
                {
                    using var file = File.OpenRead(rotated);
                    using var gzip = new GZipStream(file, CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip);
                    texts.Add(reader.ReadToEnd());
                }
                catch (Exception e) when (e is IOException or InvalidDataException)
                {
                }
            }
        }
        if (File.Exists(HistoryLog))
        {
            texts.Add(File.ReadAllText(HistoryLog));
        }
        return texts.SelectMany(t => AptParsers.ParseHistory(t, TimeZoneInfo.Local)).ToList();
    }

    // --- changelogs (§6.2) ------------------------------------------------------------------------------------------------

    /// <summary>After each update (and 10 minutes after a start, then every 6 hours): the changelogs of pending source versions not fetched yet.</summary>
    private async Task ChangelogsLoopAsync(CancellationToken ct)
    {
        try
        {
            await changelogsWanted.WaitAsync(TimeSpan.FromMinutes(10), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await FetchChangelogsAsync(ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    problems.Report("os-updates", "fetching changelogs", e);
                }
                await changelogsWanted.WaitAsync(TimeSpan.FromHours(6), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task FetchChangelogsAsync(CancellationToken ct)
    {
        if (!Rights.Changelogs || sections.Latest(SectionNames.OsUpdates) is not { } latest)
        {
            return;
        }
        var section = AgentJson.Deserialize<OsUpdatesSection>(latest.Data);
        if (section?.Distribution is not { } distribution)
        {
            return;
        }
        Dictionary<string, DateOnly> fetched;
        await gate.WaitAsync(ct);
        try
        {
            fetched = store.Changelogs();
        }
        finally
        {
            gate.Release();
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var budget = Changelogs.PerDay - fetched.Count(p => p.Value == today);
        foreach (var wanted in Changelogs.Wanted(section.Packages, fetched).Take(Math.Max(0, budget)))
        {
            ct.ThrowIfCancellationRequested();
            var changelog = await Task.Run(() => Changelogs.Fetch(distribution, wanted.Source, wanted.Version, wanted.Installed, wanted.Binary, wanted.Candidate, store.Folder), ct);
            if (changelog is not null)
            {
                lock (changelogOutbox)
                {
                    changelogOutbox.Add(changelog);
                }
            }
            fetched[Changelogs.Key(wanted.Source, wanted.Version)] = today;
            await gate.WaitAsync(ct);
            try
            {
                store.Save(fetched);
            }
            finally
            {
                gate.Release();
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}
