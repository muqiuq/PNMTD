using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Windows;

/// <summary>
/// What agents report (docs/windows-sensor-plan.md §3.5, §3.8, §8.2): heartbeats become 250, or 251 after a restart, or
/// 451 for a clock that is off or problems in three heartbeats in a row; problems go to the agent log; <c>bye</c> marks the
/// server uninstalled.
/// </summary>
public sealed class WindowsReportService(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<WindowsOptions> options, WindowsChanges changes,
    ILogger<WindowsReportService> logger)
{
    /// <summary>The source of the events PNMTD writes for agents: one, so the dedupe collapses repeated heartbeats.</summary>
    public const string EventSource = "windows-agent";

    /// <summary>A boot time that moved by more than this is a restart (clock adjustments move it a little).</summary>
    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    /// <summary>At most this many problems are kept from one message.</summary>
    public const int MaxProblemsPerMessage = 50;

    public async Task HeartbeatAsync(AgentConnection connection, Heartbeat heartbeat, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var agent = await db.WindowsAgents.Include(a => a.Sensor).ThenInclude(s => s.Parent).SingleOrDefaultAsync(a => a.SensorId == connection.SensorId, ct);
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

        var (code, message) = Condition(agent, heartbeat, skew, restarted);
        if (agent.Sensor.Enabled && agent.Sensor.Parent.Enabled)
        {
            await SensorEventWriter.WriteAsync(db, agent.SensorId, code, message, EventSource, ct);
        }
        if (restarted)
        {
            WindowsAgentLog.Add(db, agent.SensorId, connection.AgentName, WindowsAgentLogKind.Warning, message, address: connection.Address);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private (int Code, string Message) Condition(WindowsAgentEntity agent, Heartbeat heartbeat, int skew, bool restarted)
    {
        var maxSkew = options.CurrentValue.MaxClockSkewSeconds;
        if (Math.Abs(skew) > maxSkew)
        {
            return (PNMTStatusCodes.WINDOWS_AGENT_PROBLEM,
                $"{agent.Hostname}: the clock is off by {Durations.Text(TimeSpan.FromSeconds(skew))} ({(skew > 0 ? "ahead" : "behind")}; Kerberos fails beyond 5 min)");
        }
        if (agent.ProblemHeartbeats >= 3)
        {
            return (PNMTStatusCodes.WINDOWS_AGENT_PROBLEM,
                $"{agent.Hostname}: the agent reported problems in {agent.ProblemHeartbeats} heartbeats in a row (see the Agent tab)");
        }
        if (restarted)
        {
            var at = heartbeat.BootTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            return (PNMTStatusCodes.WINDOWS_RESTARTED,
                $"{agent.Hostname}: restarted at {at}{(string.IsNullOrWhiteSpace(heartbeat.LastShutdown) ? "" : "; " + Cuts.Cut(heartbeat.LastShutdown, 300))}");
        }
        return (PNMTStatusCodes.WINDOWS_OK, OkMessage(agent));
    }

    /// <summary>Stable from heartbeat to heartbeat, so they collapse by the dedupe: "SRV01: Windows Server 2019 Standard 17763, agent 1.3.0".</summary>
    public static string OkMessage(WindowsAgentEntity agent) =>
        $"{agent.Hostname}: {agent.OsCaption ?? "Windows"}{(agent.OsBuild is { } build ? " " + build : "")}, agent {agent.AgentVersion}";

    public async Task ProblemsAsync(AgentConnection connection, ProblemBatch batch, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var problems = batch.Problems.Take(MaxProblemsPerMessage).ToList();
        foreach (var problem in problems)
        {
            var count = problem.Count > 1 ? $" ({problem.Count}× since {problem.FirstAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC)" : "";
            WindowsAgentLog.Add(db, connection.SensorId, connection.AgentName, problem.Fatal ? WindowsAgentLogKind.Crashed : WindowsAgentLogKind.Problem,
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
            WindowsAgentLog.Add(db, connection.SensorId, connection.AgentName, WindowsAgentLogKind.Warning,
                $"{batch.Problems.Count - problems.Count} more problems in the same message were dropped.", address: connection.Address);
        }
        if (problems.Count > 0)
        {
            await db.WindowsAgents.Where(a => a.SensorId == connection.SensorId)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LastProblemAt, Clock.UtcNow), ct);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A section (§4): stored when its hash is new, compared with the one it replaces (§4.9).</summary>
    public async Task SectionAsync(AgentConnection connection, SectionMessage message, CancellationToken ct)
    {
        if (!SectionNames.IsKnown(message.Name))
        {
            throw new InvalidOperationException($"An unknown section \"{Cuts.Cut(message.Name, 40)}\".");
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agent = await db.WindowsAgents.Include(a => a.Sensor).ThenInclude(s => s.Parent).SingleOrDefaultAsync(a => a.SensorId == connection.SensorId, ct);
        if (agent is null || agent.AgentId != connection.AgentId)
        {
            logger.LogDebug("Agent {Name}: the section {Section} ignored, the agent is not this server's (any more)", connection.AgentName, message.Name);
            return;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (row, previous) = await WindowsSections.StoreAsync(db, connection.SensorId, message, ct);
        await changes.StoredAsync(db, agent, message, previous, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogDebug("Agent {Name}: the section {Section} stored ({Bytes} bytes, hash {Hash}, {Errors} errors, {State})", connection.AgentName, message.Name,
            message.Data.GetRawText().Length, message.Hash, message.Errors?.Count ?? 0, row.ChangedAt == row.ReceivedAt ? "changed" : "unchanged");
    }

    public async Task ByeAsync(AgentConnection connection, Bye bye, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agent = await db.WindowsAgents.SingleOrDefaultAsync(a => a.SensorId == connection.SensorId, ct);
        if (agent is null || agent.AgentId != connection.AgentId)
        {
            return;
        }
        agent.UninstalledAt = Clock.UtcNow;
        WindowsAgentLog.Add(db, connection.SensorId, connection.AgentName, WindowsAgentLogKind.Uninstalled,
            $"The service was uninstalled: {Cuts.Cut(bye.Reason, 500)}. The server and its history stay.", address: connection.Address);
        await db.SaveChangesAsync(ct);
    }
}
