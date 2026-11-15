using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Mcp;

/// <summary>What the user behind a token is, as far as the token's state needs it.</summary>
public sealed record McpTokenUser(Guid Id, string Username, string DisplayName, bool IsActive, string SecurityStamp, bool IsTotpEnabled,
    DateTime? LastSeenAt);

/// <summary>
/// Whether a token is on, until when, and if not, why (docs/mcp-plan.md §4.3). <see cref="Why"/>: <c>on</c>, <c>off</c>,
/// <c>no_value</c>, <c>expired</c>, <c>blocked</c>, <c>stamp</c>, <c>inactive</c>, <c>totp</c>, <c>mcp_off</c>.
/// </summary>
public sealed record McpTokenState(bool On, DateTime? Until, string Why)
{
    public static McpTokenState Of(McpTokenEntity token, McpTokenUser user, McpOptions options, DateTime now)
    {
        if (!options.Enabled)
        {
            return new(false, null, "mcp_off");
        }
        if (token.BlockedAt is not null)
        {
            return new(false, null, "blocked");
        }
        if (token.OnSince is not { } since)
        {
            return new(false, null, "off");
        }
        if (token.Hash is null)
        {
            return new(false, null, "no_value");
        }
        if (!user.IsActive)
        {
            return new(false, null, "inactive");
        }
        if (token.OnStamp != user.SecurityStamp)
        {
            return new(false, null, "stamp");
        }
        if (token.Kind == McpTokenKind.Write && options.WriteRequiresTotp && !user.IsTotpEnabled)
        {
            return new(false, null, "totp");
        }
        var until = token.Kind == McpTokenKind.Write
            ? token.OnUntil ?? since.AddHours(options.WriteHours)
            : (user.LastSeenAt is { } seen && seen > since ? seen : since).AddDays(options.ReadDaysAfterOnline);
        return until <= now ? new(false, until, "expired") : new(true, until, "on");
    }

    /// <summary>The end to record for a token that went off without anyone turning it off (§4.4).</summary>
    public McpTokenEnd? End => Why switch
    {
        "expired" => McpTokenEnd.Expired,
        "stamp" => McpTokenEnd.Stamp,
        "inactive" => McpTokenEnd.UserInactive,
        "totp" => McpTokenEnd.TotpOff,
        _ => null,
    };
}

/// <summary>A token checked for a request: its row, its user, and what MCP lets it do.</summary>
public sealed record McpCaller(McpTokenEntity Token, McpTokenUser User, UserRights Rights);

/// <summary>
/// The tokens (docs/mcp-plan.md §4): a user's own two, switched on, off and regenerated from the dashboard, the window for
/// deleting, and the check of a presented value. Every change is an audit event of the category MCP.
/// </summary>
public sealed class McpTokenService(PnmtdDbContext db, IOptionsMonitor<McpOptions> options, IHttpContextAccessor accessor)
{
    private McpOptions Options => options.CurrentValue;

    private AuditWho Who => AuditWho.From(accessor);

    public static string KindLabel(McpTokenKind kind) => kind == McpTokenKind.Write ? "read & write" : "read-only";

    public static AuditTarget Target(McpTokenEntity token, string username) =>
        AuditTarget.Of(AuditTargets.McpToken, token.Id, $"{username}: {KindLabel(token.Kind)}", token.UserId);

