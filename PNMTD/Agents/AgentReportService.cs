using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Agents;

/// <summary>
/// What agents report (docs/windows-sensor-plan.md §3.5, §3.8, §8.2), with the codes of their platform
/// (docs/linux-sensor-plan.md §9.2): heartbeats become OK (250, 260), or a restart (251, 261) after the boot time moved, or an
/// agent problem (451, 461) for a clock that is off or problems in three heartbeats in a row; on Linux also 466 while a high-risk
/// update is pending and unacknowledged, 461 while config.yaml is refused, 467 for a failed upgrade, interrupted dpkg or stale lists
/// (docs/linux-os-updates-plan.md §5.4), and 462 while security updates are pending too long (the server's switches). Problems go to
/// the agent log; <c>bye</c> marks the server uninstalled.
/// </summary>
public sealed class AgentReportService(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<AgentOptions> options, AgentChanges changes,
    OsUpdates.OsUpdatePackages osUpdates, IOptionsMonitor<OsUpdates.OsUpdatesOptions> osUpdateOptions, ILogger<AgentReportService> logger)
{
    /// <summary>A boot time that moved by more than this is a restart (clock adjustments move it a little).</summary>
    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>At most this many problems are kept from one message.</summary>
    public const int MaxProblemsPerMessage = 50;

    public async Task HeartbeatAsync(AgentConnection connection, Heartbeat heartbeat, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var agent = await db.Agents.Include(a => a.Sensor).ThenInclude(s => s.Parent).SingleOrDefaultAsync(a => a.SensorId == connection.SensorId, ct);
        if (agent is null || agent.AgentId != connection.AgentId)
        {
            return;
        }
        var now = Clock.UtcNow;
        var skew = (int)Math.Round((heartbeat.AgentTime.UtcDateTime - now).TotalSeconds);
        var boot = heartbeat.BootTime.UtcDateTime;
        var restarted = agent.BootTime is { } previous && boot - previous > BootTolerance;
        agent.ProblemHeartbeats = heartbeat.ProblemsSinceLast > 0 ? agent.ProblemHeartbeats + 1 : 0;
        agent.LastSeenAt = now;
        agent.BootTime = boot;
        agent.ClockSkewSeconds = skew;
        agent.ConfigVersion = heartbeat.ConfigVersion;
        agent.AgentVersion = Cuts.Cut(heartbeat.AgentVersion, 50);
        if (Cuts.CutOrNull(heartbeat.Hostname, 255) is { } hostname)
        {
            agent.Hostname = hostname;
        }
        agent.Heartbeat = AgentJson.Serialize(heartbeat with
        {
            Sessions = heartbeat.Sessions.Take(50).ToList(),
            SectionHashes = heartbeat.SectionHashes.Take(20).ToDictionary(),
        });

        var platform = AgentPlatformInfo.Of(agent.Platform);
        var (code, message) = Condition(platform, agent, heartbeat, skew, restarted);
        if (agent.Sensor.Enabled && agent.Sensor.Parent.Enabled)
        {
            await SensorEventWriter.WriteAsync(db, agent.SensorId, code, message, platform.EventSource, ct);
        }
        if (restarted)
        {
            AgentLog.Add(db, agent.SensorId, connection.AgentName, AgentLogKind.Warning, message, address: connection.Address);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private (int Code, string Message) Condition(AgentPlatformInfo platform, AgentEntity agent, Heartbeat heartbeat, int skew, bool restarted)
    {
        var maxSkew = options.CurrentValue.MaxClockSkewSeconds;
        if (Math.Abs(skew) > maxSkew)
        {
            return (platform.ProblemCode,
                $"{agent.Hostname}: the clock is off by {Durations.Text(TimeSpan.FromSeconds(skew))} ({(skew > 0 ? "ahead" : "behind")}{(platform.ClockNote is { } note ? "; " + note : "")})");
        }
        if (agent.ProblemHeartbeats >= 3)
        {
            return (platform.ProblemCode,
                $"{agent.Hostname}: the agent reported problems in {agent.ProblemHeartbeats} heartbeats in a row (see the Agent tab)");
        }
        if (restarted)
        {
            var at = heartbeat.BootTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            // Linux (docs/linux-sensor-plan.md §9.2): the message says whether the last shutdown was clean, or that it isn't known.
            var shutdown = !string.IsNullOrWhiteSpace(heartbeat.LastShutdown) ? "; " + Cuts.Cut(heartbeat.LastShutdown, 300)
                : platform.Platform == AgentPlatform.Linux ? "; whether the last shutdown was clean is not known" : "";
            return (platform.RestartedCode, $"{agent.Hostname}: restarted at {at}{shutdown}");
        }
        if (platform.Platform == AgentPlatform.Linux)
        {
            // docs/linux-os-updates-plan.md §5.4: a high-risk update before everything that lasts; its message is kept by the evaluator.
            var updates = osUpdateOptions.CurrentValue;
            if (updates.Enabled && updates.Advisories.Enabled && agent.OsUpdatesEnabled && agent.HighRiskEvents && agent.HighRiskMessage is { Length: > 0 } highRisk)
            {
                return (PNMTStatusCodes.LINUX_HIGH_RISK_UPDATE, highRisk);
            }
            // §4.6: an unsafe or broken config.yaml grants nothing, and is a problem while it lasts (461).
            if (ServerRights.Read(agent.Rights) is { } rights && ServerRights.IsRefused(rights))
            {
                return (platform.ProblemCode,
                    $"{agent.Hostname}: /etc/pnmt-agent/config.yaml is refused{(rights.Problem is { } problem ? $" ({Cuts.Cut(problem, 300)})" : "")}: it grants PNMT nothing");
            }
            // 467: kept by OsUpdateConditions (null while OS updates are off for the server or in PNMT).
            if (agent.UpdateFailedEvents && agent.UpdateFailedMessage is { Length: > 0 } failed)
            {
                return (PNMTStatusCodes.LINUX_UPDATE_FAILED, failed);
            }
            if (SecurityUpdatesMessage(agent, Clock.UtcNow) is { } securityUpdates)
            {
                return (PNMTStatusCodes.LINUX_SECURITY_UPDATES, securityUpdates);
            }
        }
        return (platform.OkCode, platform.OkMessage(agent));
    }

    /// <summary>
    /// 462 (docs/linux-sensor-plan.md §9.2), when the server's switch is on and security updates have been pending longer than its
    /// days: "web-01: 12 security updates pending since 2026-11-05, more than 7 days: openssl, libssl3, …". Stable while the same
    /// updates are pending, so it collapses by the dedupe.
    /// </summary>
    public static string? SecurityUpdatesMessage(AgentEntity agent, DateTime now)
    {
        if (!agent.SecurityUpdateEvents || agent.SecurityUpdates <= 0 || agent.SecurityUpdatesSince is not { } since)
        {
            return null;
        }
        var days = Math.Clamp(agent.SecurityUpdateDays, 1, 365);
        if (now - since <= TimeSpan.FromDays(days))
        {
            return null;
        }
        var what = agent.SecurityUpdates == 1 ? "1 security update" : $"{agent.SecurityUpdates} security updates";
        return $"{agent.Hostname}: {what} pending since {since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}, more than {days} days"
            + (string.IsNullOrEmpty(agent.SecurityPackages) ? "" : ": " + agent.SecurityPackages);
    }

    public async Task ProblemsAsync(AgentConnection connection, ProblemBatch batch, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var problems = batch.Problems.Take(MaxProblemsPerMessage).ToList();
        foreach (var problem in problems)
        {
            var count = problem.Count > 1 ? $" ({problem.Count}× since {problem.FirstAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC)" : "";
            AgentLog.Add(db, connection.SensorId, connection.AgentName, problem.Fatal ? AgentLogKind.Crashed : AgentLogKind.Problem,
                $"{Cuts.Cut(problem.Component, 100)}, {Cuts.Cut(problem.Operation, 100)}: {Cuts.Cut(problem.Message, 1000)}{count}",
                new
                {
                    Component = Cuts.Cut(problem.Component, 100),
                    Operation = Cuts.Cut(problem.Operation, 100),
                    ExceptionType = Cuts.CutOrNull(problem.ExceptionType, 200),
                    Stack = Cuts.CutOrNull(problem.Stack, 4000),
                    problem.Count,
                    problem.FirstAt,
                    problem.LastAt,
                    problem.Fatal,
                },
                connection.Address);
        }
        if (batch.Problems.Count > problems.Count)
        {
            AgentLog.Add(db, connection.SensorId, connection.AgentName, AgentLogKind.Warning,
                $"{batch.Problems.Count - problems.Count} more problems in the same message were dropped.", address: connection.Address);
        }
        if (problems.Count > 0)
        {
            await db.Agents.Where(a => a.SensorId == connection.SensorId)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LastProblemAt, Clock.UtcNow), ct);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A section (§4): one its platform sends, stored when its hash is new, compared with the one it replaces (§4.9).</summary>
    public async Task SectionAsync(AgentConnection connection, SectionMessage message, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Sensor).ThenInclude(s => s.Parent).SingleOrDefaultAsync(a => a.SensorId == connection.SensorId, ct);
        if (agent is null || agent.AgentId != connection.AgentId)
        {
            logger.LogDebug("Agent {Name}: the section {Section} ignored, the agent is not this server's (any more)", connection.AgentName, message.Name);
            return;
        }
        if (!SectionNames.IsKnown(agent.Platform, message.Name))
        {
            throw new InvalidOperationException($"An unknown section \"{Cuts.Cut(message.Name, 40)}\".");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (row, previous) = await AgentSections.StoreAsync(db, connection.SensorId, message, ct);
        // A new hash (or the first): both times were set to the same now.
        await changes.StoredAsync(db, agent, message, previous, row.ChangedAt == row.ReceivedAt, ct);
        if (agent.Platform == AgentPlatform.Linux && message.Name == SectionNames.OsUpdates)
        {
            // docs/linux-os-updates-plan.md §4.1: the pending packages, their counts, the lists' age and dpkg's state.
            await osUpdates.SectionAsync(db, agent, message, ct);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogDebug("Agent {Name}: the section {Section} stored ({Bytes} bytes, hash {Hash}, {Errors} errors, {State})", connection.AgentName, message.Name,
            message.Data.GetRawText().Length, message.Hash, message.Errors?.Count ?? 0, row.ChangedAt == row.ReceivedAt ? "changed" : "unchanged");
    }

    public async Task ByeAsync(AgentConnection connection, Bye bye, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.SensorId == connection.SensorId, ct);
        if (agent is null || agent.AgentId != connection.AgentId)
        {
            return;
        }
        agent.UninstalledAt = Clock.UtcNow;
        AgentLog.Add(db, connection.SensorId, connection.AgentName, AgentLogKind.Uninstalled,
            $"The service was uninstalled: {Cuts.Cut(bye.Reason, 500)}. The server and its history stay.", address: connection.Address);
        await db.SaveChangesAsync(ct);
    }
}
