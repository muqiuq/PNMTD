using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Connectors;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

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
        app.MapGet(BoardPaths.Api + "/messages", MessagesAsync).AllowAnonymous().WithName("BoardMessages").WithTags("Boards")
            .Produces<BoardMessages>().Produces(StatusCodes.Status304NotModified).ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
        app.MapGet(BoardPaths.Api + "/images/{fileId:guid}", ImageAsync).AllowAnonymous().WithName("BoardImage").WithTags("Boards")
            .Produces(StatusCodes.Status200OK, contentType: "image/png").ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
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

    /// <summary>
    /// The board's open messages (docs/board-messages-plan.md §6): polled every 5 s. The ETag is the process's and the board's stamp, so an
    /// unchanged one answers 304 without reading the database. With Look → Messages off, none. Read the stamp first: a message sent
    /// while this answer is made comes now or with the next stamp, never not at all.
    /// </summary>
    private static async Task<IResult> MessagesAsync(HttpContext http, BoardAccess access, BoardRuntime runtime, BoardMessageService messages,
        CancellationToken ct)
    {
        var (board, refusal) = await access.CheckAsync(http, ct);
        if (refusal is not null)
        {
            return refusal;
        }
        var tag = runtime.MessagesTag(board!.Id);
        http.Response.Headers.ETag = tag;
        if (http.Request.Headers.IfNoneMatch.ToString() == tag)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        var look = BoardLayouts.Read(board.Layout).Look;
        var list = look.Messages ? await messages.ForBoardAsync(board.Id, look.People, ct) : [];
        return Results.Json(new BoardMessages(list));
    }

    /// <summary>
    /// An image element's file (docs/board-messages-plan.md §9.4): only one the board's layout names. The board app fetches it with the
    /// token in the header and shows it from a <c>blob:</c> URL; files never change, so it may be kept for a year.
    /// </summary>
    private static async Task<IResult> ImageAsync(Guid fileId, HttpContext http, BoardAccess access, FileStore store, IDbContextFactory<PnmtdDbContext> dbs,
        CancellationToken ct)
    {
        var (board, refusal) = await access.CheckAsync(http, ct);
        if (refusal is not null)
        {
            return refusal;
        }
        if (!BoardLayouts.ImageIds(BoardLayouts.Read(board!.Layout)).Contains(fileId))
        {
            return BoardAccess.Problem(StatusCodes.Status404NotFound, "no_image", "This board doesn't show that image.");
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var file = await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && f.Kind == FileKind.Image, ct);
        return BoardImages.Serve(http, file, store) ?? BoardAccess.Problem(StatusCodes.Status404NotFound, "no_image", "This image was removed.");
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
