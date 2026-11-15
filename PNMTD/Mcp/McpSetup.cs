using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PNMTD.Tools;

namespace PNMTD.Mcp;

/// <summary>
/// The MCP server (docs/mcp-plan.md §5): the SDK's Streamable HTTP transport, stateless, at <c>/mcp</c>, the token scheme,
/// the catalog's tools, and the filters that decide what a token is offered and may call. Off until <c>Mcp__Enabled</c>.
/// </summary>
public static class McpSetup
{
    public const string Path = "/mcp";
    public const string Policy = "mcp";

    public static IServiceCollection AddPnmtdMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<McpOptions>(configuration.GetSection(McpOptions.Section));
        services.AddScoped<McpTokenService>();
        services.AddSingleton<McpLog>();
        services.AddSingleton<McpLimits>();
        services.AddScoped<McpStrikes>();
        services.AddScoped<McpAlerts>();
        // Started in Program.cs with the other tasks, not while the build writes the OpenAPI document.
        services.AddSingleton<McpTokenExpiryTask>();

        // Never a default scheme: only /mcp's policy names it (§5.2).
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpTokenHandler>(McpTokenDefaults.Scheme, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, p => p.AddAuthenticationSchemes(McpTokenDefaults.Scheme).RequireAuthenticatedUser());

        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "pnmt", Title = "PNMT", Version = ServerInfo.Version };
                o.ServerInstructions = McpInstructions.Text;
            })
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(ToolCatalog.All.Select(t => (McpServerTool)new CatalogTool(t)))
            .WithRequestFilters(f =>
            {
                f.AddListToolsFilter(McpCalls.ListFilter);
                f.AddCallToolFilter(McpCalls.CallFilter);
            });
        return services;
    }

    public static bool IsMcpPath(PathString path) => path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The specification's protection against DNS rebinding (§5.2): a request that names an <c>Origin</c> must come from
    /// PNMT's own host. Clients send none; a browser on another site would.
    /// </summary>
    public static IApplicationBuilder UseMcpOriginCheck(this IApplicationBuilder app) =>
        app.UseWhen(c => IsMcpPath(c.Request.Path), mcp => mcp.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "origin", message = "Requests to /mcp from other sites are refused." });
                return;
            }
            await next(context);
        }));

    /// <summary>Maps <c>/mcp</c> while <c>Mcp__Enabled</c> is true; otherwise it doesn't exist (an empty 404).</summary>
    public static void MapPnmtdMcp(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<McpOptions>>().Value.Enabled)
        {
            app.MapMcp(Path).RequireAuthorization(Policy);
        }
    }
}
