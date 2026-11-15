using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Tools;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Lib.Helper;
using PNMTD.Mcp;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1;

/// <summary>
/// One's own MCP tokens (docs/mcp-plan.md §4.5, §12): the MCP panel on Account → Security. Every signed-in user has both; only they
/// switch them on, off, regenerate them and open the window for deleting. The value is in a response once, never again.
/// </summary>
[Route("api/v1/account/mcp")]
[Authorize]
public sealed class AccountMcpController(McpTokenService tokens, IOptionsMonitor<McpOptions> options) : V1ControllerBase
{
    private Guid Me => User.UserId() ?? throw ApiException.NotFound("User");

    [HttpGet]
    public async Task<McpAccountItem> Get(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var (read, write) = await tokens.RowsAsync(Me, ct);
        var user = await tokens.UserAsync(Me, ct);
        return new McpAccountItem(o.Enabled, $"{McpCalls.BaseUrl(HttpContext)}{McpSetup.Path}", o.WriteHours, o.ReadDaysAfterOnline, o.WriteRequiresTotp,
            user.IsTotpEnabled, o.DeleteMinutes, o.DeletesPerWindow, Item(read, user), Item(write, user));
    }

    /// <summary>Switches a token on, or extends the read & write one's hours. The value comes along if one was made now.</summary>
    [HttpPost("{kind:regex(^(read|write)$)}/on")]
    [Audit(AuditEvents.McpTokenOn)]
    public async Task<McpTokenIssued> TurnOn(McpTokenKind kind, CancellationToken ct)
    {
        var (token, user, value) = await tokens.TurnOnAsync(Me, kind, ct);
        NoStore();
        return new McpTokenIssued(Item(token, user), value);
    }

    [HttpPost("{kind:regex(^(read|write)$)}/off")]
    [Audit(AuditEvents.McpTokenOff)]
    public async Task<McpTokenItem> TurnOff(McpTokenKind kind, CancellationToken ct)
    {
        var (token, user) = await tokens.TurnOffAsync(Me, kind, ct);
        return Item(token, user);
    }

    /// <summary>A new value, shown once; the old one stops working at once.</summary>
    [HttpPost("{kind:regex(^(read|write)$)}/regenerate")]
    [Audit(AuditEvents.McpTokenRegenerated)]
    public async Task<McpTokenIssued> Regenerate(McpTokenKind kind, CancellationToken ct)
    {
        var (token, user, value) = await tokens.RegenerateAsync(Me, kind, ct);
        NoStore();
        return new McpTokenIssued(Item(token, user), value);
    }

    /// <summary>"Allow deleting for 10 minutes" (§7.5): only while the read & write token is on. 409 token_off.</summary>
    [HttpPost("write/delete-window")]
    [Audit(AuditEvents.McpDeleteAllowed)]
    public async Task<McpTokenItem> OpenDeleteWindow(CancellationToken ct)
    {
        var (token, user) = await tokens.OpenDeleteWindowAsync(Me, ct);
        return Item(token, user);
    }

    /// <summary><i>Stop now</i>.</summary>
    [HttpDelete("write/delete-window")]
    [Audit(AuditEvents.McpDeleteEnded)]
    public async Task<McpTokenItem> CloseDeleteWindow(CancellationToken ct)
    {
        var (token, user) = await tokens.CloseDeleteWindowAsync(Me, ct);
        return Item(token, user);
    }

    /// <summary>One's own MCP log (the card's <i>Your log</i>): both tokens, newest first.</summary>
    [HttpGet("log")]
    public async Task<Paged<McpLogItem>> Log([FromQuery] McpLogQuery query, [FromServices] PnmtdDbContext db, CancellationToken ct) =>
        await McpLogController.PageAsync(db, query, Me, ct);

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private McpTokenItem Item(McpTokenEntity t, McpTokenUser user) => Item(t, user, options.CurrentValue, McpRights.For(User.Rights(), t.Kind).Levels);

    internal static McpTokenItem Item(McpTokenEntity t, McpTokenUser user, McpOptions options, IReadOnlyDictionary<Area, Level> rights)
    {
        var now = Clock.UtcNow;
        var state = McpTokenState.Of(t, user, options, now);
        return new McpTokenItem(t.Kind, state.On, state.Why, state.Until, t.Hash is not null, t.Hint, t.IssuedAt, t.LastUsedAt, t.LastUsedFrom, t.LastClient,
            t.EndedAt, t.EndedWhy, t.EndedNote, t.BlockedAt, t.Strikes.Count(s => s > now.AddHours(-24)), t.DeleteUntil > now ? t.DeleteUntil : null,
            t.DeleteCount, rights);
    }
}

