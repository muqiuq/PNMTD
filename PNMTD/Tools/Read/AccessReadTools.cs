using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Cookie.Access;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// The access watch's read tools (docs/cookie-access-plan.md §5.2): a server's or router's logins, PNMT's sign-ins, one account's
/// profile on one target, and one account or source across all targets. Logins carry names and addresses: <i>Monitoring: Details</i>,
/// and <i>Audit log: View</i> for PNMT's. Account names, workstations, devices and target users are untrusted text, masked.
/// </summary>
public static class AccessReadTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("logins_list", "A server's or router's logins",
            "A server's or a MikroTik router's logins, newest first: when, the account, admin or not, how, from where, the device or key, sudo's target, " +
            "the outcome and count, the logoff, and the access watch's verdict. At most 14 days. Paged.",
            Schema([Id("sensorId", "The WINDOWS, LINUX or MIKROTIK sensor"), Int("days", "The last this many days, 1 to 14; 7 when left out"),
                Text("account", "Only this account (with or without its domain)"), Text("source", "Only this source address"),
                Choice("group", "Only this kind of login", AccessSignals.Groups), Choice("outcome", "Only successes or failures", ["success", "failure"]), .. PageArgs]),
            Area.Monitoring, Level.Details, ToolKind.Read, LoginsAsync) { ForCookie = true },
        new("pnmt_signins", "PNMT's sign-ins",
            "PNMT's own sign-ins, failures, lockouts, failed 2FA codes and MCP tokens switched on, newest first: when, the user, from where, the device. " +
            "At most 14 days. Paged.",
            Schema([Text("user", "Only this PNMT user"), Text("source", "Only this source address"), Int("days", "The last this many days, 1 to 14; 7 when left out"),
                .. PageArgs]),
            Area.AuditLog, Level.View, ToolKind.Read, SignInsAsync) { ForCookie = true, CookieOnly = true },
        new("access_profile", "One account on one target",
            "One account's logins on one server or router (or PNMT, without sensorId) over the access watch's history: first and last seen, counts by kind, " +
            "its source networks with counts and last use, the hours of the week it logs in, its keys or devices, whom it became with sudo or su.",
            Schema(Id("sensorId", "The server or router; PNMT when left out", required: false), Text("account", "The account", true)),
            Area.Monitoring, Level.Details, ToolKind.Read, ProfileAsync) { ForCookie = true },
        new("account_logins", "One account or source everywhere",
            "One account, or one source address, across all servers and routers in the last hours: where it logged in, how, the outcome; and PNMT's " +
            "sign-ins from that source or by that user when you may read them.",
            Schema(Text("account", "The account"), Text("source", "The source address"), Int("hours", "The last this many hours, 1 to 24; 24 when left out")),
            Area.Monitoring, Level.Details, ToolKind.Read, AccountAsync) { ForCookie = true },
    ];

    private static object Login(AgentLoginEntity l, ToolContext ctx, AccessReviewState? state) => new
    {
        l.Id,
        At = Clock.ToLocal(l.At).ToString("yyyy-MM-dd HH:mm ddd", System.Globalization.CultureInfo.InvariantCulture),
        Account = ctx.Untrusted("account", l.Domain is { Length: > 0 } d ? $"{d}\\{l.User}" : l.User, 300),
        Admin = l.IsAdmin,
        Way = AccessWatch.Way(AccessSignals.WayOf(l.Kind)),
        Outcome = l.Outcome.ToString().ToLowerInvariant(),
        Count = l.Count > 1 ? l.Count : (int?)null,
        Last = l.LastAt is { } last ? Clock.ToLocal(last).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : null,
        Source = l.SourceAddress,
        PublicSource = AccessSignals.IsPublic(l.SourceAddress) ? true : (bool?)null,
        Device = ctx.UntrustedOrNull("workstation", l.Workstation, 200),
        Key = l.KeyFingerprint,
        Became = ctx.UntrustedOrNull("target user", l.TargetUser, 200),
        LoggedOff = l.LoggedOffAt is { } off ? Clock.ToLocal(off).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : null,
        Verdict = state?.ToString().ToLowerInvariant(),
    };

    private static async Task<(Guid Id, string Name, bool Router)> TargetAsync(PnmtdDbContext db, Guid sensorId, CancellationToken ct)
    {
        var sensor = await db.Sensors.AsNoTracking().Where(s => s.Id == sensorId).Select(s => new { s.Id, Host = s.Parent.Name, s.Type }).SingleOrDefaultAsync(ct)
            ?? throw ToolException.NotFound("server or router");
        var router = await db.MikrotikRouters.AnyAsync(r => r.SensorId == sensorId, ct);
        if (!router && !await db.Agents.AnyAsync(a => a.SensorId == sensorId, ct))
        {
            throw ToolException.Invalid("sensorId must be a server with PNMT's agent or a MikroTik router.");
        }
        return (sensor.Id, sensor.Host, router);
    }

    private static async Task<object> LoginsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var (id, name, router) = await TargetAsync(db, IdOf(args, "sensorId"), ct);
        var since = Clock.UtcNow.AddDays(-Math.Clamp(IntOf(args, "days", 7), 1, 14));
        var q = db.AgentLogins.AsNoTracking().Where(l => l.SensorId == id && (l.LastAt ?? l.At) >= since);
        if (OptionalText(args, "account", 300) is { Length: > 0 } account)
        {
            var bare = AccessSignals.Bare(account).ToLowerInvariant();
            q = q.Where(l => l.User.ToLower() == bare);
        }
        if (OptionalText(args, "source", 100) is { Length: > 0 } source)
        {
            q = q.Where(l => l.SourceAddress == source);
        }
        if (OptionalText(args, "outcome", 10) is { } outcome)
        {
            var o = outcome == "failure" ? LogonOutcome.Failure : LogonOutcome.Success;
            q = q.Where(l => l.Outcome == o);
        }
        if (OptionalText(args, "group", 20) is { } group)
        {
            var target = router ? AccessTargetKind.Router : AccessTargetKind.Server;
            var kinds = Enum.GetValues<LogonKind>().Where(k => AccessSignals.GroupOf(target, k, "") == group).ToList();
            q = q.Where(l => kinds.Contains(l.Kind));
        }
        var page = await Paging.From(args).ReadAsync(q.OrderByDescending(l => l.LastAt ?? l.At), ct);
        var ids = page.Items.Select(l => l.Id).ToList();
        var states = await db.AccessReviews.AsNoTracking().Where(r => r.AgentLoginId != null && ids.Contains(r.AgentLoginId.Value))
            .Select(r => new { Id = r.AgentLoginId!.Value, r.State }).ToListAsync(ct);
        var byId = states.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().State);
        ctx.Target("Sensor", id, name);
        ctx.Summary = $"{page.Items.Count} logins of {name}";
        return new
        {
            Target = name,
            Kind = router ? "router" : "server",
            page.NextCursor,
            page.Total,
            Items = page.Items.Select(l => Login(l, ctx, byId.TryGetValue(l.Id, out var s) ? s : null)),
        };
    }

    private static readonly string[] SignInEvents = [AuditEvents.LoginSucceeded, AuditEvents.LoginFailed, AuditEvents.LockedOut, AuditEvents.TotpFailed, AuditEvents.McpTokenOn];

    private static async Task<object> SignInsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var since = Clock.UtcNow.AddDays(-Math.Clamp(IntOf(args, "days", 7), 1, 14));
        var q = db.AuditLog.AsNoTracking().Where(a => SignInEvents.Contains(a.EventType) && a.CreatedAt >= since);
        if (OptionalText(args, "user", 200) is { Length: > 0 } user)
        {
            var u = user.ToLowerInvariant();
            q = q.Where(a => a.Username.ToLower() == u);
        }
        if (OptionalText(args, "source", 100) is { Length: > 0 } source)
        {
            q = q.Where(a => a.IpAddress == source);
        }
        var page = await Paging.From(args).ReadAsync(q.OrderByDescending(a => a.CreatedAt), ct);
        ctx.Summary = $"{page.Items.Count} of PNMT's sign-ins";
        return new
        {
            page.NextCursor,
            page.Total,
            Items = page.Items.Select(a => new
            {
                At = Clock.ToLocal(a.CreatedAt).ToString("yyyy-MM-dd HH:mm ddd", System.Globalization.CultureInfo.InvariantCulture),
                Event = a.EventType switch
                {
                    AuditEvents.LoginSucceeded => "signed in",
                    AuditEvents.LoginFailed => "failed",
                    AuditEvents.LockedOut => "locked out",
                    AuditEvents.TotpFailed => "2FA code failed",
                    _ => "MCP token switched on",
                },
                User = ctx.Privacy.Mask(a.Username),
                Source = a.IpAddress,
                PublicSource = AccessSignals.IsPublic(a.IpAddress) ? true : (bool?)null,
                Device = ctx.UntrustedOrNull("device", UserAgentFamily.Of(a.UserAgent), 100),
            }),
        };
    }

    private static async Task<object> ProfileAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var account = TextOf(args, "account", 1, 300);
        var bare = AccessSignals.Bare(account).ToLowerInvariant();
        var since = Clock.UtcNow.AddDays(-AccessHistory.MaxHistoryDays);
        if (OptionalId(args, "sensorId") is not { } sensorId)
        {
            if (!ctx.Rights.Has(Area.AuditLog, Level.View))
            {
                throw new ToolException("forbidden", "PNMT's sign-ins need Audit log: View.");
            }
            var signIns = await db.AuditLog.AsNoTracking()
                .Where(a => a.EventType == AuditEvents.LoginSucceeded && a.CreatedAt >= since && a.Username.ToLower() == bare)
                .OrderByDescending(a => a.CreatedAt).Take(AccessHistory.MaxHistory).Select(a => new { a.CreatedAt, a.IpAddress, a.UserAgent }).ToListAsync(ct);
            ctx.Summary = $"{signIns.Count} sign-ins of one PNMT user";
            return new
            {
                Target = AccessWatch.PnmtName,
                Account = ctx.Privacy.Mask(account),
                SignIns = signIns.Count,
                First = signIns.Count > 0 ? Clock.ToLocal(signIns[^1].CreatedAt).ToString("yyyy-MM-dd") : null,
                Last = signIns.Count > 0 ? Clock.ToLocal(signIns[0].CreatedAt).ToString("yyyy-MM-dd HH:mm") : null,
                Networks = Networks(signIns.Select(s => (s.IpAddress, s.CreatedAt))),
                Devices = signIns.GroupBy(s => UserAgentFamily.Of(s.UserAgent) ?? "unknown").Select(g => new { Device = g.Key, Count = g.Count() }).OrderByDescending(d => d.Count),
                Hours = Hours(signIns.Select(s => s.CreatedAt)),
            };
        }
        var (id, name, router) = await TargetAsync(db, sensorId, ct);
        var rows = await db.AgentLogins.AsNoTracking()
            .Where(l => l.SensorId == id && l.Outcome == LogonOutcome.Success && l.At >= since && l.User.ToLower() == bare)
            .OrderByDescending(l => l.At).Take(AccessHistory.MaxHistory).ToListAsync(ct);
        rows = [.. rows.Where(l => !account.Contains('\\') || AccessSignals.SameAccount(l.Domain is { Length: > 0 } d ? $"{d}\\{l.User}" : l.User, account))];
        var target = router ? AccessTargetKind.Router : AccessTargetKind.Server;
        ctx.Target("Sensor", id, name);
        ctx.Summary = $"{rows.Count} logins of one account on {name}";
        return new
        {
            Target = name,
            Account = ctx.Untrusted("account", account, 300),
            Logins = rows.Count,
            First = rows.Count > 0 ? Clock.ToLocal(rows[^1].At).ToString("yyyy-MM-dd") : null,
            Last = rows.Count > 0 ? Clock.ToLocal(rows[0].At).ToString("yyyy-MM-dd HH:mm") : null,
            Admin = rows.Any(r => r.IsAdmin),
            Kinds = rows.GroupBy(r => AccessSignals.GroupOf(target, r.Kind, "")).Select(g => new { Kind = g.Key, Count = g.Count() }).OrderByDescending(k => k.Count),
            Networks = Networks(rows.Select(r => (r.SourceAddress, r.At))),
            Hours = Hours(rows.Select(r => r.At)),
            Keys = rows.Where(r => r.KeyFingerprint is not null).GroupBy(r => r.KeyFingerprint).Select(g => new { Key = g.Key, Count = g.Count() }),
            Became = rows.Where(r => r.TargetUser is not null).GroupBy(r => r.TargetUser!.ToLowerInvariant())
                .Select(g => new { User = ctx.Untrusted("target user", g.Key, 100), Count = g.Count() }),
        };
    }

    private static IEnumerable<object> Networks(IEnumerable<(string? Source, DateTime At)> logins) =>
        logins.Where(l => l.Source is not null).GroupBy(l => AccessSignals.NetworkOf(l.Source)!)
            .Select(g => new
            {
                Network = g.Key,
                Count = g.Count(),
                Last = Clock.ToLocal(g.Max(x => x.At)).ToString("yyyy-MM-dd"),
                Public = AccessSignals.IsPublic(g.First().Source) ? true : (bool?)null,
            })
            .OrderByDescending(n => n.Count).Take(20);

    /// <summary>The hours of the week, as "Mon 08–17 (40)": each weekday's earliest and latest hour, and how many.</summary>
    private static IEnumerable<string> Hours(IEnumerable<DateTime> at) =>
        at.Select(Clock.ToLocal).GroupBy(l => l.DayOfWeek).OrderBy(g => ((int)g.Key + 6) % 7)
            .Select(g => $"{g.Key.ToString()[..3]} {g.Min(l => l.Hour):00}–{g.Max(l => l.Hour):00} ({g.Count()})");

    private static async Task<object> AccountAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var account = OptionalText(args, "account", 300);
        var source = OptionalText(args, "source", 100);
        if (string.IsNullOrWhiteSpace(account) && string.IsNullOrWhiteSpace(source))
        {
            throw ToolException.Invalid("Give an account or a source.");
        }
        var since = Clock.UtcNow.AddHours(-Math.Clamp(IntOf(args, "hours", 24), 1, 24));
        var q = db.AgentLogins.AsNoTracking().Where(l => (l.LastAt ?? l.At) >= since);
        if (!string.IsNullOrWhiteSpace(account))
        {
            var bare = AccessSignals.Bare(account).ToLowerInvariant();
            q = q.Where(l => l.User.ToLower() == bare);
        }
        if (!string.IsNullOrWhiteSpace(source))
        {
            q = q.Where(l => l.SourceAddress == source);
        }
        var rows = await q.OrderByDescending(l => l.At).Take(100).ToListAsync(ct);
        var sensors = rows.Select(r => r.SensorId).Distinct().ToList();
        var names = await db.Sensors.AsNoTracking().Where(s => sensors.Contains(s.Id)).Select(s => new { s.Id, Host = s.Parent.Name }).ToDictionaryAsync(s => s.Id, s => s.Host, ct);
        object? signIns = null;
        if (ctx.Rights.Has(Area.AuditLog, Level.View))
        {
            var a = db.AuditLog.AsNoTracking().Where(x => SignInEvents.Contains(x.EventType) && x.CreatedAt >= since);
            if (!string.IsNullOrWhiteSpace(source))
            {
                a = a.Where(x => x.IpAddress == source);
            }
            if (!string.IsNullOrWhiteSpace(account))
            {
                var bare = AccessSignals.Bare(account).ToLowerInvariant();
                a = a.Where(x => x.Username.ToLower() == bare);
            }
            signIns = (await a.OrderByDescending(x => x.CreatedAt).Take(30).ToListAsync(ct)).Select(x => new
            {
                At = Clock.ToLocal(x.CreatedAt).ToString("yyyy-MM-dd HH:mm"),
                x.EventType,
                User = ctx.Privacy.Mask(x.Username),
                Source = x.IpAddress,
            });
        }
        ctx.Summary = $"{rows.Count} logins on {names.Count} targets";
        return new
        {
            Targets = names.Count,
            Logins = rows.Select(r => new
            {
                Target = names.GetValueOrDefault(r.SensorId),
                r.SensorId,
                Kind = r.Channel == RouterLogins.Channel ? "router" : "server",
                Login = Login(r, ctx, null),
            }),
            PnmtSignIns = signIns,
        };
    }
}
