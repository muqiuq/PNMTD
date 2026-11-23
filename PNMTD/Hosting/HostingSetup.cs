using PNMTD.Api;

namespace PNMTD.Hosting;

/// <summary>
/// The admin UI under /pnmt-admin, the response headers, and the plain 404/405 for everything that
/// matches no endpoint (docs/webui-migration-plan.md §3.5).
/// </summary>
public static class WebUi
{
    /// <summary>Fixed by design; PNMTD.WebUI/vite.config.ts has the same base.</summary>
    public const string BasePath = "/pnmt-admin";

    private const string IndexFile = "pnmt-admin/index.html";

    private static readonly StaticFileOptions FileOptions = new()
    {
        OnPrepareResponse = context =>
        {
            // Vite's hashed assets never change; index.html and the rest are revalidated.
            context.Context.Response.Headers.CacheControl =
                context.Context.Request.Path.StartsWithSegments(BasePath + "/assets")
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
        },
    };

    /// <summary>/pnmt-admin → /pnmt-admin/, then the files of the build (anonymous: the shell holds no data).</summary>
    public static IApplicationBuilder UseWebUiFiles(this IApplicationBuilder app)
    {
        app.Use((context, next) =>
        {
            if (context.Request.Path.Value is { } path && path.Equals(BasePath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                context.Response.Headers.Location = BasePath + "/" + context.Request.QueryString;
                return Task.CompletedTask;
            }
            return next(context);
        });
        return app.UseWhen(c => c.Request.Path.StartsWithSegments(BasePath), ui => ui.UseStaticFiles(FileOptions));
    }

    /// <summary>
    /// Deep links (/pnmt-admin/hosts/…) get index.html. Paths with a file extension do not, so a
    /// missing asset is a 404 rather than HTML. Unknown /api paths get a ProblemDetails 404 (401
    /// when signed out), never the UI.
    /// </summary>
    public static void MapWebUi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapFallbackToFile(BasePath + "/{**path:nonfile}", IndexFile, FileOptions)
            .AllowAnonymous();
        endpoints.MapFallback(ApiPaths.V1 + "/{**rest}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound))
            .ExcludeFromDescription();
        endpoints.MapFallback("/api/{**rest}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound))
            .ExcludeFromDescription();
    }
}

public static class HostingSetup
{
    private const string UiCsp =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; font-src 'self'; " +
        "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    private const string ApiCsp = "default-src 'none'; frame-ancestors 'none'";

    /// <summary>
    /// For what serves content: the UI and the API. Never for the sensor API, whose responses keep
    /// v0.12's headers byte for byte (§3.3), nor for the empty 404s and 405s elsewhere (/, /foo,
    /// the removed routes), which also answer as v0.12 did.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var path = context.Request.Path;
            // The board app is a UI of its own, its data an API (docs/boards-plan.md §4.2); neither is for search engines.
            var isBoard = Boards.BoardPaths.IsBoardPath(path);
            var isApi = ApiPaths.IsApi(path) || Boards.BoardPaths.IsApi(path);
            var isUi = path.StartsWithSegments(WebUi.BasePath) || (isBoard && !isApi);
            if (isUi || isApi)
            {
                context.Response.OnStarting(() =>
                {
                    var headers = context.Response.Headers;
                    headers.XContentTypeOptions = "nosniff";
                    headers.XFrameOptions = "DENY";
                    headers["Referrer-Policy"] = "no-referrer";
                    headers["Cross-Origin-Opener-Policy"] = "same-origin";
                    headers["Cross-Origin-Resource-Policy"] = "same-origin";
                    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                    headers.ContentSecurityPolicy = isUi ? UiCsp : ApiCsp;
                    if (isBoard)
                    {
                        headers["X-Robots-Tag"] = "noindex, nofollow";
                    }
                    if (isApi && !headers.ContainsKey("Cache-Control"))
                    {
                        headers.CacheControl = "no-store";
                    }
                    return Task.CompletedTask;
                });
            }
            return next(context);
        });

    /// <summary>
    /// Between routing and authentication. Deny by default would answer 401 for a request that
    /// matches no endpoint, and for routing's 405 endpoint. v0.12 answered those with an empty 404
    /// and an empty 405 (with Allow), and sensor clients rely on it (§3.3), so they are answered here,
    /// for every path, before authentication. A catch-all MapFallback cannot do this: routing only
    /// creates its 405 endpoint when no endpoint for the path accepts the method, and a catch-all
    /// accepts every method.
    /// </summary>
    public static IApplicationBuilder UsePlainNotFound(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (endpoint is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }
            if (endpoint.DisplayName == Http405EndpointDisplayName && endpoint.RequestDelegate is { } reject)
            {
                return reject(context);
            }
            return next(context);
        });

    /// <summary>The name of the endpoint HttpMethodMatcherPolicy creates for a 405 (not public API).</summary>
    public const string Http405EndpointDisplayName = "405 HTTP Method Not Supported";
}