/// <summary>
/// Admin → MCP (docs/mcp-plan.md §10): the documentation, generated from the catalog, for every signed-in user; the log and
/// everyone's tokens with <i>MCP: View</i>; turning a token off with <i>MCP: Manage</i>. An entry of one's own is readable
/// without the right.
/// </summary>
[Route("api/v1/mcp")]
[Authorize]
public sealed class McpLogController(PnmtdDbContext db, IOptionsMonitor<McpOptions> options) : V1ControllerBase
{
    [HttpGet("catalog")]
    public McpCatalog Catalog()
    {
        var o = options.CurrentValue;
        var services = HttpContext.RequestServices;
        var ceiling = RightsCatalog.Areas.Select(a => new McpCeilingRow(a.Area, a.Label, McpRights.Ceiling.TryGetValue(a.Area, out var rw) ? rw : null,
            McpRights.ReadOnlyCap.TryGetValue(a.Area, out var ro) ? ro : null)).ToList();
        var guard = new Dictionary<string, string>
        {
            ["injection"] = Cookie.CookieRound.ChatInjectionQuestion, ["acts"] = McpGuard.Acts, ["settings"] = McpGuard.Settings, ["secrets"] = McpGuard.Secrets,
        };
        var tools = ToolCatalog.All.Select(t => new McpToolDoc(t.Name, t.Title, t.Description, t.Kind, t.RightText,
            ToolCatalog.KindAllows(t, McpTokenKind.Read), t.Kind == ToolKind.Delete || t.Destructive, t.ForCookie, t.InputSchema,
            t.Available?.Invoke(services) ?? true)).ToList();
        return new McpCatalog(o.Enabled, $"{McpCalls.BaseUrl(HttpContext)}{McpSetup.Path}", McpInstructions.Text, o.WriteHours, o.ReadDaysAfterOnline,
            o.WriteRequiresTotp, o.DeleteMinutes, o.DeletesPerWindow, o.CallsPerMinute, o.WritesPerMinute, o.CookieMessagesPerHour, o.MaxResultChars,
            o.StrikesToBlock, o.LogEntries, ceiling, guard, tools);
    }

    /// <summary>Admin → MCP → Status (docs/adjustments1.md): the tokens and the calls at a glance.</summary>
    [HttpGet("status")]
    [RequireRight(Area.Mcp, Level.View)]
    public async Task<McpStatus> Status(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = Clock.UtcNow;
        var tokens = await db.McpTokens.AsNoTracking().ToListAsync(ct);
        var users = await db.Users.AsNoTracking()
            .Select(u => new McpTokenUser(u.Id, u.Username, u.DisplayName, u.IsActive, u.SecurityStamp, u.IsTotpEnabled, u.LastSeenAt)).ToDictionaryAsync(u => u.Id, ct);
        var states = tokens.Where(t => users.ContainsKey(t.UserId)).Select(t => (Token: t, State: McpTokenState.Of(t, users[t.UserId], o, now))).ToList();
        var day = now.AddDays(-1);
        var week = now.AddDays(-7);
        var calls = db.McpLog.AsNoTracking().Where(l => l.Method == "tools/call");
        var outcomes = await calls.Where(l => l.At >= day).GroupBy(l => l.Outcome).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var tools = await calls.Where(l => l.At >= week && l.Tool != null).GroupBy(l => l.Tool!).Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).Take(10).ToListAsync(ct);
        var people = await calls.Where(l => l.At >= week).GroupBy(l => l.Username).Select(g => new { g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count).Take(10).ToListAsync(ct);
        var recent = await calls.Where(l => l.At >= week).Select(l => new { l.At, Ok = l.Outcome == McpOutcome.Ok }).ToListAsync(ct);
        var days = Enumerable.Range(0, 7).Select(i => DateOnly.FromDateTime(Clock.LocalNow).AddDays(-6 + i))
            .Select(d => new McpDay(d, recent.Count(r => DateOnly.FromDateTime(Clock.ToLocal(r.At)) == d),
                recent.Count(r => !r.Ok && DateOnly.FromDateTime(Clock.ToLocal(r.At)) == d))).ToList();
        return new McpStatus(o.Enabled,
            states.Count(s => s.State.On && s.Token.Kind == McpTokenKind.Read), states.Count(s => s.State.On && s.Token.Kind == McpTokenKind.Write),
            tokens.Count(t => t.BlockedAt is not null), tokens.Count(t => t.Hash is not null), tokens.Count(t => t.DeleteUntil > now),
            outcomes.Sum(x => x.Count), recent.Count, await db.McpLog.CountAsync(l => l.Strike && l.At >= day, ct),
            await db.McpLog.Where(l => l.Method == "tools/call").MaxAsync(l => (DateTime?)l.At, ct),
            [.. outcomes.OrderByDescending(x => x.Count).Select(x => new McpCount(x.Key.ToString(), x.Count))],
            [.. tools.Select(x => new McpCount(x.Key, x.Count))], [.. people.Select(x => new McpCount(x.Key, x.Count))], days);
    }

    [HttpGet("log")]
    [RequireRight(Area.Mcp, Level.View)]
    public async Task<Paged<McpLogItem>> Log([FromQuery] McpLogQuery query, CancellationToken ct) => await PageAsync(db, query, null, ct);

