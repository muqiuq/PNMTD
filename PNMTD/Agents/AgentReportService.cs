using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Agents;

/// <summary>
/// What agents report (docs/windows-sensor-plan.md §3.5, §3.8, §8.2), with the codes of their platform
/// (docs/linux-sensor-plan.md §9.2): heartbeats become OK (250, 260), or a restart (251, 261) after the boot time moved, or an
/// agent problem (451, 461) for a clock that is off or problems in three heartbeats in a row; problems go to the agent log;
/// <c>bye</c> marks the server uninstalled.
/// </summary>
public sealed class AgentReportService(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<AgentOptions> options, AgentChanges changes,
    ILogger<AgentReportService> logger)
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
            return (platform.RestartedCode,
                $"{agent.Hostname}: restarted at {at}{(string.IsNullOrWhiteSpace(heartbeat.LastShutdown) ? "" : "; " + Cuts.Cut(heartbeat.LastShutdown, 300))}");
        }
        return (platform.OkCode, platform.OkMessage(agent));
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
        await changes.StoredAsync(db, agent, message, previous, ct);
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
