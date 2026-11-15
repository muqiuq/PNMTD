using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Access;

/// <summary>
/// The access watch (docs/cookie-access-plan.md §4, §5.1): every new login of a watched target is compared with its history in
/// code; one with a signal and no known access that covers it becomes a review, in the access case of its target and account,
/// and wakes Cookie. Servers' logins come from <see cref="Agents.AgentAccessService"/>, routers' from the poll
/// (<see cref="RouterLoginStore"/>), PNMT's sign-ins from the audit log and the MCP log (<see cref="SignInsAsync"/>). It runs
/// whenever the watch is on and <c>Cookie__Enabled</c> is set, whether Cookie is started or not: code's notice needs it (§6.5).
/// </summary>
public sealed class AccessWatch(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, AccessHistory history, CookieSignals signals,
    IOptionsMonitor<CookieOptions> options, ILogger<AccessWatch> logger)
{
    public const string PnmtName = "PNMT";

    /// <summary>A login older than this when it comes is late (§4.5): an agent's backlog.</summary>
    public static readonly TimeSpan LateAfter = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Json = CookieSettings.Json;

    private bool Enabled => options.CurrentValue.Enabled;

    /// <summary>A server's fresh logins, after <see cref="Agents.AgentAccessService"/> stored them.</summary>
    public async Task ServerLoginsAsync(Guid sensorId, IReadOnlyCollection<Guid> fresh, CancellationToken ct)
    {
        if (!Enabled || fresh.Count == 0)
        {
            return;
        }
        try
        {
            var s = await settings.GetAsync<AccessSettings>(ct);
            if (!s.Watches(AccessTargetKind.Server))
            {
                return;
            }
            await using var db = await dbs.CreateDbContextAsync(ct);
            var sensor = await db.Sensors.AsNoTracking().Include(x => x.Parent).SingleOrDefaultAsync(x => x.Id == sensorId, ct);
            if (sensor is null || !ServerWatched(s, sensor.Parent.Id, sensor.Parent.LocationId))
            {
                return;
            }
            var rows = await db.AgentLogins.AsNoTracking().Where(l => fresh.Contains(l.Id) && l.Outcome == LogonOutcome.Success).OrderBy(l => l.At).ToListAsync(ct);
            foreach (var row in rows)
            {
                var login = new AccessLogin(AccessTargetKind.Server, sensorId, sensor.Parent.Name, sensor.Parent.Id, sensor.Parent.LocationId, row.Id, null, row.At,
                    row.User, row.Domain, row.IsAdmin, row.Kind, AccessSignals.WayOf(row.Kind), row.SourceAddress, row.Workstation, row.Workstation,
                    row.KeyFingerprint, row.TargetUser, row.AuthPackage);
                if (s.Kinds.Watched(AccessTargetKind.Server, row.Kind, row.IsAdmin, login.Way))
                {
                    await EvaluateAsync(db, login, s, ct);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The agent's batch is stored already: a failing watch must not lose it.
            logger.LogWarning(e, "The access watch couldn't look at the logins of {Sensor}", sensorId);
        }
    }

    public static bool ServerWatched(AccessSettings s, Guid hostId, Guid? locationId) =>
        !s.ExcludedHosts.Contains(hostId)
        && (s.ServerHosts.Count == 0 && s.ServerLocations.Count == 0 || s.ServerHosts.Contains(hostId) || locationId is { } l && s.ServerLocations.Contains(l));

    /// <summary>A router's fresh logins, after <see cref="RouterLoginStore"/> stored them; never PNMT's own poll and jobs (§4.2).</summary>
    public async Task RouterLoginsAsync(Guid sensorId, IReadOnlyCollection<Guid> fresh, CancellationToken ct)
    {
        if (!Enabled || fresh.Count == 0)
        {
            return;
        }
        try
        {
            var s = await settings.GetAsync<AccessSettings>(ct);
            if (!s.Watches(AccessTargetKind.Router))
            {
                return;
            }
            await using var db = await dbs.CreateDbContextAsync(ct);
            var router = await db.MikrotikRouters.AsNoTracking().Include(r => r.Sensor).ThenInclude(x => x.Parent).SingleOrDefaultAsync(r => r.SensorId == sensorId, ct);
            if (router is null || !router.WatchLogins)
            {
                return;
            }
            var host = router.Sensor.Parent;
            var rows = await db.AgentLogins.AsNoTracking().Where(l => fresh.Contains(l.Id) && l.Outcome == LogonOutcome.Success).OrderBy(l => l.At).ToListAsync(ct);
            foreach (var row in rows)
            {
                var login = new AccessLogin(AccessTargetKind.Router, sensorId, host.Name, host.Id, host.LocationId, row.Id, null, row.At, row.User, null, row.IsAdmin,
                    row.Kind, AccessSignals.WayOf(row.Kind), row.SourceAddress);
                if (!s.Kinds.Watched(AccessTargetKind.Router, row.Kind, row.IsAdmin, login.Way) || await OwnAsync(db, router, login, ct))
                {
                    continue;
                }
                await EvaluateAsync(db, login, s, ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "The access watch couldn't look at the logins of router {Sensor}", sensorId);
        }
    }

    /// <summary>PNMT's own logins (§4.2): the poll's <c>pnmt</c> from PNMTD's address, and PNMT's jobs as an admin while they run.</summary>
    private static async Task<bool> OwnAsync(PnmtdDbContext db, MikrotikRouterEntity router, AccessLogin l, CancellationToken ct)
    {
        if (router.PnmtAddress is not { Length: > 0 } address || !string.Equals(address, l.Source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.Equals(l.User, RouterPoll.UserName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var user = l.User.ToLowerInvariant();
        var start = l.At.AddMinutes(2);
        var end = l.At.AddMinutes(-2);
        return await db.MikrotikJobs.AsNoTracking().AnyAsync(j => j.SensorId == l.SensorId && j.AdminUsername != null && j.AdminUsername.ToLower() == user
            && j.StartedAt != null && j.StartedAt <= start && (j.FinishedAt == null || j.FinishedAt >= end), ct);
    }

    /// <summary>
    /// PNMT's sign-ins since the last look (§4.3): the audit log's successful sign-ins and tokens switched on, and each MCP token's
    /// first call from a network that day. Returns how many were looked at.
    /// </summary>
    public async Task<int> SignInsAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return 0;
        }
        var s = await settings.GetAsync<AccessSettings>(ct);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var runtime = await CaseBook.RuntimeAsync(db, ct);
        var now = Clock.UtcNow;
        var cursor = runtime.AccessAuditAt ?? now.AddMinutes(-5);
        var mcpCursor = runtime.AccessMcpLogId ?? await db.McpLog.MaxAsync(m => (long?)m.Id, ct) ?? 0;
        if (!s.Watches(AccessTargetKind.Pnmt))
        {
            // Off, the cursors follow: switching it on looks forward, not at what passed meanwhile.
            runtime.AccessAuditAt = now;
            runtime.AccessMcpLogId = await db.McpLog.MaxAsync(m => (long?)m.Id, ct) ?? 0;
            await db.SaveChangesAsync(ct);
            return 0;
        }
        var entries = await db.AuditLog.AsNoTracking()
            .Where(a => a.CreatedAt > cursor && (a.EventType == AuditEvents.LoginSucceeded || a.EventType == AuditEvents.McpTokenOn))
            .OrderBy(a => a.CreatedAt).Take(500).ToListAsync(ct);
        var calls = await db.McpLog.AsNoTracking().Where(m => m.Id > mcpCursor && m.UserId != null).OrderBy(m => m.Id).Take(2000).ToListAsync(ct);
        var names = entries.Select(e => e.Username.ToLowerInvariant()).Concat(calls.Select(c => c.Username.ToLowerInvariant())).Distinct().ToList();
        var admins = await db.Users.AsNoTracking().Where(u => names.Contains(u.Username.ToLower())).ToDictionaryAsync(u => u.Username.ToLowerInvariant(), u => u.IsAdmin, ct);
        var looked = 0;
        foreach (var a in entries)
        {
            if (string.IsNullOrWhiteSpace(a.Username))
            {
                continue;
            }
            var mcp = a.EventType == AuditEvents.McpTokenOn;
            var login = new AccessLogin(AccessTargetKind.Pnmt, null, PnmtName, null, null, null, a.Id, a.CreatedAt, a.Username, null,
                admins.GetValueOrDefault(a.Username.ToLowerInvariant()), null, mcp ? AccessSignals.Mcp : AccessSignals.Web, a.IpAddress,
                UserAgentFamily.Of(a.UserAgent));
            if (s.Kinds.Watched(AccessTargetKind.Pnmt, null, login.IsAdmin, login.Way))
            {
                await EvaluateAsync(db, login, s, ct);
                looked++;
            }
            runtime.AccessAuditAt = a.CreatedAt;
        }
        runtime.AccessAuditAt ??= cursor;
        // A token's first call from a network that day; the rest are history.
        foreach (var call in calls)
        {
            runtime.AccessMcpLogId = call.Id;
            if (call.Ip is not { Length: > 0 } ip || !s.Kinds.PnmtMcp)
            {
                continue;
            }
            var network = AccessSignals.NetworkOf(ip);
            var dayStart = Clock.ToUtc(Clock.ToLocal(call.At).Date);
            var earlier = await db.McpLog.AsNoTracking().Where(m => m.TokenId == call.TokenId && m.Id < call.Id && m.At >= dayStart && m.Ip != null)
                .Select(m => m.Ip!).Distinct().ToListAsync(ct);
            if (earlier.Any(e => AccessSignals.NetworkOf(e) == network))
            {
                continue;
            }
            var login = new AccessLogin(AccessTargetKind.Pnmt, null, PnmtName, null, null, null, null, call.At, call.Username, null,
                admins.GetValueOrDefault(call.Username.ToLowerInvariant()), null, AccessSignals.Mcp, ip, call.Client ?? UserAgentFamily.Of(call.UserAgent));
            await EvaluateAsync(db, login, s, ct, mcpLogId: call.Id);
            looked++;
        }
        await db.SaveChangesAsync(ct);
        return looked;
    }

    /// <summary>
    /// One login (§4.4, §4.5): covered by known access, nothing; else its signals; none, nothing; an excluded account with only
    /// medium ones, nothing. Otherwise a review in its case, and Cookie wakes. Returns the review, or null.
    /// </summary>
    public async Task<AccessReviewEntity?> EvaluateAsync(PnmtdDbContext db, AccessLogin l, AccessSettings s, CancellationToken ct, long? mcpLogId = null)
    {
        var now = Clock.UtcNow;
        if (l.AgentLoginId is { } loginId && await db.AccessReviews.AnyAsync(r => r.AgentLoginId == loginId, ct)
            || l.AuditLogId is { } auditId && await db.AccessReviews.AnyAsync(r => r.AuditLogId == auditId, ct)
            || mcpLogId is { } callId && await db.AccessReviews.AnyAsync(r => r.McpLogId == callId, ct))
        {
            return null;
        }
        var known = await db.AccessKnown.AsNoTracking().Where(k => k.EndedAt == null).ToListAsync(ct);
        if (known.Any(k => AccessSignals.Covers(k, l, now)))
        {
            return null;
        }
        var learning = await AccessHistory.LearningAsync(db, l, s, ct);
        var facts = await history.FactsAsync(db, l, s, learning, ct);
        var found = AccessSignals.Compute(facts).ToList();
        var strong = found.Any(x => x.Strength == SignalStrength.Strong);
        if (AccessSignals.Excluded(s.Excluded, l))
        {
            found.RemoveAll(x => x.Strength != SignalStrength.Strong);
        }
        if (found.Count == 0)
        {
            return null;
        }
        var review = new AccessReviewEntity
        {
            Id = Guid.NewGuid(), CreatedAt = now, Target = l.Target, SensorId = l.SensorId, TargetName = CaseBook.Cut(l.TargetName, 300)!,
            AgentLoginId = l.AgentLoginId, AuditLogId = l.AuditLogId, McpLogId = mcpLogId, At = l.At, Account = CaseBook.Cut(l.Account, 520)!, IsAdmin = l.IsAdmin,
            Way = CaseBook.Cut(l.Way, 32)!, Group = l.Group, Source = CaseBook.Cut(l.Source, 100), Device = CaseBook.Cut(l.Device, 256),
            KeyFingerprint = CaseBook.Cut(l.KeyFingerprint, 100), TargetUser = CaseBook.Cut(l.TargetUser, 256),
            Signals = JsonSerializer.Serialize(found, Json), Strong = strong, Late = now - l.At > LateAfter, PersonId = facts.Person?.UserId,
            Flagged = Flagged(l),
        };
        var c = await CaseAsync(db, l, review, now, ct);
        review.CaseId = c.Id;
        db.AccessReviews.Add(review);
        CaseBook.Entry(db, c.Id, now, "login", $"{Describe(review)} · {string.Join("; ", found.Select(x => x.Text))}");
        await db.SaveChangesAsync(ct);
        signals.Raise($"access: {l.Account} on {l.TargetName}");
        return review;
    }

    /// <summary>The access case of the login's target and account (§5.1): joined while open, else opened. A strong signal lets a stopped one go on.</summary>
    private static async Task<CookieCaseEntity> CaseAsync(PnmtdDbContext db, AccessLogin l, AccessReviewEntity review, DateTime now, CancellationToken ct)
    {
        var key = KeyOf(l.SensorId, l.Account);
        var c = await db.CookieCases.SingleOrDefaultAsync(x => x.Open && x.Key == key, ct);
        if (c is null)
        {
            c = new CookieCaseEntity
            {
                Id = Guid.NewGuid(), Kind = CookieCaseKind.Access, Key = key, Open = true,
                Title = CaseBook.Cut($"Login: {l.Account} on {l.TargetName}", 300)!, HostId = l.HostId, LocationId = l.LocationId, SensorId = l.SensorId,
                Importance = review.Strong ? Lib.Models.Enum.Importance.High : Lib.Models.Enum.Importance.Normal, OpenedAt = now, ChangedAt = now, ProgressAt = now,
                Areas = l.Target == AccessTargetKind.Pnmt ? [CookieAccess.AuditLogArea] : ["Monitoring", CookieAccess.LoginsArea],
            };
            db.CookieCases.Add(c);
            CaseBook.Entry(db, c.Id, now, "opened", $"{l.Account} logged in to {l.TargetName} in a way that is new for them.");
            await db.SaveChangesAsync(ct);
            return c;
        }
        c.ChangedAt = now;
        if (review.Strong)
        {
            c.Importance = Lib.Models.Enum.Importance.High;
            if (c.StoppedAt is not null)
            {
                c.StoppedAt = null;
                CaseBook.Entry(db, c.Id, now, "go-on", "A login with a strong signal: the case goes on by itself.");
            }
        }
        return c;
    }

    public static string KeyOf(Guid? sensorId, string account)
    {
        var who = account.Trim().ToLowerInvariant();
        if (who.Length > 48)
        {
            who = who[..36] + "~" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(who)))[..11];
        }
        return $"access:{sensorId?.ToString() ?? "pnmt"}:{who}";
    }

    /// <summary>The outside text of a login that the local scanner reads as instructions to an AI (§9).</summary>
    private static bool Flagged(AccessLogin l) =>
        new[] { l.User, l.Domain, l.Workstation, l.Device, l.TargetUser }.Any(t => t is { Length: > 0 } && Ai.InjectionScanner.Check(t!).Score >= Ai.InjectionScanner.BlockScore);

    /// <summary>"CONTOSO\jdoe by RDP from 203.0.113.7 (public), Sun 03:12".</summary>
    public static string Describe(AccessReviewEntity r)
    {
        var local = Clock.ToLocal(r.At);
        var source = r.Source is null ? "" : $" from {r.Source}{(AccessSignals.IsPublic(r.Source) ? " (public)" : "")}";
        var device = r.Target == AccessTargetKind.Pnmt && r.Device is { } d ? $" with {d}" : "";
        var to = r.Target == AccessTargetKind.Pnmt ? " to PNMT" : $" to {r.TargetName}";
        var way = r.Way switch
        {
            "web" => "signed in",
            "mcp" => "used MCP",
            _ => $"by {Way(r.Way)}",
        };
        return $"{r.Account} {way}{to}{source}{device}, {local:ddd HH:mm}{(r.Late ? " (reported late)" : "")}";
    }

    public static string Way(string way) => way switch
    {
        "rdp" => "RDP",
        "ssh" => "SSH",
        "winrm" => "WinRM",
        "winbox" => "WinBox",
        "webfig" => "WebFig",
        "api" => "the API",
        "telnet" => "Telnet",
        "ftp" => "FTP",
        "sftp" => "SFTP",
        "network" => "a network logon",
        "console" => "the console",
        "sudo" => "sudo",
        "su" => "su",
        _ => way,
    };

    public static IReadOnlyList<AccessSignal> SignalsOf(AccessReviewEntity r) => JsonSerializer.Deserialize<List<AccessSignal>>(r.Signals, Json) ?? [];
}
