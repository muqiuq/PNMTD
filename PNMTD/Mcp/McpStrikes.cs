using System.Collections.Concurrent;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Notifications;

namespace PNMTD.Mcp;

/// <summary>Calls per token and minute (docs/mcp-plan.md §5.4), on PNMT's clock. In memory: a restart forgets them, which is fine.</summary>
public sealed class McpLimits(IOptionsMonitor<McpOptions> options)
{
    private readonly ConcurrentDictionary<(Guid Token, bool Write), Queue<DateTime>> windows = new();

    /// <summary>Null when the call may go ahead (and is counted); otherwise how long to wait.</summary>
    public TimeSpan? Take(Guid token, bool write)
    {
        var o = options.CurrentValue;
        var now = Clock.UtcNow;
        var all = windows.GetOrAdd((token, false), _ => new Queue<DateTime>());
        var writes = windows.GetOrAdd((token, true), _ => new Queue<DateTime>());
        lock (all)
        {
            lock (writes)
            {
                Prune(all, now);
                Prune(writes, now);
                if (all.Count >= Math.Max(1, o.CallsPerMinute))
                {
                    return all.Peek().AddMinutes(1) - now;
                }
                if (write && writes.Count >= Math.Max(1, o.WritesPerMinute))
                {
                    return writes.Peek().AddMinutes(1) - now;
                }
                all.Enqueue(now);
                if (write)
                {
                    writes.Enqueue(now);
                }
                return null;
            }
        }
    }

    private static void Prune(Queue<DateTime> window, DateTime now)
    {
        while (window.Count > 0 && window.Peek() <= now.AddMinutes(-1))
        {
            window.Dequeue();
        }
    }
}

/// <summary>
/// Strikes (docs/mcp-plan.md §8.6): a refused request that shows intent. <see cref="McpOptions.StrikesToBlock"/> within 24
/// hours turn the token off, block it, and alert every active admin by Pushover and e-mail.
/// </summary>
public sealed class McpStrikes(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<McpOptions> options, McpAlerts alerts)
{
    /// <summary>Counts a strike; true when it blocked the token.</summary>
    public async Task<bool> RecordAsync(Guid tokenId, string? baseUrl, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(CancellationToken.None);
        var token = await db.McpTokens.SingleOrDefaultAsync(t => t.Id == tokenId, CancellationToken.None);
        if (token is null)
        {
            return false;
        }
        var now = Clock.UtcNow;
        token.Strikes = [.. token.Strikes.Where(s => s > now.AddHours(-24)), now];
        var blocked = false;
        if (token.BlockedAt is null && token.Strikes.Count >= Math.Max(1, options.CurrentValue.StrikesToBlock))
        {
            blocked = true;
            McpTokenService.End(token, McpTokenEnd.Strikes, now);
            token.BlockedAt = now;
            var user = await db.Users.AsNoTracking().Where(u => u.Id == token.UserId).Select(u => new { u.Username, u.DisplayName }).SingleAsync(CancellationToken.None);
            var since = now.AddHours(-24);
            var refused = await db.McpLog.AsNoTracking().Where(l => l.TokenId == tokenId && l.Strike && l.At >= since).OrderBy(l => l.Id).Take(10)
                .ToListAsync(CancellationToken.None);
            var alerted = await alerts.TokenBlockedAsync(db, token, user.DisplayName, refused, baseUrl, CancellationToken.None);
            AuditLog.Record(db, AuditEvents.McpTokenBlocked, $"Blocked after {token.Strikes.Count} refused requests in 24 hours. {alerted}",
                AuditWho.System, McpTokenService.Target(token, user.Username));
        }
        await db.SaveChangesAsync(CancellationToken.None);
        return blocked;
    }
}

/// <summary>
/// The alert to every active admin when a token is blocked (docs/mcp-plan.md §8.6), straight through
/// <see cref="NotificationService"/>: Cookie and notification rules aren't involved, so it goes out whatever they do.
/// </summary>
public sealed class McpAlerts(NotificationService notifications, IConfiguration configuration)
{
    public const string Subject = "PNMT: an MCP token was blocked";

    /// <summary>Sends, and says who was alerted and how it went, for the audit entry.</summary>
    public async Task<string> TokenBlockedAsync(PnmtdDbContext db, McpTokenEntity token, string person, IReadOnlyList<McpLogEntity> refused,
        string? baseUrl, CancellationToken ct)
    {
        var admins = await db.Users.AsNoTracking().Where(u => u.IsAdmin && u.IsActive && (u.Email != null || u.PushoverKey != null))
            .OrderBy(u => u.Username).Select(u => new { u.Username, u.Email, u.PushoverKey }).ToListAsync(ct);
        if (admins.Count == 0)
        {
            return "No admin could be alerted: no active admin has an e-mail address or a Pushover key.";
        }
        var kind = McpTokenService.KindLabel(token.Kind);
        var first = $"{person}'s {kind} MCP token was blocked after {refused.Count} refused requests in 24 hours.";
        var text = new StringBuilder(first).Append("\n\nThe refused requests:\n");
        foreach (var r in refused)
        {
            text.Append("- ").Append(Clock.ToLocal(r.At).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append(": ")
                .Append(r.Tool ?? r.Method).Append(" — ").Append(r.Error ?? r.Summary)
                .Append(" (client ").Append(r.Client ?? r.UserAgent ?? "unknown").Append(", from ").Append(r.Ip ?? "unknown").Append(")\n");
        }
        text.Append("\nThe token stays off until ").Append(person).Append(" switches it on again on PNMT's dashboard.");
        if (baseUrl is not null)
        {
            text.Append("\nThe log: ").Append(baseUrl.TrimEnd('/')).Append("/pnmt-admin/admin/mcp/log?token=").Append(token.Id);
        }
        var simulate = configuration.GetValue("Development:DoNotSendNotifications", false);
        var outcomes = new List<string>();
        foreach (var admin in admins)
        {
            var sent = new List<string>();
            foreach (var (channel, recipient) in new[] { ("Pushover", admin.PushoverKey is { } k ? $"pushover://{k}" : null), ("e-mail", admin.Email) })
            {
                if (recipient is null)
                {
                    continue;
                }
                var result = await notifications.SendAsync(new NotificationMessage(recipient, Subject, first, text.ToString(), Importance.High),
                    "MCP token blocked", simulate, ct);
                sent.Add(result is { Ok: true } ? channel : $"{channel} failed");
            }
            outcomes.Add($"{admin.Username} ({string.Join(", ", sent)})");
        }
        return "Alerted: " + string.Join("; ", outcomes) + (simulate ? " (simulated)" : "") + ".";
    }
}
