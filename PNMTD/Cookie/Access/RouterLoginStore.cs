using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Access;

/// <summary>A router's user as stored: its group, and whether the group makes it an admin (docs/cookie-access-plan.md §4.2).</summary>
public sealed record RouterUserItem(string Name, string? Group, bool Admin, bool Disabled);

/// <summary>
/// A router's logins (docs/cookie-access-plan.md §4.2), from each poll's read of its log: stored in <c>agent_logins</c> with the
/// channel <c>routeros</c>, a line read twice once, failures aggregated per account, source and way per 10 minutes, a logout as
/// its login's end. Also the router's users and who is logged in now, PNMTD's address as the router sees it (the poll's own
/// session), and whether lines may be missing. Every router's, watched or not; <see cref="AccessWatch"/> then looks at the new ones.
/// </summary>
public sealed class RouterLoginStore(IDbContextFactory<PnmtdDbContext> dbs, AccessWatch watch, ILogger<RouterLoginStore> logger)
{
    private static readonly JsonSerializerOptions Json = CookieSettings.Json;

    /// <summary>After a successful poll; <paramref name="previousSuccessAt"/> is the poll before it, for the gap.</summary>
    public async Task StoreAsync(Guid sensorId, RouterReading reading, DateTime? previousSuccessAt, CancellationToken ct)
    {
        if (!reading.LogRead)
        {
            return;
        }
        List<Guid> fresh;
        try
        {
            fresh = await StoreLockedAsync(sensorId, reading, previousSuccessAt, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The poll is recorded already: the logins wait for the next one.
            logger.LogWarning(e, "The logins of router {Sensor} couldn't be stored", sensorId);
            return;
        }
        await watch.RouterLoginsAsync(sensorId, fresh, ct);
    }

    private async Task<List<Guid>> StoreLockedAsync(Guid sensorId, RouterReading reading, DateTime? previousSuccessAt, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var router = await db.MikrotikRouters.SingleOrDefaultAsync(r => r.SensorId == sensorId, ct);
        if (router is null)
        {
            return [];
        }
        var utcNow = Clock.UtcNow;
        var routerNow = RouterLogins.Clock(reading.Clock) ?? Clock.ToLocal(utcNow);
        var offset = RouterLogins.Offset(routerNow, utcNow);
        var groups = reading.Groups.GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Policies, StringComparer.OrdinalIgnoreCase);
        var users = reading.Users.Select(u => new RouterUserItem(u.Name, u.Group, u.Group is { } g && groups.TryGetValue(g, out var p) && RouterLogins.IsAdminGroup(p), u.Disabled))
            .ToList();
        var admins = users.Where(u => u.Admin).Select(u => u.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        router.RouterUsers = JsonSerializer.Serialize(users, Json);
        router.ActiveUsers = JsonSerializer.Serialize(reading.Active, Json);
        if (reading.Active.FirstOrDefault(a => string.Equals(a.Name, RouterPoll.UserName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Via, "ssh", StringComparison.OrdinalIgnoreCase) && a.Address is { Length: > 0 }) is { } own)
        {
            router.PnmtAddress = CaseBook.Cut(own.Address, 100);
        }
        router.LoginsFirstReadAt ??= utcNow;

        var lines = new List<(DateTime At, RouterLogLine Line, RouterLoginLine Login)>();
        var unmatched = 0;
        foreach (var line in reading.LogLines)
        {
            var parsed = RouterLogins.Parse(line.Message);
            var at = RouterLogins.Time(line.Time, routerNow);
            if (parsed is null || parsed.Kind is null || at is null)
            {
                if (RouterLogins.LooksLikeLogin(line.Message))
                {
                    unmatched++;
                }
                continue;
            }
            lines.Add((DateTime.SpecifyKind(at.Value - offset, DateTimeKind.Utc), line, parsed));
        }
        router.LogUnmatched = unmatched;
        if (lines.Count > 0)
        {
            var oldest = lines.Min(l => l.At);
            router.LogOldestAt = oldest;
            // The poll's own login is in every read: when the oldest line is newer than the poll before, lines are gone.
            router.LogGap = previousSuccessAt is { } before && oldest > before.AddMinutes(1);
        }

        var ids = lines.Select(l => l.Login.Event == RouterLoginEvent.Failure
            ? RouterLogins.FailureId(l.At, l.Login.User, l.Login.Source, l.Login.Via)
            : RouterLogins.RecordId(l.At, l.Line.Message)).Distinct().ToList();
        var existing = await db.AgentLogins.Where(x => x.SensorId == sensorId && x.Channel == RouterLogins.Channel && ids.Contains(x.RecordId))
            .ToDictionaryAsync(x => x.RecordId, ct);
        var fresh = new List<AgentLoginEntity>();
        // The poll's own login, every few minutes, isn't stored: it would be most of the table, and it tells nothing (§4.2).
        bool OwnPoll(RouterLoginLine l) => string.Equals(l.User, RouterPoll.UserName, StringComparison.OrdinalIgnoreCase)
            && router.PnmtAddress is { Length: > 0 } address && string.Equals(address, l.Source, StringComparison.OrdinalIgnoreCase);
        foreach (var (at, line, login) in lines.Where(l => l.Login.Event == RouterLoginEvent.LoggedIn && !OwnPoll(l.Login)).OrderBy(l => l.At))
        {
            var id = RouterLogins.RecordId(at, line.Message);
            if (existing.ContainsKey(id))
            {
                continue;
            }
            var row = new AgentLoginEntity
            {
                Id = Guid.NewGuid(), SensorId = sensorId, Channel = RouterLogins.Channel, RecordId = id, At = at, Count = 1, Outcome = LogonOutcome.Success,
                User = CaseBook.Cut(login.User, 256)!, IsAdmin = admins.Contains(login.User), Kind = login.Kind!.Value, SourceAddress = CaseBook.Cut(login.Source, 100),
                AuthPackage = CaseBook.Cut(login.Via, 50),
            };
            db.AgentLogins.Add(row);
            existing[id] = row;
            fresh.Add(row);
        }
        // Failures, per account, source and way per 10 minutes: the count is what the log holds of them, never less than before.
        foreach (var bucket in lines.Where(l => l.Login.Event == RouterLoginEvent.Failure)
                     .GroupBy(l => RouterLogins.FailureId(l.At, l.Login.User, l.Login.Source, l.Login.Via)))
        {
            var first = bucket.OrderBy(b => b.At).First();
            var last = bucket.Max(b => b.At);
            var count = bucket.Select(b => (b.At, b.Line.Message)).Distinct().Count();
            if (existing.TryGetValue(bucket.Key, out var row))
            {
                row.Count = Math.Max(row.Count, count);
                row.LastAt = row.LastAt is { } was && was > last ? was : last;
                continue;
            }
            row = new AgentLoginEntity
            {
                Id = Guid.NewGuid(), SensorId = sensorId, Channel = RouterLogins.Channel, RecordId = bucket.Key, At = first.At, LastAt = count > 1 ? last : null,
                Count = count, Outcome = LogonOutcome.Failure, User = CaseBook.Cut(first.Login.User, 256)!, IsAdmin = admins.Contains(first.Login.User),
                Kind = first.Login.Kind!.Value, SourceAddress = CaseBook.Cut(first.Login.Source, 100), AuthPackage = CaseBook.Cut(first.Login.Via, 50),
            };
            db.AgentLogins.Add(row);
            existing[bucket.Key] = row;
        }
        await db.SaveChangesAsync(ct);
        // A logout ends the newest open login of the same account, source and way before it.
        foreach (var (at, _, login) in lines.Where(l => l.Login.Event == RouterLoginEvent.LoggedOut).OrderBy(l => l.At))
        {
            var kind = login.Kind!.Value;
            var user = login.User;
            var source = login.Source;
            var open = await db.AgentLogins.Where(x => x.SensorId == sensorId && x.Channel == RouterLogins.Channel && x.Outcome == LogonOutcome.Success
                    && x.User == user && x.SourceAddress == source && x.Kind == kind && x.At <= at && x.LoggedOffAt == null)
                .OrderByDescending(x => x.At).FirstOrDefaultAsync(ct);
            if (open is not null)
            {
                open.LoggedOffAt = at;
            }
        }
        await db.SaveChangesAsync(ct);
        return [.. fresh.Select(f => f.Id)];
    }
}
