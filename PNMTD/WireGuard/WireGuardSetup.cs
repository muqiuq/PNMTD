namespace PNMTD.WireGuard;

public static class WireGuardSetup
{
    /// <summary>The tunnel network's services (docs/mikrotik-plan.md §9.3), for the app, the CLI and the gateway.</summary>
    public static IServiceCollection AddWireGuard(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WireGuardOptions>(configuration.GetSection(WireGuardOptions.Section));
        services.AddSingleton<WireGuardNetwork>();
        return services;
    }
}
