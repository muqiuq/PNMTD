using System.Text.Json;
using Microsoft.Extensions.Options;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Mcp;
using PNMTD.Models.Db;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>The token itself (docs/mcp-plan.md §7.2): what it is, until when, and what it may do. MCP only.</summary>
public static class SelfTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("whoami", "Who am I", "Your MCP token: read-only or read & write, whose it is, until when it is on, the rights MCP gives it, whether deleting " +
            "is allowed right now, and the limits. Call it first.",
            Schema(), null, Level.None, ToolKind.Read, WhoAmIAsync),
    ];

    private static async Task<object> WhoAmIAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var call = ctx.Mcp ?? throw new ToolException("mcp_only", "Only over MCP.");
        var tokens = ctx.Services.GetRequiredService<McpTokenService>();
        var options = ctx.Services.GetRequiredService<IOptionsMonitor<McpOptions>>().CurrentValue;
        var (read, write) = await tokens.RowsAsync(call.UserId, ct);
        var token = call.Kind == McpTokenKind.Write ? write : read;
        var user = await tokens.UserAsync(call.UserId, ct);
        var now = Clock.UtcNow;
        var state = McpTokenState.Of(token, user, options, now);
        ctx.Summary = $"{call.Username}'s {McpTokenService.KindLabel(call.Kind)} token";
        return new
        {
            Token = McpTokenService.KindLabel(call.Kind),
            User = user.DisplayName,
            user.Username,
            OnUntil = state.Until,
            Rights = ctx.Rights.Levels.OrderBy(l => l.Key).Select(l => $"{RightsCatalog.Of(l.Key).Label}: {l.Value}"),
            Deleting = call.Kind == McpTokenKind.Write
                ? token.DeleteUntil > now ? $"allowed until {token.DeleteUntil:O}, {options.DeletesPerWindow - token.DeleteCount} more objects"
                    : "not allowed: ask the person to click \"Allow deleting for 10 minutes\" on Account → Security in PNMT"
                : "never with a read-only token",
            Limits = new
            {
                options.CallsPerMinute, options.WritesPerMinute, options.CookieMessagesPerHour, options.MaxResultChars, options.DeletesPerWindow,
            },
            TimeZone = Clock.TimeZone.Id,
            Documentation = ctx.Link("admin/mcp"),
        };
    }
}
