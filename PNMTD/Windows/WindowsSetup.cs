using PNMTD.Agents;
using PNMTD.Lib.Agent;

namespace PNMTD.Windows;

public static class WindowsSetup
{
    /// <summary>Windows servers and their agents (docs/windows-sensor-plan.md §8.4).</summary>
    public static IServiceCollection AddWindowsAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WindowsOptions>(configuration.GetSection(WindowsOptions.Section));
        services.AddSingleton<AgentKeyStore>();
        services.AddSingleton<AgentHub>();
        services.AddSingleton<JoinGuard>();
        services.AddSingleton<WindowsReportService>();
        services.AddSingleton<WindowsChanges>();
        services.AddSingleton<WindowsMaintenanceTask>();
        services.AddSingleton<WindowsDownloads>();
        return services;
    }

    /// <summary>The WebSocket's middleware, for its path only (§3.5): the keep-alive ping every 30 s.</summary>
    public static IApplicationBuilder UseAgentWebSockets(this IApplicationBuilder app) =>
        app.UseWhen(c => AgentPaths.IsWebSocket(c.Request.Path),
            b => b.UseWebSockets(new WebSocketOptions { KeepAliveInterval = AgentProtocol.KeepAlive }));

    /// <summary>The three anonymous agent endpoints (§9.1): the join (GET and POST) and the WebSocket.</summary>
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(AgentProtocol.JoinPath, AgentJoinEndpoint.InfoAsync).AllowAnonymous().ExcludeFromDescription();
        app.MapPost(AgentProtocol.JoinPath, AgentJoinEndpoint.JoinAsync).AllowAnonymous().ExcludeFromDescription();
        app.MapGet(AgentProtocol.WebSocketPath, (HttpContext context, AgentHub hub) => hub.HandleAsync(context)).AllowAnonymous().ExcludeFromDescription();
        return app;
    }
}
