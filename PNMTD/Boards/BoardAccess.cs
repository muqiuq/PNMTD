using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Auth;
using PNMTD.Connectors;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Boards;

/// <summary>A board's paths (docs/boards-plan.md §4.1): the app at /pnmt-board/, its data at /pnmt-board/api/.</summary>
public static class BoardPaths
{
    public const string Path = "/pnmt-board";

    public const string Api = "/pnmt-board/api";

    public static bool IsBoardPath(PathString path) => path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase);

    public static bool IsApi(PathString path) => path.StartsWithSegments(Api, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The check of every data request (docs/boards-plan.md §5.2), in this order: the address isn't blocked and within its budget; the token
/// names a board (<c>Authorization: Board &lt;token&gt;</c>); the board is on; then a signed-in PNMT user is let in from any address, and
/// anyone else only from the board's networks (none: anywhere). The answers are ProblemDetails with a <c>code</c>, which the board app
/// shows full screen. A refusal by network is audited, at most once per board and address an hour.
/// </summary>
public sealed class BoardAccess(IDbContextFactory<PnmtdDbContext> dbs, BoardGuard guard, BoardRuntime runtime, IOptionsMonitor<BoardOptions> options,
    ILogger<BoardAccess> logger)
{
    public const string Scheme = "Board";

    /// <summary>The board, or the answer for the request.</summary>
    public sealed record Result(BoardRuntime.Row? Board, IResult? Refusal);

    public async Task<Result> CheckAsync(HttpContext http, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            return new Result(null, Results.NotFound());
        }
        var address = ConnectorNetworks.Text(http.Connection.RemoteIpAddress) ?? "unknown";
        if (guard.BlockedUntil(address) is { } until)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((until - Clock.UtcNow).TotalSeconds));
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return new Result(null, Problem(StatusCodes.Status429TooManyRequests, "blocked",
                $"Too many unknown boards from this address: try again in {seconds} s."));
        }
        if (!guard.Allow(address, options.CurrentValue.RequestsPerMinute))
        {
            http.Response.Headers.RetryAfter = "60";
            return new Result(null, Problem(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many requests from this address: try again in a minute."));
        }
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase) ? header[(Scheme.Length + 1)..].Trim() : null;
        var board = BoardTokens.LooksLikeOne(token) ? await FindAsync(BoardTokens.Hash(token!), ct) : null;
        if (board is null)
        {
            logger.LogInformation("A board request from {Address} with {Why}", address, token is null ? "no token" : "an unknown token");
            guard.Failed(address);
            return new Result(null, Problem(StatusCodes.Status404NotFound, "board_not_found",
                "There is no board at this address. Its URL may have been renewed, or the board deleted."));
        }
        if (!board.Enabled)
        {
            return new Result(board, Problem(StatusCodes.Status403Forbidden, "board_disabled", "This board is switched off."));
        }
        if (SignedIn(http.User) || board.Networks.Count == 0 || ConnectorNetworks.Contains(board.Networks, http.Connection.RemoteIpAddress))
        {
            return new Result(board, null);
        }
        if (runtime.AuditRefusal(board.Id, address))
        {
            await AuditRefusalAsync(board, address, http, ct);
        }
        return new Result(board, Problem(StatusCodes.Status403Forbidden, "board_network",
            $"This board can't be opened from {address}. An admin can add the network, or sign in to PNMT in this browser.",
            new Dictionary<string, object?> { ["address"] = address }));
    }

    /// <summary>
    /// A signed-in person (§5.2): a session the admin UI would accept as it is, past two-factor, with no password change or daily code
    /// pending. MCP tokens never count.
    /// </summary>
    public static bool SignedIn(ClaimsPrincipal user) =>
        LastSeen.IsPerson(user) && !user.HasClaim(SessionClaims.MustChangePassword, "true") && !user.HasClaim(SessionClaims.TotpSetupPending, "true")
        && !user.HasClaim(SessionClaims.TotpDue, "true");

    private async Task<BoardRuntime.Row?> FindAsync(string hash, CancellationToken ct)
    {
        if (runtime.Token(hash) is { } known)
        {
            return known;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var row = await db.Boards.AsNoTracking().Where(b => b.TokenHash == hash)
            .Select(b => new { b.Id, b.Name, b.Enabled, b.Networks, b.Layout, b.Revision }).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }
        var board = new BoardRuntime.Row(row.Id, row.Name, row.Enabled, ConnectorNetworks.Read(row.Networks), row.Layout, row.Revision);
        runtime.Remember(hash, board);
        return board;
    }

    private async Task AuditRefusalAsync(BoardRuntime.Row board, string address, HttpContext http, CancellationToken ct)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            var agent = http.Request.Headers.UserAgent.ToString();
            AuditLog.Record(db, AuditEvents.BoardRefused, $"{board.Name}: opened from {address}, outside its networks (at most one entry an hour for an address)",
                new AuditWho(AuditActor.System, null, "board", $"board:{board.Id}", address, agent.Length > 500 ? agent[..500] : agent),
                AuditTarget.Of(AuditTargets.Board, board.Id, board.Name), AuditOutcome.Denied);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "A board's refusal could not be recorded");
        }
    }

    public static IResult Problem(int status, string code, string detail, IDictionary<string, object?>? extra = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        foreach (var (key, value) in extra ?? new Dictionary<string, object?>())
        {
            extensions[key] = value;
        }
        return Results.Problem(statusCode: status, detail: detail, extensions: extensions);
    }
}