    /// <summary>Both rows of a user, made if missing (a user created after the migration, by any path).</summary>
    public async Task<(McpTokenEntity Read, McpTokenEntity Write)> RowsAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.McpTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        if (rows.Count < 2)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO mcp_tokens ("Id", "UserId", "Kind", "DeleteCount", "Strikes")
                VALUES (gen_random_uuid(), {userId}, 'Read', 0, ARRAY[]::timestamp with time zone[]),
                       (gen_random_uuid(), {userId}, 'Write', 0, ARRAY[]::timestamp with time zone[])
                ON CONFLICT ("UserId", "Kind") DO NOTHING
                """, ct);
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
            rows = await db.McpTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        }
        return (rows.Single(t => t.Kind == McpTokenKind.Read), rows.Single(t => t.Kind == McpTokenKind.Write));
    }

    public async Task<McpTokenUser> UserAsync(Guid userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new McpTokenUser(u.Id, u.Username, u.DisplayName, u.IsActive, u.SecurityStamp, u.IsTotpEnabled, u.LastSeenAt))
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("User");

    private async Task<(McpTokenEntity Token, McpTokenUser User)> MineAsync(Guid userId, McpTokenKind kind, CancellationToken ct)
    {
        if (!Options.Enabled)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "mcp_off", "The MCP server is off (Mcp__Enabled).");
        }
        var (read, write) = await RowsAsync(userId, ct);
        return (kind == McpTokenKind.Write ? write : read, await UserAsync(userId, ct));
    }

    /// <summary>
    /// Switches a token on, or extends the read-write one's 12 hours. Returns the value if one was made now: the first
    /// time, the token had none (§4.5).
    /// </summary>
    public async Task<(McpTokenEntity Token, McpTokenUser User, string? Value)> TurnOnAsync(Guid userId, McpTokenKind kind, CancellationToken ct)
    {
        var (token, user) = await MineAsync(userId, kind, ct);
        if (kind == McpTokenKind.Write && Options.WriteRequiresTotp && !user.IsTotpEnabled)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "totp_required", "The read & write token needs two-factor authentication on your account.");
        }
        var now = Clock.UtcNow;
        string? value = null;
        if (token.Hash is null)
        {
            value = Issue(token, now);
        }
        var extended = McpTokenState.Of(token, user, Options, now).On;
        token.OnSince = now;
        token.OnUntil = kind == McpTokenKind.Write ? now.AddHours(Options.WriteHours) : null;
        token.OnStamp = user.SecurityStamp;
        token.BlockedAt = null;
        token.Strikes = [];
        var until = McpTokenState.Of(token, user, Options, now).Until;
        AuditLog.Record(db, AuditEvents.McpTokenOn,
            (kind == McpTokenKind.Write
                ? $"{(extended ? "Extended" : "On")} for {Options.WriteHours} hours, until {Clock.ToLocal(until!.Value):yyyy-MM-dd HH:mm}"
                : $"On until {Options.ReadDaysAfterOnline} days after last online") + (value is null ? "" : "; a new value was made"),
            Who, Target(token, user.Username));
        await db.SaveChangesAsync(ct);
        return (token, user, value);
    }

    public async Task<(McpTokenEntity Token, McpTokenUser User)> TurnOffAsync(Guid userId, McpTokenKind kind, CancellationToken ct)
    {
        var (token, user) = await MineAsync(userId, kind, ct);
        var now = Clock.UtcNow;
        var deleteWindow = token.DeleteUntil > now;
        End(token, McpTokenEnd.Off, now);
        AuditLog.Record(db, AuditEvents.McpTokenOff, deleteWindow ? "Off; the window for deleting closed with it" : "Off", Who, Target(token, user.Username));
        await db.SaveChangesAsync(ct);
        return (token, user);
    }

    /// <summary>A new value; the old one stops working at once. The token stays on or off as it was (§4.4).</summary>
    public async Task<(McpTokenEntity Token, McpTokenUser User, string Value)> RegenerateAsync(Guid userId, McpTokenKind kind, CancellationToken ct)
    {
        var (token, user) = await MineAsync(userId, kind, ct);
        var now = Clock.UtcNow;
        var hadOne = token.Hash is not null;
        var value = Issue(token, now);
        token.DeleteUntil = null;
        AuditLog.Record(db, AuditEvents.McpTokenRegenerated, hadOne ? "A new value; the old one stopped working" : "The first value",
            Who, Target(token, user.Username));
        await db.SaveChangesAsync(ct);
        return (token, user, value);
    }

    /// <summary>"Allow deleting for 10 minutes" (§7.5): only while the read-write token is on.</summary>
    public async Task<(McpTokenEntity Token, McpTokenUser User)> OpenDeleteWindowAsync(Guid userId, CancellationToken ct)
    {
        var (token, user) = await MineAsync(userId, McpTokenKind.Write, ct);
        var now = Clock.UtcNow;
        if (!McpTokenState.Of(token, user, Options, now).On)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "token_off", "Turn the read & write token on first.");
        }
        token.DeleteUntil = now.AddMinutes(Options.DeleteMinutes);
        token.DeleteCount = 0;
        AuditLog.Record(db, AuditEvents.McpDeleteAllowed,
            $"Deleting allowed for {Options.DeleteMinutes} minutes, at most {Options.DeletesPerWindow} objects", Who, Target(token, user.Username));
        await db.SaveChangesAsync(ct);
        return (token, user);
    }

    public async Task<(McpTokenEntity Token, McpTokenUser User)> CloseDeleteWindowAsync(Guid userId, CancellationToken ct)
    {
        var (token, user) = await MineAsync(userId, McpTokenKind.Write, ct);
        if (token.DeleteUntil > Clock.UtcNow)
        {
            token.DeleteUntil = null;
            AuditLog.Record(db, AuditEvents.McpDeleteEnded, $"Stopped, {token.DeleteCount} deleted", Who, Target(token, user.Username));
            await db.SaveChangesAsync(ct);
        }
        return (token, user);
    }

    /// <summary>An admin with <i>MCP: Manage</i> turns someone's token off (§10.3), with a reason the user sees.</summary>
    public async Task<(McpTokenEntity Token, McpTokenUser User)> TurnOffByAdminAsync(Guid tokenId, string? reason, CancellationToken ct)
    {
        var why = reason?.Trim() ?? "";
        new ApiValidator().Check(why.Length is >= 3 and <= 300, "reason", "Why, in 3 to 300 characters: the token's user reads it.").ThrowIfInvalid();
        var token = await db.McpTokens.SingleOrDefaultAsync(t => t.Id == tokenId, ct) ?? throw ApiException.NotFound("Token");
        var user = await UserAsync(token.UserId, ct);
        End(token, McpTokenEnd.Admin, Clock.UtcNow, why);
        AuditLog.Record(db, AuditEvents.McpTokenTurnedOffByAdmin, why, Who, Target(token, user.Username));
        await db.SaveChangesAsync(ct);
        return (token, user);
    }

    /// <summary>
    /// The check of a presented value (§5.2): shaped like a token, its hash found, its user, and on. Null with why not.
    /// Writes nothing: what the request did goes into the MCP log.
    /// </summary>
    public async Task<(McpCaller? Caller, string Why)> AuthenticateAsync(string? value, CancellationToken ct)
    {
        if (!Options.Enabled)
        {
            return (null, "mcp_off");
        }
        if (!McpTokenValues.LooksLikeOne(value))
        {
            return (null, "unknown");
        }
        var hash = McpTokenValues.Hash(value!);
        var token = await db.McpTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Hash == hash, ct);
        if (token is null)
        {
            return (null, "unknown");
        }
        var user = await UserAsync(token.UserId, ct);
        var state = McpTokenState.Of(token, user, Options, Clock.UtcNow);
        if (!state.On)
        {
            return (new McpCaller(token, user, UserRights.None), state.Why);
        }
        var rights = (await SessionCache.LoadAsync(db, user.Id))?.Rights ?? UserRights.None;
        return (new McpCaller(token, user, McpRights.For(rights, token.Kind)), "on");
    }

    /// <summary>Who used it last, at most once a minute (§4.2).</summary>
    public async Task TouchAsync(McpTokenEntity token, string? from, string? client, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        if (token.LastUsedAt is { } last && now - last < TimeSpan.FromMinutes(1) && token.LastUsedFrom == from && token.LastClient == client)
        {
            return;
        }
        await db.McpTokens.Where(t => t.Id == token.Id).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.LastUsedAt, now)
            .SetProperty(t => t.LastUsedFrom, AuditLog.Cut(from, 64))
            .SetProperty(t => t.LastClient, AuditLog.Cut(client, 200)), ct);
    }

    private static string Issue(McpTokenEntity token, DateTime now)
    {
        var value = McpTokenValues.New(token.Kind);
        token.Hash = McpTokenValues.Hash(value);
        token.Hint = McpTokenValues.Hint(value);
        token.IssuedAt = now;
        return value;
    }

    internal static void End(McpTokenEntity token, McpTokenEnd why, DateTime at, string? note = null)
    {
        token.OnSince = null;
        token.OnUntil = null;
        token.OnStamp = null;
        token.DeleteUntil = null;
        token.EndedAt = at;
        token.EndedWhy = why;
        token.EndedNote = note;
    }
}
