namespace PNMTD.Network;

public static class NetworkMapSetup
{
    /// <summary>
    /// The network map (docs/network-map-plan.md §9.3). The scanner is hosted like the MikroTik job runner (outside
    /// BackgroundTasks:Enabled, so the tests can run it); the schedule under BackgroundTasks:Enabled, in Program.
    /// </summary>
    public static IServiceCollection AddNetworkMap(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<NetworkMapOptions>(configuration.GetSection(NetworkMapOptions.Section));
        services.AddSingleton<NetworkAnalysis>();
        services.AddSingleton<NetworkScanner>();
        services.AddSingleton<NetworkScanSchedule>();
        services.AddScoped<NetworkMapService>();
        services.AddScoped<NetworkConnectionService>();
        services.AddScoped<NetworkPositionService>();
        services.AddScoped<NetworkScanService>();
        services.AddScoped<NetworkSkipRuleService>();
        return services;
    }
}
