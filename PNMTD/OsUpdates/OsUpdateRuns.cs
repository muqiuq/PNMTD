using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.OsUpdates;

/// <summary>
/// The agents' side of OS updates on PNMTD (docs/linux-os-updates-plan.md §4.5, §5.1, §5.3): a run's start, its log in order (a gap asks
/// the agent for the missing part; a repeat is skipped), its end (stored once; checked against the log's hash; acknowledged so the agent
/// forgets it), other runs from history.log, and changelogs. Retention keeps the newest 50 update and 200 upgrade runs per server. 265
/// goes out for each successful upgrade PNMT ran; 467's message is kept by <see cref="OsUpdateConditions"/>.
/// </summary>
public sealed class OsUpdateRuns(IDbContextFactory<PnmtdDbContext> dbs, AgentKeyStore keys, IOptionsMonitor<OsUpdatesOptions> options,
    OsUpdateConditions conditions, OsAdvisoryNudge nudge, ILogger<OsUpdateRuns> logger)
{
    /// <summary>The head a truncated log keeps (§5.3); the rest of <c>MaxLogBytes</c> is its end.</summary>
    public const long TruncatedHead = 1024 * 1024;

    /// <summary>When PNMTD last asked an agent to send a run's log again, per run: at most once a minute.</summary>
    private readonly ConcurrentDictionary<Guid, DateTime> resendAsked = new();

    // --- agent → PNMTD --------------------------------------------------------------------------------------------------------

    public async Task StartedAsync(AgentConnection connection, OsUpdateStartedMessage started, CancellationToken ct)
    {
        if (connection.Platform != AgentPlatform.Linux)
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var run = await db.OsUpdateRuns.SingleOrDefaultAsync(r => r.Id == started.RunId && r.SensorId == connection.SensorId, ct);
        var now = Clock.UtcNow;
        if (run is null)
        {
            run = NewRun(connection.SensorId, started.RunId, started.Kind, started.Mode, started.Trigger, started.RequestedByName, started.StartedAt.UtcDateTime);
            db.OsUpdateRuns.Add(run);
        }
        if (run.State is OsUpdateState.Requested or OsUpdateState.Lost)
        {
            run.State = OsUpdateState.Running;
        }
        run.StartedAt ??= started.StartedAt.UtcDateTime;
        run.Command ??= Cuts.Cut(started.Command, 1000);
        run.LastHeardAt = now;
        if (run.Trigger != OsUpdateTrigger.Person)
        {
            AgentLog.Add(db, connection.SensorId, connection.AgentName, AgentLogKind.Request, $"OS updates: {Cuts.Cut(started.Command, 300)} started ({Trigger(run)}).",
                address: connection.Address);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task OutputAsync(AgentConnection connection, OsUpdateOutputMessage output, CancellationToken ct)
    {
        if (connection.Platform != AgentPlatform.Linux)
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var run = await db.OsUpdateRuns.SingleOrDefaultAsync(r => r.Id == output.RunId && r.SensorId == connection.SensorId, ct);
        if (run is null || output.Offset < 0)
        {
            return;
        }
        run.LastHeardAt = Clock.UtcNow;
        var bytes = Encoding.UTF8.GetBytes(output.Text.Length > OsUpdateOutputMessage.MaxBytes * 2 ? output.Text[..(OsUpdateOutputMessage.MaxBytes * 2)] : output.Text);
        if (output.Offset > run.LogBytes)
        {
            // A part went missing (a dropped connection): the agent sends it again from where PNMTD is.
            await db.SaveChangesAsync(ct);
            await AskResendAsync(connection, run.Id, run.LogBytes, ct);
            return;
        }
        var skip = (int)(run.LogBytes - output.Offset);
        if (skip >= bytes.Length)
        {
            await db.SaveChangesAsync(ct);
            return;
        }
        await AppendAsync(db, run, bytes.AsSpan(skip).ToArray(), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stores a part of the log at <see cref="OsUpdateRunEntity.LogBytes"/>. Beyond <c>MaxLogBytes</c> the parts after the head go, oldest
    /// first, so the start and the end stay (§5.3); the offsets then jump, and the reader marks where.
    /// </summary>
    private async Task AppendAsync(PnmtdDbContext db, OsUpdateRunEntity run, byte[] bytes, CancellationToken ct)
    {
        var text = Encoding.UTF8.GetString(bytes);
        db.OsUpdateLogChunks.Add(new OsUpdateLogChunkEntity { RunId = run.Id, Offset = run.LogBytes, Text = text, Bytes = bytes.Length });
        run.LogBytes += bytes.Length;
        var max = options.CurrentValue.MaxLog;
        var stored = await db.OsUpdateLogChunks.Where(c => c.RunId == run.Id).SumAsync(c => (long)c.Bytes, ct) + bytes.Length;
        if (stored <= max)
        {
            return;
        }
        run.LogTruncated = true;
        var middle = await db.OsUpdateLogChunks.Where(c => c.RunId == run.Id && c.Offset >= TruncatedHead).OrderBy(c => c.Offset)
            .Select(c => new { c.Offset, c.Bytes }).ToListAsync(ct);
        var drop = new List<long>();
        foreach (var chunk in middle)
        {
            if (stored <= max)
            {
                break;
            }
            drop.Add(chunk.Offset);
            stored -= chunk.Bytes;
        }
        if (drop.Count > 0)
        {
            await db.OsUpdateLogChunks.Where(c => c.RunId == run.Id && drop.Contains(c.Offset)).ExecuteDeleteAsync(ct);
        }
    }

    public async Task ResultAsync(AgentConnection connection, OsUpdateResultMessage result, CancellationToken ct)
    {
        if (connection.Platform != AgentPlatform.Linux)
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Sensor).ThenInclude(s => s.Parent).SingleOrDefaultAsync(a => a.SensorId == connection.SensorId, ct);
        if (agent is null || agent.AgentId != connection.AgentId)
        {
            return;
        }
        var run = await db.OsUpdateRuns.SingleOrDefaultAsync(r => r.Id == result.RunId && r.SensorId == connection.SensorId, ct);
        var firstEnd = run is null || !run.Finished || run.State == OsUpdateState.Lost;
        if (run is null)
        {
            run = NewRun(connection.SensorId, result.RunId, result.Kind, result.Mode, result.Trigger, result.RequestedByName, result.StartedAt.UtcDateTime);
            db.OsUpdateRuns.Add(run);
        }
        run.LastHeardAt = Clock.UtcNow;
        run.ReportedLogBytes = Math.Max(0, result.LogBytes);
        run.LogSha256 = Cuts.CutOrNull(result.LogSha256, 64);
        if (run.LogBytes < result.LogBytes && !run.LogIncomplete && resendAsked.ContainsKey(run.Id))
        {
            // PNMTD asked for the missing part already, and the end came again without it (the server's copy is gone): what is here stays.
            run.LogIncomplete = true;
        }
        if (run.LogBytes < result.LogBytes && !run.LogIncomplete)
        {
            // The end came before all of the log: the agent sends the rest, then the end again.
            run.Command ??= Cuts.CutOrNull(result.Command, 1000);
            run.StartedAt ??= result.StartedAt.UtcDateTime;
            if (run.State is OsUpdateState.Requested or OsUpdateState.Lost)
            {
                run.State = OsUpdateState.Running;
            }
            await db.SaveChangesAsync(ct);
            await AskResendAsync(connection, run.Id, run.LogBytes, ct, force: true);
            return;
        }
        if (firstEnd)
        {
            run.State = result.State is OsUpdateState.Succeeded or OsUpdateState.Warnings or OsUpdateState.Failed ? result.State : OsUpdateState.Failed;
            run.ExitCode = result.ExitCode;
            run.Summary = JsonSerializer.Serialize(Bounded(result.Summary), AgentJson.Options);
            run.Message = Cuts.CutOrNull(result.Message, 2000);
            run.StartedAt ??= result.StartedAt.UtcDateTime;
            run.FinishedAt = result.FinishedAt.UtcDateTime;
            run.Command ??= Cuts.CutOrNull(result.Command, 1000);
            if (!run.LogTruncated && result.LogBytes > 0 && !await HashMatchesAsync(db, run.Id, result.LogSha256, ct))
            {
                run.LogIncomplete = true;
            }
            if (run.Kind == OsUpdateKind.Upgrade)
            {
                agent.LastUpgradeAt = run.FinishedAt;
            }
            AgentLog.Add(db, connection.SensorId, connection.AgentName, run.State == OsUpdateState.Failed ? AgentLogKind.Warning : AgentLogKind.Request,
                $"OS updates: {Describe(run, result.Summary)}", address: connection.Address);
            if (run.Kind == OsUpdateKind.Upgrade && run.State is OsUpdateState.Succeeded or OsUpdateState.Warnings
                && agent.Sensor.Enabled && agent.Sensor.Parent.Enabled)
            {
                await SensorEventWriter.WriteAsync(db, agent.SensorId, PNMTStatusCodes.LINUX_UPDATES_INSTALLED,
                    Cuts.Cut(InstalledMessage(agent.Hostname, run, result.Summary), 1000), AgentPlatformInfo.Of(AgentPlatform.Linux).EventSource, ct);
            }
            await db.SaveChangesAsync(ct);
            await PruneAsync(db, agent.SensorId, ct);
            await conditions.RefreshAsync(db, agent, ct);
            await db.SaveChangesAsync(ct);
            nudge.Sensor(agent.SensorId);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }
        resendAsked.TryRemove(run.Id, out _);
        await AcknowledgeAsync(connection, [run.Id], ct);
    }

    /// <summary>Runs outside PNMT from history.log (§4.5): each entry once per server, as an upgrade run without a log.</summary>
    public async Task HistoryAsync(AgentConnection connection, OsUpdateHistoryMessage history, CancellationToken ct)
    {
        if (connection.Platform != AgentPlatform.Linux)
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.SensorId == connection.SensorId, ct);
        if (agent is null || agent.AgentId != connection.AgentId)
        {
            return;
        }
        var entries = history.Entries.Take(500).Where(e => !string.IsNullOrEmpty(e.Id)).ToList();
        var ids = entries.Select(e => Cuts.Cut(e.Id, 32)).ToList();
        var known = await db.OsUpdateRuns.Where(r => r.SensorId == connection.SensorId && r.ExternalId != null && ids.Contains(r.ExternalId))
            .Select(r => r.ExternalId!).ToListAsync(ct);
        var added = 0;
        foreach (var entry in entries.Where(e => !known.Contains(Cuts.Cut(e.Id, 32))).DistinctBy(e => e.Id))
        {
            var summary = new OsUpdateSummary(entry.Upgraded, entry.Installed, entry.Removed, [], [], false, [], entry.Error is null ? [] : [entry.Error]);
            db.OsUpdateRuns.Add(new OsUpdateRunEntity
            {
                Id = Guid.NewGuid(), SensorId = connection.SensorId, Kind = OsUpdateKind.Upgrade, Trigger = OsUpdateTrigger.External,
                RequestedByName = Cuts.CutOrNull(entry.RequestedBy, 200), RequestedAt = entry.StartedAt.UtcDateTime, StartedAt = entry.StartedAt.UtcDateTime,
                FinishedAt = (entry.FinishedAt ?? entry.StartedAt).UtcDateTime, State = entry.Error is null ? OsUpdateState.Succeeded : OsUpdateState.Failed,
                Summary = JsonSerializer.Serialize(Bounded(summary), AgentJson.Options), Command = Cuts.CutOrNull(entry.Commandline, 1000),
                Message = Cuts.CutOrNull(entry.Error, 2000), ExternalId = Cuts.Cut(entry.Id, 32), LastHeardAt = Clock.UtcNow,
            });
            if (entry.Upgraded.Count > 0 && (agent.LastUpgradeAt is null || entry.StartedAt.UtcDateTime > agent.LastUpgradeAt))
            {
                agent.LastUpgradeAt = (entry.FinishedAt ?? entry.StartedAt).UtcDateTime;
            }
            added++;
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            await PruneAsync(db, connection.SensorId, ct);
        }
    }

    /// <summary>Changelog parts (§6.2): stored per distribution, source and version, so one server's fetch serves the fleet; the evaluator looks again.</summary>
    public async Task ChangelogsAsync(AgentConnection connection, OsChangelogsMessage message, CancellationToken ct)
    {
        if (connection.Platform != AgentPlatform.Linux)
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        foreach (var changelog in message.Changelogs.Take(50))
        {
            var distribution = Cuts.Cut(changelog.Distribution, 60);
            var source = Cuts.Cut(changelog.Source, 200);
            var version = Cuts.Cut(changelog.Version, 200);
            if (distribution.Length == 0 || source.Length == 0 || version.Length == 0)
            {
                continue;
            }
            var row = await db.OsPackageChangelogs.SingleOrDefaultAsync(c => c.Distribution == distribution && c.SourcePackage == source && c.Version == version, ct);
            if (row is null)
            {
                row = new OsPackageChangelogEntity { Distribution = distribution, SourcePackage = source, Version = version };
                db.OsPackageChangelogs.Add(row);
            }
            row.InstalledVersion = Cuts.CutOrNull(changelog.InstalledVersion, 200);
            row.Cves = changelog.Cves.Where(c => c.StartsWith("CVE-", StringComparison.Ordinal)).Select(c => Cuts.Cut(c, 40)).Distinct().Take(500).ToList();
            row.Urgencies = changelog.Urgencies.Select(u => Cuts.Cut(u, 20).ToLowerInvariant()).Distinct().Take(20).ToList();
            row.Text = changelog.Text.Length > PackageChangelog.MaxTextChars ? changelog.Text[..PackageChangelog.MaxTextChars] : changelog.Text;
            row.FetchedFrom = Cuts.CutOrNull(changelog.From, 40);
            row.ReceivedAt = Clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        // A year is enough: newer versions replace what matters.
        var cutoff = Clock.UtcNow.AddDays(-365);
        await db.OsPackageChangelogs.Where(c => c.ReceivedAt < cutoff).ExecuteDeleteAsync(ct);
        nudge.All();
    }

    // --- PNMTD → agent --------------------------------------------------------------------------------------------------------

    private async Task AskResendAsync(AgentConnection connection, Guid runId, long from, CancellationToken ct, bool force = false)
    {
        var now = Clock.UtcNow;
        if (!force && resendAsked.TryGetValue(runId, out var asked) && now - asked < TimeSpan.FromMinutes(1))
        {
            return;
        }
        resendAsked[runId] = now;
        await SendAsync(connection, AgentMessageTypes.OsUpdateLog, new OsUpdateLogRequest(runId, from), ct);
    }

    private Task AcknowledgeAsync(AgentConnection connection, IReadOnlyList<Guid> runIds, CancellationToken ct) =>
        SendAsync(connection, AgentMessageTypes.OsUpdateReceived, new OsUpdateReceivedRequest(runIds), ct);

    /// <summary>A signed request whose ack nothing waits for: a refusal is logged by the hub.</summary>
    private async Task SendAsync(AgentConnection connection, string type, object body, CancellationToken ct)
    {
        try
        {
            var requestId = AgentSigning.NewRequestId();
            var signed = await keys.SignAsync(type, connection.AgentId, body, ct, AgentProtocol.RequestLifetime, requestId, connection.Session);
            await connection.SendAsync(type, signed, ct, requestId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Agent {Name}: sending {Type} failed", connection.AgentName, type);
        }
    }

    // --- retention and the lost (§5.1, §5.3) ----------------------------------------------------------------------------------

    /// <summary>The newest <c>KeepUpdateLogs</c> update runs and <c>KeepUpgradeLogs</c> upgrade runs of a server; running ones always stay.</summary>
    public async Task PruneAsync(PnmtdDbContext db, Guid sensorId, CancellationToken ct)
    {
        var o = options.CurrentValue;
        foreach (var (kind, keep) in new[] { (OsUpdateKind.Update, o.KeepUpdates), (OsUpdateKind.Upgrade, o.KeepUpgrades) })
        {
            var old = await db.OsUpdateRuns.Where(r => r.SensorId == sensorId && r.Kind == kind)
                .OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id).Skip(keep)
                .Where(r => r.State != OsUpdateState.Running && r.State != OsUpdateState.Requested)
                .Select(r => r.Id).ToListAsync(ct);
            if (old.Count > 0)
            {
                await db.OsUpdateRuns.Where(r => old.Contains(r.Id)).ExecuteDeleteAsync(ct);
            }
        }
    }

    /// <summary>
    /// Once an hour (AgentMaintenanceTask): every server's retention, and runs that lost their server: requested without an answer for
    /// 10 minutes, or running without a word for 7 days.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var now = Clock.UtcNow;
        var requested = now.AddMinutes(-10);
        var silent = now.AddDays(-7);
        await db.OsUpdateRuns.Where(r => (r.State == OsUpdateState.Requested && r.RequestedAt < requested)
                                         || (r.State == OsUpdateState.Running && (r.LastHeardAt ?? r.RequestedAt) < silent))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, OsUpdateState.Lost).SetProperty(r => r.FinishedAt, now)
                .SetProperty(r => r.Message, "The server didn't report the run's end."), ct);
        var sensors = await db.OsUpdateRuns.Select(r => r.SensorId).Distinct().ToListAsync(ct);
        foreach (var sensorId in sensors)
        {
            await PruneAsync(db, sensorId, ct);
        }
    }

    // --- texts ---------------------------------------------------------------------------------------------------------------

    public static OsUpdateRunEntity NewRun(Guid sensorId, Guid runId, OsUpdateKind kind, OsUpgradeMode? mode, OsUpdateTrigger trigger, string? requestedBy, DateTime at) => new()
    {
        Id = runId, SensorId = sensorId, Kind = kind, Mode = kind == OsUpdateKind.Upgrade ? mode ?? OsUpgradeMode.Upgrade : null, Trigger = trigger,
        RequestedByName = Cuts.CutOrNull(requestedBy, 200), RequestedAt = at, StartedAt = at, State = OsUpdateState.Running,
    };

    public static string What(OsUpdateKind kind, OsUpgradeMode? mode) => kind == OsUpdateKind.Update ? "apt-get update"
        : mode == OsUpgradeMode.FullUpgrade ? "apt-get full-upgrade" : "apt-get upgrade";

    private static string Trigger(OsUpdateRunEntity run) => run.Trigger switch
    {
        OsUpdateTrigger.Schedule => "the daily update",
        OsUpdateTrigger.Server => "from the server's command line",
        OsUpdateTrigger.External => "outside PNMT",
        _ => run.RequestedByName is { } name ? $"for {name}" : "a person",
    };

    /// <summary>"apt-get full-upgrade for Philipp Albrecht: succeeded, 23 upgraded, 1 installed, 0 removed, exit 0."</summary>
    public static string Describe(OsUpdateRunEntity run, OsUpdateSummary summary)
    {
        var counts = run.Kind == OsUpdateKind.Update
            ? $"{summary.RepositoriesReached} repositories reached{(summary.RepositoriesFailed > 0 ? $", {summary.RepositoriesFailed} failed" : "")}"
            : $"{summary.Upgraded.Count} upgraded, {summary.Installed.Count} installed, {summary.Removed.Count} removed";
        return $"{What(run.Kind, run.Mode)} ({Trigger(run)}): {run.State.ToString().ToLowerInvariant()}, {counts}"
               + (run.ExitCode is { } code ? $", exit {code}" : "") + (run.Message is { } m ? $": {Cuts.Cut(m, 300)}" : "") + ".";
    }

    /// <summary>265 (§5.4): "web-01: 23 packages upgraded, 1 installed, 0 removed, for Philipp Albrecht; a reboot is required".</summary>
    public static string InstalledMessage(string hostname, OsUpdateRunEntity run, OsUpdateSummary summary)
    {
        var who = run.Trigger switch
        {
            OsUpdateTrigger.Person when run.RequestedByName is { } name => $", for {name}",
            OsUpdateTrigger.Server => ", from the server's command line",
            _ => "",
        };
        var restart = summary.ServicesToRestart.Count > 0 ? $"; {summary.ServicesToRestart.Count} services need a restart" : "";
        return $"{hostname}: {summary.Upgraded.Count} packages upgraded, {summary.Installed.Count} installed, {summary.Removed.Count} removed{who}"
               + (summary.RebootRequired ? "; a reboot is required" : "") + restart;
    }

    public static OsUpdateSummary Bounded(OsUpdateSummary s) => new(
        s.Upgraded.Take(5000).Select(Bounded).ToList(), s.Installed.Take(5000).Select(Bounded).ToList(), s.Removed.Take(5000).Select(Bounded).ToList(),
        s.ConfigFilesKept.Take(200).Select(c => Cuts.Cut(c, 500)).ToList(), s.ServicesToRestart.Take(200).Select(c => Cuts.Cut(c, 200)).ToList(),
        s.RebootRequired, s.Warnings.Take(50).Select(w => Cuts.Cut(w, 500)).ToList(), s.Errors.Take(50).Select(e => Cuts.Cut(e, 500)).ToList(),
        Math.Max(0, s.RepositoriesReached), Math.Max(0, s.RepositoriesFailed), Cuts.CutOrNull(s.Fetched, 200));

    private static PackageChange Bounded(PackageChange p) => new(Cuts.Cut(p.Name, 200), Cuts.CutOrNull(p.Arch, 40), Cuts.CutOrNull(p.From, 200), Cuts.CutOrNull(p.To, 200));

    public static OsUpdateSummary? ReadSummary(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<OsUpdateSummary>(json, AgentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<bool> HashMatchesAsync(PnmtdDbContext db, Guid runId, string expected, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunks = await db.OsUpdateLogChunks.Where(c => c.RunId == runId).OrderBy(c => c.Offset).Select(c => c.Text).ToListAsync(ct);
        foreach (var text in chunks)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(text));
        }
        return string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A run's log as stored: its parts in order, with a marker where the middle of a long log was left out.</summary>
    public static async Task<(string Text, long NextOffset)> ReadLogAsync(PnmtdDbContext db, Guid runId, long from, CancellationToken ct)
    {
        var chunks = await db.OsUpdateLogChunks.AsNoTracking().Where(c => c.RunId == runId && c.Offset + c.Bytes > from).OrderBy(c => c.Offset)
            .Select(c => new { c.Offset, c.Bytes, c.Text }).ToListAsync(ct);
        var builder = new StringBuilder();
        var expected = from;
        foreach (var chunk in chunks)
        {
            if (chunk.Offset > expected)
            {
                builder.Append(CultureInfo.InvariantCulture,
                    $"\n[… {Math.Round((chunk.Offset - expected) / 1024d / 1024d, 1)} MiB of this log were left out: PNMT keeps the start and the end of long logs …]\n");
            }
            var text = chunk.Text;
            if (chunk.Offset < from)
            {
                // A part that starts before the offset: from its first character at or after it.
                var skip = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)[..(int)Math.Min(from - chunk.Offset, chunk.Bytes)]).Length;
                text = text[Math.Min(skip, text.Length)..];
            }
            builder.Append(text);
            expected = chunk.Offset + chunk.Bytes;
        }
        return (builder.ToString(), Math.Max(expected, from));
    }
}
