using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Agents;

/// <summary>
/// Logins and connections (docs/windows-sensor-plan.md §4.7; docs/linux-sensor-plan.md §5.6), of every platform: stored once
/// per log and record id (a record sent again updates its count, its last time and its logoff); the platform's admin login
/// (254, 264) for a new admin login of a kind that pages there (Windows: console, RDP, SSH, WinRM); its login failures
/// (454, 464) for a burst of failed logins from one source or for one user, or a lockout. Both optional per server, on by
/// default.
/// </summary>
public sealed class AgentAccessService(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<AgentOptions> options)
{
    public const int MaxPerBatch = 1000;

    public async Task AccessAsync(AgentConnection connection, AccessBatch batch, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var agent = await db.Agents.Include(a => a.Sensor).ThenInclude(s => s.Parent).SingleOrDefaultAsync(a => a.SensorId == connection.SensorId, ct);
        if (agent is null || agent.AgentId != connection.AgentId)
        {
            return;
        }
        var platform = AgentPlatformInfo.Of(agent.Platform);
        var records = batch.Entries.Take(MaxPerBatch).ToList();
        var keys = records.Select(r => (Channel: Cuts.Cut(r.Channel, 200), r.RecordId)).ToList();
        var channels = keys.Select(k => k.Channel).Distinct().ToList();
        var ids = keys.Select(k => k.RecordId).Distinct().ToList();
        var existing = await db.AgentLogins.Where(l => l.SensorId == agent.SensorId && channels.Contains(l.Channel) && ids.Contains(l.RecordId))
            .ToDictionaryAsync(l => (l.Channel, l.RecordId), ct);
        var fresh = new List<AgentLoginEntity>();
        foreach (var record in records)
        {
            var key = (Cuts.Cut(record.Channel, 200), record.RecordId);
            if (!existing.TryGetValue(key, out var row))
            {
                row = new AgentLoginEntity { Id = Guid.NewGuid(), SensorId = agent.SensorId, Channel = key.Item1, RecordId = record.RecordId };
                db.AgentLogins.Add(row);
                existing[key] = row;
                fresh.Add(row);
            }
            Apply(row, record);
        }

        var now = Clock.UtcNow;
        var write = agent.Sensor.Enabled && agent.Sensor.Parent.Enabled;
        if (write && agent.AdminLoginEvents)
        {
            foreach (var login in fresh.Where(l => l.Outcome == LogonOutcome.Success && l.IsAdmin && platform.AdminLoginKinds.Contains(l.Kind) && l.At > now.AddHours(-1)))
            {
                await SensorEventWriter.WriteAsync(db, agent.SensorId, platform.AdminLoginCode, AdminMessage(login), platform.EventSource, ct);
            }
        }
        await db.SaveChangesAsync(ct);
        if (write && agent.FailedLoginEvents && records.Any(r => r.Outcome == LogonOutcome.Failure))
        {
            foreach (var message in await BurstsAsync(db, agent, records, ct))
            {
                await SensorEventWriter.WriteAsync(db, agent.SensorId, platform.LoginFailuresCode, message, platform.EventSource, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    private static void Apply(AgentLoginEntity row, LoginRecord record)
    {
        row.At = record.At.UtcDateTime;
        row.LastAt = record.LastAt?.UtcDateTime;
        row.Count = Math.Max(1, record.Count);
        row.Outcome = record.Outcome;
        row.FailureReason = Cuts.CutOrNull(record.FailureReason, 100);
        row.User = Cuts.Cut(record.User, 256);
        row.Domain = Cuts.CutOrNull(record.Domain, 256);
        row.IsAdmin = record.IsAdmin;
        row.Kind = record.Kind;
        row.AuthPackage = Cuts.CutOrNull(record.AuthPackage, 50);
        row.SourceAddress = Cuts.CutOrNull(record.SourceAddress, 100);
        row.SourcePort = record.SourcePort;
        row.Workstation = Cuts.CutOrNull(record.Workstation, 256);
        row.Elevated = record.Elevated;
        row.LogonId = Cuts.CutOrNull(record.LogonId, 40);
        row.LoggedOffAt = record.LoggedOffAt?.UtcDateTime ?? row.LoggedOffAt;
    }

    /// <summary>"CONTOSO\jdoe: RDP from 10.0.4.17 (Kerberos)" (§4.7).</summary>
    public static string AdminMessage(AgentLoginEntity login)
    {
        var how = login.Kind switch
        {
            LogonKind.Rdp => "RDP",
            LogonKind.Ssh => "SSH",
            LogonKind.WinRm => "WinRM",
            LogonKind.Console => "console",
            var other => other.ToString(),
        };
        var user = login.Domain is null ? login.User : $"{login.Domain}\\{login.User}";
        var from = login.SourceAddress is null ? "" : $" from {login.SourceAddress}";
        var details = string.Join(", ", new[] { login.AuthPackage, login.Elevated == true ? "elevated" : null }.Where(d => d is not null));
        return $"{user}: {how}{from}{(details.Length > 0 ? $" ({details})" : "")}";
    }

    /// <summary>
    /// The bursts (§4.7): the threshold of failed logins within the window from one source or for one user, and lockouts. The
    /// message is stable while a burst lasts, so it collapses by the dedupe.
    /// </summary>
    private async Task<List<string>> BurstsAsync(PnmtdDbContext db, AgentEntity agent, IReadOnlyList<LoginRecord> records, CancellationToken ct)
    {
        var minutes = Math.Clamp(options.CurrentValue.FailedLoginWindowMinutes, 1, 60);
        var since = Clock.UtcNow.AddMinutes(-minutes);
        var failures = await db.AgentLogins.AsNoTracking()
            .Where(l => l.SensorId == agent.SensorId && l.Outcome == LogonOutcome.Failure && (l.LastAt ?? l.At) >= since)
            .Select(l => new { l.User, l.Domain, l.SourceAddress, l.Count, l.Kind, l.Workstation })
            .ToListAsync(ct);
        var threshold = Math.Max(2, agent.FailedLoginThreshold);
        var messages = new List<string>();
        foreach (var source in failures.Where(f => f.SourceAddress is not null && f.Kind != LogonKind.Lockout).GroupBy(f => f.SourceAddress!))
        {
            var count = source.Sum(f => f.Count);
            if (count >= threshold)
            {
                var users = source.Select(f => f.User).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
                messages.Add($"Failed logins from {source.Key}: at least {threshold} in {minutes} min (users: {string.Join(", ", users)})");
            }
        }
        foreach (var user in failures.Where(f => f.Kind != LogonKind.Lockout).GroupBy(f => f.Domain is null ? f.User : $"{f.Domain}\\{f.User}", StringComparer.OrdinalIgnoreCase))
        {
            var count = user.Sum(f => f.Count);
            if (count >= threshold && user.Select(f => f.SourceAddress).Distinct().Count() > 1)
            {
                messages.Add($"Failed logins for {user.Key}: at least {threshold} in {minutes} min from several sources");
            }
        }
        foreach (var lockout in records.Where(r => r.Kind == LogonKind.Lockout))
        {
            messages.Add($"{lockout.User} locked out{(lockout.Workstation is null ? "" : $" (from {lockout.Workstation})")}");
        }
        return messages.Distinct().Take(10).ToList();
    }
}
