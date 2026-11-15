using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Enums;

namespace PNMTD.Mcp;

/// <summary>
/// Every 5 minutes (docs/mcp-plan.md §4.3): records the end of tokens that went off by themselves (their time, their user's
/// stamp, deactivation, 2FA turned off) and of windows for deleting that ran out, so Account → Security and the audit say when.
/// Requests never wait for it: they check the state themselves. In task health as "MCP tokens".
/// </summary>
public sealed class McpTokenExpiryTask(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<McpOptions> options, ILogger<McpTokenExpiryTask> logger)
    : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "The MCP tokens' expiry failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var now = Clock.UtcNow;
        var tokens = await db.McpTokens.Where(t => t.OnSince != null || t.DeleteUntil != null).ToListAsync(ct);
        var ids = tokens.Select(t => t.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .Select(u => new McpTokenUser(u.Id, u.Username, u.DisplayName, u.IsActive, u.SecurityStamp, u.IsTotpEnabled, u.LastSeenAt))
            .ToDictionaryAsync(u => u.Id, ct);
        var ended = 0;
        foreach (var token in tokens)
        {
            if (!users.TryGetValue(token.UserId, out var user))
            {
                continue;
            }
            if (token.DeleteUntil is { } until && until <= now)
            {
                AuditLog.Record(db, AuditEvents.McpDeleteEnded, $"Ran out, {token.DeleteCount} deleted", AuditWho.System, McpTokenService.Target(token, user.Username));
                token.DeleteUntil = null;
            }
            var state = McpTokenState.Of(token, user, settings, now);
            if (token.OnSince is not null && !state.On && state.End is { } why)
            {
                var windowOpen = token.DeleteUntil > now;
                McpTokenService.End(token, why, state.Until is { } at && at <= now ? at : now);
                AuditLog.Record(db, AuditEvents.McpTokenExpired, $"Off by itself: {Reason(why)}{(windowOpen ? "; the window for deleting closed with it" : "")}",
                    AuditWho.System, McpTokenService.Target(token, user.Username));
                ended++;
            }
        }
        db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_MCP_TOKEN_EXPIRY);
        await db.SaveChangesAsync(ct);
        return ended;
    }

    private static string Reason(Models.Db.McpTokenEnd why) => why switch
    {
        Models.Db.McpTokenEnd.Expired => "its time is over",
        Models.Db.McpTokenEnd.Stamp => "its user's password, 2FA or sessions changed",
        Models.Db.McpTokenEnd.UserInactive => "its user was deactivated",
        Models.Db.McpTokenEnd.TotpOff => "its user has no 2FA any more",
        _ => why.ToString(),
    };
}
