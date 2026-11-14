using PNMTD.Lib.Agent;

namespace PNMTD.Agents;

public static class AgentSetup
{
    /// <summary>
    /// Servers with an agent, Windows and Linux (docs/windows-sensor-plan.md §8.4; docs/linux-sensor-plan.md §9.4): the hub,
    /// the join, PNMTD's key, what agents report, diagnostics and scripts' results, retention and the downloads.
    /// </summary>
    public static IServiceCollection AddAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        services.AddSingleton<AgentKeyStore>();
        services.AddSingleton<AgentHub>();
        services.AddSingleton<JoinGuard>();
        services.AddSingleton<AgentReportService>();
        services.AddSingleton<AgentChanges>();
        services.AddSingleton<AgentAccessService>();
        services.AddSingleton<Diagnostics.DiagnosticsRunner>();
        services.AddSingleton<AgentScriptResults>();
        services.AddSingleton<AgentMaintenanceTask>();
        services.AddSingleton<AgentDownloads>();
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
