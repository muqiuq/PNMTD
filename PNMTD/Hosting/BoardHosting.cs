using Microsoft.Extensions.Options;
using PNMTD.Boards;

namespace PNMTD.Hosting;

/// <summary>
/// The board app under /pnmt-board (docs/boards-plan.md §4.2), built as <see cref="WebUi"/> is: the shell's files are anonymous and hold
/// no data; deep links (/pnmt-board/&lt;token&gt;) get its index.html; /pnmt-board/api without an endpoint is a ProblemDetails 404. With
/// Boards__Enabled=false all of /pnmt-board answers a bare 404, as an unknown path does.
/// </summary>
public static class BoardHosting
{
    private const string IndexFile = "pnmt-board/index.html";

    private static readonly StaticFileOptions FileOptions = new()
    {
        OnPrepareResponse = context =>
        {
            context.Context.Response.Headers.CacheControl =
                context.Context.Request.Path.StartsWithSegments(BoardPaths.Path + "/assets")
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
        },
    };

    /// <summary>/pnmt-board → /pnmt-board/, then the files of the board's build.</summary>
    public static IApplicationBuilder UseBoardFiles(this IApplicationBuilder app)
    {
        app.Use((context, next) =>
        {
            var path = context.Request.Path;
            if (!BoardPaths.IsBoardPath(path))
            {
                return next(context);
            }
            if (!context.RequestServices.GetRequiredService<IOptionsMonitor<BoardOptions>>().CurrentValue.Enabled)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            if (path.Value is { } value && value.Equals(BoardPaths.Path, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                context.Response.Headers.Location = BoardPaths.Path + "/" + context.Request.QueryString;
                return Task.CompletedTask;
            }
            return next(context);
        });
        return app.UseWhen(c => BoardPaths.IsBoardPath(c.Request.Path) && !BoardPaths.IsApi(c.Request.Path), board => board.UseStaticFiles(FileOptions));
    }

    /// <summary>The shell for /pnmt-board/ and /pnmt-board/&lt;token&gt;; a ProblemDetails 404 for an unknown /pnmt-board/api path.</summary>
    public static void MapBoardApp(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapFallback(BoardPaths.Api + "/{**rest}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["code"] = "not_found" }))
            .AllowAnonymous().ExcludeFromDescription();
        endpoints.MapFallbackToFile(BoardPaths.Path + "/{**path:nonfile}", IndexFile, FileOptions).AllowAnonymous();
    }
}
