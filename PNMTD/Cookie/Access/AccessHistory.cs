using Microsoft.EntityFrameworkCore;
using PNMTD.Audit;
using PNMTD.Availability;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Access;

/// <summary>
/// What code knows about a login (docs/cookie-access-plan.md §4.4, §4.5): the target's history of the account, the failures
/// before it, the source's fan-out, the key, the person and the plan. Servers' and routers' logins are in <c>agent_logins</c>,
/// PNMT's sign-ins in the audit log and the MCP log.
/// </summary>
public sealed class AccessHistory(AvailabilityPlan plan)
{
    /// <summary>At most this many of the account's logins make its history: the newest.</summary>
    public const int MaxHistory = 5000;

    /// <summary>The logins' retention: the history can't reach further.</summary>
    public const int MaxHistoryDays = 180;

    private sealed record Past(DateTime At, string? Source, string Group, bool IsAdmin, string? TargetUser, string? Device, LogonKind? Kind);

    public async Task<AccessFacts> FactsAsync(PnmtdDbContext db, AccessLogin l, AccessSettings s, bool learning, CancellationToken ct)
    {
        var since = l.At.AddDays(-s.HistoryDays);
        var past = l.Target == AccessTargetKind.Pnmt ? await PnmtPastAsync(db, l, since, ct) : await TargetPastAsync(db, l, since, ct);
        var network = AccessSignals.NetworkOf(l.Source);
        var hours = past.Select(p => AccessSignals.HourOfWeek(Clock.ToLocal(p.At))).ToHashSet();
        var (here, anywhere, locked) = await FailuresAsync(db, l, ct);
        var (fanOut, fanOutNew) = await FanOutAsync(db, l, past.Count == 0, ct);
        SshKeyEntity? key = null;
        if (l.KeyFingerprint is { Length: > 0 } fingerprint)
        {
            key = await db.SshKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Fingerprint == fingerprint, ct);
        }
        var person = await PersonAsync(db, l, s.Links, key, ct);
        var (fromPnmt, job) = l.Target == AccessTargetKind.Router ? await RouterAsync(db, l, ct) : (false, false);
        var calendar = await plan.CalendarAsync(ct);
        return new AccessFacts
        {
            Login = l,
            HistoryDays = s.HistoryDays,
            Learning = learning,
            AccountSeen = past.Count > 0,
            NetworkSeen = network is not null && past.Any(p => AccessSignals.NetworkOf(p.Source) == network),
            GroupSeen = past.Any(p => p.Group == l.Group),
            AdminSeen = past.Any(p => p.IsAdmin),
            TargetUserSeen = l.TargetUser is { } target && past.Any(p => string.Equals(p.TargetUser, target, StringComparison.OrdinalIgnoreCase)),
            DeviceSeen = l.Device is { } device && past.Any(p => p.Device == device),
            HourSeen = AccessSignals.NearHour(AccessSignals.HourOfWeek(Clock.ToLocal(l.At)), hours),
            PublicSeen = past.Any(p => AccessSignals.IsPublic(p.Source)),
            CleartextSeen = past.Any(p => p.Kind is LogonKind.Telnet or LogonKind.Ftp),
            FailuresHere = here,
            FailuresAnywhere = anywhere,
            LockedOutBefore = locked,
            KeyKnown = l.KeyFingerprint is null ? null : key is not null,
            KeyRetired = key?.State == SshKeyState.Retired,
            KeyOwnerId = key?.UserId,
            Person = person,
            FormerPerson = person is { Active: false },
            FanOut = fanOut,
            FanOutNew = fanOutNew,
            FromPnmt = fromPnmt,
            PnmtJob = job,
            WorkingHours = calendar.IsWorking(l.At),
            TrustedName = AccessSignals.TrustedName(l.Source, s.Trusted),
        };
    }

    private static async Task<List<Past>> TargetPastAsync(PnmtdDbContext db, AccessLogin l, DateTime since, CancellationToken ct)
    {
        var user = l.User.ToLowerInvariant();
        var rows = await db.AgentLogins.AsNoTracking()
            .Where(x => x.SensorId == l.SensorId && x.Outcome == LogonOutcome.Success && x.At >= since && x.At <= l.At && x.Id != l.AgentLoginId
                && x.User.ToLower() == user)
            .OrderByDescending(x => x.At).Take(MaxHistory)
            .Select(x => new { x.At, x.Domain, x.SourceAddress, x.Kind, x.IsAdmin, x.TargetUser }).ToListAsync(ct);
        return [.. rows.Where(x => string.Equals(x.Domain ?? "", l.Domain ?? "", StringComparison.OrdinalIgnoreCase))
            .Select(x => new Past(x.At, x.SourceAddress, AccessSignals.GroupOf(l.Target, x.Kind, ""), x.IsAdmin, x.TargetUser, null, x.Kind))];
    }

    private static async Task<List<Past>> PnmtPastAsync(PnmtdDbContext db, AccessLogin l, DateTime since, CancellationToken ct)
    {
        var user = l.User.ToLowerInvariant();
        var web = await db.AuditLog.AsNoTracking()
            .Where(a => (a.EventType == AuditEvents.LoginSucceeded || a.EventType == AuditEvents.McpTokenOn) && a.CreatedAt >= since && a.CreatedAt <= l.At
                && a.Id != l.AuditLogId && a.Username.ToLower() == user)
            .OrderByDescending(a => a.CreatedAt).Take(MaxHistory)
            .Select(a => new { a.CreatedAt, a.EventType, a.IpAddress, a.UserAgent }).ToListAsync(ct);
        var mcp = await db.McpLog.AsNoTracking()
            .Where(m => m.At >= since && m.At < l.At && m.Username.ToLower() == user)
            .OrderByDescending(m => m.At).Take(MaxHistory)
            .Select(m => new { m.At, m.Ip, m.UserAgent }).ToListAsync(ct);
        return
        [
            .. web.Select(a => new Past(a.CreatedAt, a.IpAddress, a.EventType == AuditEvents.McpTokenOn ? AccessSignals.Mcp : AccessSignals.Web, false, null,
                a.EventType == AuditEvents.LoginSucceeded ? UserAgentFamily.Of(a.UserAgent) : null, null)),
            .. mcp.Select(m => new Past(m.At, m.Ip, AccessSignals.Mcp, false, null, null, null)),
        ];
    }

    /// <summary>Failures from the same source within the hour before: on this target, and anywhere; for PNMT also the account's.</summary>
    private static async Task<(int Here, int Anywhere, bool Locked)> FailuresAsync(PnmtdDbContext db, AccessLogin l, CancellationToken ct)
    {
        var from = l.At.AddHours(-1);
        var here = 0;
        var anywhere = 0;
        var locked = false;
        if (l.Source is { } source)
        {
            var failures = await db.AgentLogins.AsNoTracking()
                .Where(x => x.Outcome == LogonOutcome.Failure && x.SourceAddress == source && (x.LastAt ?? x.At) >= from && x.At <= l.At)
                .Select(x => new { x.SensorId, x.Count }).ToListAsync(ct);
            var signIns = await db.AuditLog.AsNoTracking()
                .CountAsync(a => a.EventType == AuditEvents.LoginFailed && a.IpAddress == source && a.CreatedAt >= from && a.CreatedAt <= l.At, ct);
            anywhere = failures.Sum(f => f.Count) + signIns;
            here = l.Target == AccessTargetKind.Pnmt ? signIns : failures.Where(f => f.SensorId == l.SensorId).Sum(f => f.Count);
        }
        if (l.Target == AccessTargetKind.Pnmt)
        {
            var user = l.User.ToLowerInvariant();
            var account = await db.AuditLog.AsNoTracking()
                .Where(a => a.CreatedAt >= from && a.CreatedAt <= l.At && a.Username.ToLower() == user
                    && (a.EventType == AuditEvents.LoginFailed || a.EventType == AuditEvents.LockedOut || a.EventType == AuditEvents.TotpFailed))
                .Select(a => a.EventType).ToListAsync(ct);
            locked = account.Any(e => e != AuditEvents.LoginFailed);
            here = Math.Max(here, account.Count(e => e == AuditEvents.LoginFailed));
        }
        return (here, anywhere, locked);
    }

    /// <summary>The targets the source logged in to within the 15 minutes before, and whether an account was new on one of them.</summary>
    private static async Task<(int Targets, bool New)> FanOutAsync(PnmtdDbContext db, AccessLogin l, bool newHere, CancellationToken ct)
    {
        if (l.Source is not { } source || l.Target == AccessTargetKind.Pnmt)
        {
            return (0, false);
        }
        var from = l.At.AddMinutes(-15);
        var targets = await db.AgentLogins.AsNoTracking()
            .Where(x => x.SourceAddress == source && x.Outcome == LogonOutcome.Success && x.At >= from && x.At <= l.At.AddMinutes(1))
            .Select(x => x.SensorId).Distinct().CountAsync(ct);
        if (targets < 3)
        {
            return (targets, false);
        }
        var others = newHere || await db.AccessReviews.AsNoTracking()
            .AnyAsync(r => r.Source == source && r.At >= from && r.Signals.Contains("\"new-account\""), ct);
        return (targets, others);
    }

    /// <summary>Whose account it is (§5.3), by the ways switched on; for PNMT, the user who signed in.</summary>
    public async Task<AccessPerson?> PersonAsync(PnmtdDbContext db, AccessLogin l, AccessAccountLinks links, SshKeyEntity? key, CancellationToken ct)
    {
        var id = await LinkAsync(db, l, links, key, ct);
        if (id is null)
        {
            return null;
        }
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return null;
        }
        var today = DateOnly.FromDateTime(Clock.ToLocal(l.At));
        var snapshot = await plan.ReadAsync(today, today, ct, [user.Id]);
        var works = snapshot.Works(user.Id, today);
        string? away = null;
        if (!works)
        {
            away = snapshot.Has(user.Id, today, AvailabilityFlag.Vacation) ? "on vacation"
                : snapshot.Has(user.Id, today, AvailabilityFlag.Holiday) ? "on holiday"
                : snapshot.Has(user.Id, today, AvailabilityFlag.Compensation) ? "compensation"
                : "a day off";
        }
        return new AccessPerson(user.Id, user.DisplayName, user.IsActive, works, away);
    }

    public static async Task<Guid?> LinkAsync(PnmtdDbContext db, AccessLogin l, AccessAccountLinks links, SshKeyEntity? key, CancellationToken ct)
    {
        if (l.Target == AccessTargetKind.Pnmt)
        {
            var name = l.User.ToLowerInvariant();
            return await db.Users.AsNoTracking().Where(u => u.Username.ToLower() == name).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        }
        if (links.Key && key?.UserId is { } owner)
        {
            return owner;
        }
        if (AccessSignals.Shared(l.Account))
        {
            return null;
        }
        if (links.ServerAccounts)
        {
            var listed = await db.Users.AsNoTracking().Where(u => u.ServerAccounts.Count > 0).Select(u => new { u.Id, u.ServerAccounts }).ToListAsync(ct);
            // The full account first (CONTOSO\jdoe), then without the domain.
            if (listed.FirstOrDefault(u => u.ServerAccounts.Any(a => string.Equals(a.Trim(), l.Account, StringComparison.OrdinalIgnoreCase))) is { } exact)
            {
                return exact.Id;
            }
            if (listed.FirstOrDefault(u => u.ServerAccounts.Any(a => AccessSignals.SameAccount(a, l.Account))) is { } bare)
            {
                return bare.Id;
            }
        }
        if (links.Username)
        {
            var bare = AccessSignals.Bare(l.Account).ToLowerInvariant();
            return await db.Users.AsNoTracking().Where(u => u.Username.ToLower() == bare).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        }
        return null;
    }

    /// <summary>A router's login: from PNMTD's own address, and whether one of PNMT's jobs ran as the account then.</summary>
    private static async Task<(bool FromPnmt, bool Job)> RouterAsync(PnmtdDbContext db, AccessLogin l, CancellationToken ct)
    {
        var router = await db.MikrotikRouters.AsNoTracking().Where(r => r.SensorId == l.SensorId).Select(r => new { r.PnmtAddress }).SingleOrDefaultAsync(ct);
        var fromPnmt = router?.PnmtAddress is { Length: > 0 } address && string.Equals(address, l.Source, StringComparison.OrdinalIgnoreCase);
        if (!fromPnmt)
        {
            return (false, false);
        }
        var user = l.User.ToLowerInvariant();
        var start = l.At.AddMinutes(2);
        var end = l.At.AddMinutes(-2);
        var job = await db.MikrotikJobs.AsNoTracking().AnyAsync(j => j.SensorId == l.SensorId && j.AdminUsername != null && j.AdminUsername.ToLower() == user
            && j.StartedAt != null && j.StartedAt <= start && (j.FinishedAt == null || j.FinishedAt >= end), ct);
        return (true, job);
    }

    /// <summary>Whether a target is in its learning period (§4.5): its history is younger than the learning days.</summary>
    public static async Task<bool> LearningAsync(PnmtdDbContext db, AccessLogin l, AccessSettings s, CancellationToken ct)
    {
        if (s.LearningDays == 0)
        {
            return false;
        }
        var limit = l.At.AddDays(-s.LearningDays);
        switch (l.Target)
        {
            case AccessTargetKind.Pnmt:
                return !await db.AuditLog.AsNoTracking().AnyAsync(a => a.EventType == AuditEvents.LoginSucceeded && a.CreatedAt <= limit, ct);
            case AccessTargetKind.Router:
                var first = await db.MikrotikRouters.AsNoTracking().Where(r => r.SensorId == l.SensorId).Select(r => r.LoginsFirstReadAt).SingleOrDefaultAsync(ct);
                return first is null || first > limit;
            default:
                var joined = await db.Agents.AsNoTracking().Where(a => a.SensorId == l.SensorId).Select(a => (DateTime?)a.JoinedAt).SingleOrDefaultAsync(ct);
                var older = await db.AgentLogins.AsNoTracking().AnyAsync(x => x.SensorId == l.SensorId && x.At <= limit, ct);
                return !older && (joined is null || joined > limit);
        }
    }
}

/// <summary>A user agent's browser and system family (docs/cookie-access-plan.md §4.3): a new browser version isn't a new device.</summary>
public static class UserAgentFamily
{
    public static string? Of(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }
        var ua = userAgent;
        bool Has(string s) => ua.Contains(s, StringComparison.OrdinalIgnoreCase);
        var browser = Has("Edg/") ? "Edge" : Has("OPR/") || Has("Opera") ? "Opera" : Has("Firefox/") ? "Firefox" : Has("Chrome/") || Has("CriOS/") ? "Chrome"
            : Has("Safari/") ? "Safari" : null;
        var system = Has("iPhone") || Has("iPad") ? "iOS" : Has("Android") ? "Android" : Has("Windows") ? "Windows" : Has("CrOS") ? "ChromeOS"
            : Has("Macintosh") || Has("Mac OS X") ? "macOS" : Has("Linux") ? "Linux" : null;
        if (browser is null)
        {
            return Has("mcp") || Has("claude") || Has("node") || Has("python") || Has("curl") ? "an MCP or HTTP client" : "an unknown client";
        }
        return system is null ? browser : $"{browser} on {system}";
    }
}
