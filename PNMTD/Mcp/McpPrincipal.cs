using System.Security.Claims;
using PNMTD.Auth;
using PNMTD.Models.Db;

namespace PNMTD.Mcp;

/// <summary>
/// The principal of an MCP request (docs/mcp-plan.md §5.2): the token's user, the token, and the capped rights as the
/// usual <c>pnmt:right</c> claims, so that every <c>User.Has(…)</c> in PNMT's services sees what the token may do.
/// Never <c>pnmt:admin</c>.
/// </summary>
public static class McpPrincipal
{
    public const string TokenId = "pnmt:mcp_token";
    public const string Kind = "pnmt:mcp_kind";

    public static ClaimsPrincipal Create(McpCaller caller)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, caller.User.Id.ToString()),
            new(ClaimTypes.Name, caller.User.Username),
            new(SessionClaims.DisplayName, caller.User.DisplayName),
            new(TokenId, caller.Token.Id.ToString()),
            new(Kind, caller.Token.Kind.ToString()),
        };
        claims.AddRange(caller.Rights.Claims().Where(c => c.Type != SessionClaims.Admin));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpTokenDefaults.Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static bool IsMcp(ClaimsPrincipal? principal) =>
        principal?.Identity is { IsAuthenticated: true, AuthenticationType: McpTokenDefaults.Scheme };

    public static Guid? TokenOf(ClaimsPrincipal? principal) =>
        IsMcp(principal) && Guid.TryParse(principal!.FindFirstValue(TokenId), out var id) ? id : null;

    public static McpTokenKind? KindOf(ClaimsPrincipal? principal) =>
        IsMcp(principal) && Enum.TryParse<McpTokenKind>(principal!.FindFirstValue(Kind), out var kind) ? kind : null;

    /// <summary>The audit's <c>ActorRef</c>: <c>token:{id}:rw</c> or <c>:ro</c>.</summary>
    public static string? Reference(ClaimsPrincipal? principal) =>
        TokenOf(principal) is { } id ? $"token:{id}:{(KindOf(principal) == McpTokenKind.Write ? "rw" : "ro")}" : null;
}
