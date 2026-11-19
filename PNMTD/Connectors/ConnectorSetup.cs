namespace PNMTD.Connectors;

public static class ConnectorSetup
{
    /// <summary>
    /// 3rd Party Connectors (docs/connectors-plan.md): the options, the key's protector, the guard, the vault, the signals, the queue's
    /// nudge, the review, the worker, the endpoint's gateway and the admin service.
    /// </summary>
    public static IServiceCollection AddConnectors(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ConnectorOptions>(configuration.GetSection(ConnectorOptions.Section));
        services.AddSingleton<ConnectorKeyProtector>();
        services.AddSingleton<ConnectorGuard>();
        services.AddSingleton<ConnectorSecretVault>();
        services.AddSingleton<ConnectorCallSignals>();
        services.AddSingleton<ConnectorQueue>();
        services.AddSingleton<ConnectorPollGate>();
        services.AddSingleton<ConnectorAlarms>();
        services.AddSingleton<ConnectorReview>();
        services.AddSingleton<ConnectorWorker>();
        services.AddSingleton<ConnectorGateway>();
        services.AddScoped<ConnectorService>();
        return services;
    }

    /// <summary>The four anonymous endpoints of <c>/connector/v1</c> (§4.1).</summary>
    public static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder app) => ConnectorGateway.Map(app);

    /// <summary>The worker exists from the start, so a nudge before its first timer pass reaches it.</summary>
    public static void StartConnectorWorker(IServiceProvider services) => services.GetRequiredService<ConnectorWorker>();
}