    /// <summary>An entry with its arguments, result and changes: with <i>MCP: View</i>, or one's own.</summary>
    [HttpGet("log/{id:long}")]
    public async Task<McpLogDetail> Entry(long id, CancellationToken ct)
    {
        var e = await db.McpLog.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct);
        if (e is null || (!User.Has(Area.Mcp, Level.View) && e.UserId != User.UserId()))
        {
            throw ApiException.NotFound("MCP log entry");
        }
        var changes = e.CorrelationId.Length == 0 ? []
            : await db.AuditLog.AsNoTracking().Where(l => l.CorrelationId == e.CorrelationId).OrderBy(l => l.CreatedAt).ToListAsync(ct);
        return new McpLogDetail(ItemOf(e), e.CorrelationId, e.UserAgent, e.Arguments, e.Result is null ? null : McpLog.Unpack(e.Result), e.Truncated, e.Error,
            e.Targets, e.Guard, e.CookieMessageId, [.. changes.Select(AuditLogController.ToItem)]);
    }

    [HttpGet("tokens")]
    [RequireRight(Area.Mcp, Level.View)]
    public async Task<IReadOnlyList<McpAdminTokenItem>> Tokens(CancellationToken ct)
    {
        var tokens = await db.McpTokens.AsNoTracking().ToListAsync(ct);
        var users = await db.Users.AsNoTracking()
            .Select(u => new McpTokenUser(u.Id, u.Username, u.DisplayName, u.IsActive, u.SecurityStamp, u.IsTotpEnabled, u.LastSeenAt)).ToDictionaryAsync(u => u.Id, ct);
        var o = options.CurrentValue;
        return [.. tokens.Where(t => users.ContainsKey(t.UserId)).Select(t => new McpAdminTokenItem(t.Id, t.UserId, users[t.UserId].Username,
                users[t.UserId].DisplayName, AccountMcpController.Item(t, users[t.UserId], o, new Dictionary<Area, Level>())))
            .OrderByDescending(t => t.Token.BlockedAt is not null).ThenByDescending(t => t.Token.On).ThenBy(t => t.Username).ThenBy(t => t.Token.Kind)];
    }

    [HttpPost("tokens/{id:guid}/off")]
    [RequireRight(Area.Mcp, Level.Manage)]
    [Audit(AuditEvents.McpTokenTurnedOffByAdmin)]
    public async Task<McpAdminTokenItem> TurnOff(Guid id, McpTurnOffRequest request, [FromServices] McpTokenService tokens, CancellationToken ct)
    {
        var (token, user) = await tokens.TurnOffByAdminAsync(id, request.Reason, ct);
        return new McpAdminTokenItem(token.Id, user.Id, user.Username, user.DisplayName,
            AccountMcpController.Item(token, user, options.CurrentValue, new Dictionary<Area, Level>()));
    }

    /// <summary><i>Revoke all tokens</i> (docs/adjustments1.md): every value stops working; everyone makes a new one.</summary>
    [HttpPost("tokens/revoke-all")]
    [RequireRight(Area.Mcp, Level.Manage)]
    [Audit(AuditEvents.McpTokensRevokedByAdmin)]
    public async Task<McpRevokeAllResult> RevokeAll(McpRevokeAllRequest request, [FromServices] McpTokenService tokens, CancellationToken ct) =>
        new(await tokens.RevokeAllAsync(request.Reason, ct));

    internal static McpLogItem ItemOf(McpLogEntity e) => new(e.Id, e.At, e.TokenId, e.Kind, e.UserId, e.Username, e.Ip, e.Client, e.Method, e.Tool, e.ToolKind,
        e.Outcome, e.ErrorCode, e.Summary, e.Strike, e.DurationMs);

    internal static async Task<Paged<McpLogItem>> PageAsync(PnmtdDbContext db, McpLogQuery query, Guid? onlyUser, CancellationToken ct)
    {
        new ApiValidator().Check(query.Page >= 1, "page", "From 1.").Check(query.PageSize is >= 1 and <= 200, "pageSize", "1 to 200.").ThrowIfInvalid();
        var q = db.McpLog.AsNoTracking();
        if ((onlyUser ?? query.UserId) is { } user)
        {
            q = q.Where(l => l.UserId == user);
        }
        if (query.TokenId is { } token)
        {
            q = q.Where(l => l.TokenId == token);
        }
        if (query.Kind is { } kind)
        {
            q = q.Where(l => l.Kind == kind);
        }
        if (!string.IsNullOrWhiteSpace(query.Tool))
        {
            q = q.Where(l => l.Tool == query.Tool);
        }
        if (query.Outcome is { } outcome)
        {
            q = q.Where(l => l.Outcome == outcome);
        }
        if (query.From is { } from)
        {
            var f = Clock.AsUtc(from);
            q = q.Where(l => l.At >= f);
        }
        if (query.To is { } to)
        {
            var t = Clock.AsUtc(to);
            q = q.Where(l => l.At <= t);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim().Replace("%", "").Replace("_", "")}%";
            q = q.Where(l => EF.Functions.ILike(l.Summary, pattern) || (l.Targets != null && EF.Functions.ILike(l.Targets, pattern)) || EF.Functions.ILike(l.Username, pattern));
        }
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(l => l.Id).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        return new Paged<McpLogItem>([.. rows.Select(ItemOf)], query.Page, query.PageSize, total);
    }
}
