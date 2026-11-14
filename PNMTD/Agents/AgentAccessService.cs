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
/// (254, 264) for a new admin login of a kind that pages there (Windows: console, RDP, SSH, WinRM; Linux: SSH and console,
/// with the key of a key login), within the last hour; its login failures (454, 464) for a burst of failed logins from one
/// source or for one user, or a lockout. On Linux a <c>not in sudoers</c> pages too, and failures for accounts that don't
/// exist (<see cref="LinuxLogins.InvalidUser"/>, the internet's scanners) never count. sudo and su are stored, never page. Both
/// optional per server, on by default.
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
            var admins = fresh.Where(l => l.Outcome == LogonOutcome.Success && l.IsAdmin && platform.AdminLoginKinds.Contains(l.Kind) && l.At > now.AddHours(-1)).ToList();
            var keyNames = agent.Platform == AgentPlatform.Linux ? await KeyNamesAsync(db, admins.Select(l => l.KeyFingerprint).OfType<string>().ToList(), ct) : null;
            foreach (var login in admins)
            {
                var message = agent.Platform == AgentPlatform.Linux ? LinuxAdminMessage(login, keyNames!) : AdminMessage(login);
                await SensorEventWriter.WriteAsync(db, agent.SensorId, platform.AdminLoginCode, message, platform.EventSource, ct);
            }
        }
        await db.SaveChangesAsync(ct);
        if (write && agent.FailedLoginEvents && records.Any(r => r.Outcome == LogonOutcome.Failure))
        {
            var messages = agent.Platform == AgentPlatform.Linux ? await LinuxBurstsAsync(db, agent, fresh, ct) : await BurstsAsync(db, agent, records, ct);
            foreach (var message in messages)
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
        // Linux (docs/linux-sensor-plan.md §5.6).
        row.KeyFingerprint = Cuts.CutOrNull(record.KeyFingerprint, 100);
        row.TargetUser = Cuts.CutOrNull(record.TargetUser, 256);
        row.Command = Cuts.CutOrNull(record.Command, 2000);
        row.TriedUsers = record.TriedUsers is { Count: > 0 } tried ? tried.Take(10).Select(u => Cuts.Cut(u, 256)).ToList() : null;
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
    /// Linux's 264 (docs/linux-sensor-plan.md §5.6): "root: SSH from 10.0.4.17 with key SHA256:Kq1f9hJ0…rS2A", "root: SSH from
    /// 10.0.4.17 (password)", "root: console (tty1, password)".
    /// </summary>
    public static string LinuxAdminMessage(AgentLoginEntity login, IReadOnlyDictionary<string, SshKeyName> keyNames)
    {
        var how = login.Kind == LogonKind.Console ? "console" : login.Kind.ToString().ToUpperInvariant();
        var from = login.SourceAddress is not null ? $" from {login.SourceAddress}" : "";
        var with = login.KeyFingerprint is { } fingerprint ? $" with {KeyText(fingerprint, keyNames.GetValueOrDefault(fingerprint))}" : "";
        var details = new[] { login.SourceAddress is null ? login.Workstation : null, login.KeyFingerprint is null ? login.AuthPackage : null }.OfType<string>().ToList();
        return $"{login.User}: {how}{from}{with}{(details.Count > 0 ? $" ({string.Join(", ", details)})" : "")}";
    }

    /// <summary>
    /// How a key is named in events and messages: "key SHA256:Kq1f9hJ0…rS2A" while it isn't registered; with the key manager
    /// (docs/linux-sensor-plan.md §6.4, L5) "key Philipp, laptop (philipp)". The one place that words a key.
    /// </summary>
    public static string KeyText(string fingerprint, SshKeyName? name = null) =>
        name is null ? $"key {SshKeys.Short(fingerprint)}" : $"key {name.Name}{(name.Owner is { Length: > 0 } owner ? $" ({owner})" : "")}";

    /// <summary>
    /// The registered names of these keys, by fingerprint: none until the key manager exists. L5 (docs/linux-sensor-plan.md
    /// §6.4) answers them from <c>ssh_keys</c> here, and every message that names a key follows.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, SshKeyName>> KeyNamesAsync(PnmtdDbContext db, IReadOnlyCollection<string> fingerprints, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, SshKeyName>>(new Dictionary<string, SshKeyName>(StringComparer.Ordinal));

    /// <summary>
    /// Linux's 464 (docs/linux-sensor-plan.md §5.6): the threshold of failures against existing accounts within the window from one
    /// source or for one user (sudo and su have no source), and each new <c>not in sudoers</c> and lockout. Failures for accounts
    /// that don't exist (<see cref="LinuxLogins.InvalidUser"/>) never count. Stable while a burst lasts, so it collapses by the dedupe.
    /// </summary>
    private async Task<List<string>> LinuxBurstsAsync(PnmtdDbContext db, AgentEntity agent, IReadOnlyList<AgentLoginEntity> fresh, CancellationToken ct)
    {
        var minutes = Math.Clamp(options.CurrentValue.FailedLoginWindowMinutes, 1, 60);
        var since = Clock.UtcNow.AddMinutes(-minutes);
        var failures = await db.AgentLogins.AsNoTracking()
            .Where(l => l.SensorId == agent.SensorId && l.Outcome == LogonOutcome.Failure && (l.LastAt ?? l.At) >= since
                && (l.FailureReason == null || l.FailureReason != LinuxLogins.InvalidUser))
            .Select(l => new { l.User, l.SourceAddress, l.Count, l.Kind, l.FailureReason })
            .ToListAsync(ct);
        var threshold = Math.Max(2, agent.FailedLoginThreshold);
        var messages = new List<string>();
        foreach (var source in failures.Where(f => f.SourceAddress is not null).GroupBy(f => f.SourceAddress!))
        {
            if (source.Sum(f => f.Count) >= threshold)
            {
                var users = source.Select(f => f.User).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5).ToList();
                messages.Add($"Failed logins from {source.Key}: at least {threshold} in {minutes} min (users: {string.Join(", ", users)})");
            }
        }
        foreach (var user in failures.GroupBy(f => f.User, StringComparer.Ordinal))
        {
            // Once from one source, the source's message says it already.
            var sources = user.Select(f => f.SourceAddress).Distinct().ToList();
            if (user.Sum(f => f.Count) >= threshold && (sources.Count > 1 || sources[0] is null))
            {
                var kinds = string.Join(", ", user.Select(f => f.Kind.ToString()).Distinct().Order(StringComparer.Ordinal));
                messages.Add($"Failed logins for {user.Key}: at least {threshold} in {minutes} min ({kinds})");
            }
        }
        foreach (var refused in fresh.Where(l => l.Outcome == LogonOutcome.Failure && l.FailureReason == LinuxLogins.NotInSudoers))
        {
            messages.Add($"{refused.User}: not in sudoers ({refused.Kind.ToString().ToLowerInvariant()}{(refused.Workstation is { } tty ? " on " + tty : "")})");
        }
        foreach (var locked in fresh.Where(l => l.Outcome == LogonOutcome.Failure && l.FailureReason == LinuxLogins.Lockout))
        {
            var from = locked.SourceAddress ?? locked.Workstation;
            messages.Add($"{locked.User} locked{(from is null ? "" : $" (from {from})")}");
        }
        return messages.Distinct().Take(10).ToList();
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

/// <summary>A key's registered name and owner (docs/linux-sensor-plan.md §6.4): what <see cref="AgentAccessService.KeyText"/> names it by.</summary>
public sealed record SshKeyName(string Name, string? Owner);
