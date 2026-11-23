using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Connectors;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Boards;

/// <summary>
/// The board app's data (docs/boards-plan.md §12.2), under /pnmt-board/api so that a reverse proxy can pass the boards without the
/// admin UI: the view, and the lounge's lines for its faster poll. Anonymous; every request passes <see cref="BoardAccess"/> first.
/// Only GETs: nothing on a board changes anything, so no CSRF token is needed.
/// </summary>
public static class BoardEndpoints
{
    public static IEndpointRouteBuilder MapBoardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(BoardPaths.Api + "/view", ViewAsync).AllowAnonymous().WithName("BoardView").WithTags("Boards")
            .Produces<BoardView>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        app.MapGet(BoardPaths.Api + "/lounge", LoungeAsync).AllowAnonymous().WithName("BoardLounge").WithTags("Boards")
            .Produces<BoardElementData>().ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    /// <summary>The board's layout, status bar and data: a view at most half its refresh old (at most 10 s), shared by its screens.</summary>
    private static async Task<IResult> ViewAsync(HttpContext http, BoardAccess access, BoardRuntime runtime, IServiceScopeFactory scopes,
        IDbContextFactory<PnmtdDbContext> dbs, CancellationToken ct)
    {
        var (board, refusal) = await access.CheckAsync(http, ct);
        if (refusal is not null)
        {
            return refusal;
        }
        var layout = BoardLayouts.Read(board!.Layout);
        var maxAge = TimeSpan.FromSeconds(Math.Min(layout.RefreshSeconds / 2.0, 10));
        // A build isn't the request's: a screen that goes away doesn't cancel the one its neighbours wait for.
        var view = await runtime.ViewAsync(board.Id, board.Revision, maxAge, async () =>
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BoardData>().BuildAsync(board.Name, layout, null, board.Id, CancellationToken.None);
        });
        await SeenAsync(http, board, runtime, dbs);
        return Results.Json(view);
    }

    /// <summary>The lounge element's lines (§7.6): 404 when the board has none.</summary>
    private static async Task<IResult> LoungeAsync(HttpContext http, BoardAccess access, BoardData data, CancellationToken ct)
    {
        var (board, refusal) = await access.CheckAsync(http, ct);
        if (refusal is not null)
        {
            return refusal;
        }
        var layout = BoardLayouts.Read(board!.Layout);
        var element = BoardLayouts.Elements(layout).FirstOrDefault(e => e.Type == BoardElementType.Lounge);
        if (element is null)
        {
            return BoardAccess.Problem(StatusCodes.Status404NotFound, "no_lounge", "This board doesn't show the lounge.");
        }
        return Results.Json(await data.LoungeAsync(element, layout.Look.People, ct));
    }

    /// <summary>Who showed it (§5.4): in memory for "screens now"; in the database at most once a minute.</summary>
    private static async Task SeenAsync(HttpContext http, BoardRuntime.Row board, BoardRuntime runtime, IDbContextFactory<PnmtdDbContext> dbs)
    {
        var address = ConnectorNetworks.Text(http.Connection.RemoteIpAddress) ?? "unknown";
        var agent = http.Request.Headers.UserAgent.ToString();
        agent = agent.Length > 200 ? agent[..200] : agent;
        if (!runtime.Seen(board.Id, address, agent))
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync();
        var now = Clock.UtcNow;
        await db.Boards.Where(b => b.Id == board.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.LastSeenAt, now)
            .SetProperty(b => b.LastSeenAddress, address.Length > 64 ? address[..64] : address).SetProperty(b => b.LastSeenAgent, agent));
    }
}
