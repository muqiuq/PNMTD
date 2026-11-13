namespace PNMTD.Mikrotik;

public static class MikrotikSetup
{
    /// <summary>
    /// MikroTik routers (docs/mikrotik-plan.md §9.3). The poller and the version check are hosted services,
    /// added in Program under BackgroundTasks:Enabled; everything else is here, so the API and the CLI can
    /// poll a router without them.
    /// </summary>
    public static IServiceCollection AddMikrotik(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MikrotikOptions>(configuration.GetSection(MikrotikOptions.Section));
        services.AddSingleton<IRouterConnector, SshRouterConnector>();
        services.AddSingleton<MikrotikKeyStore>();
        services.AddSingleton<RouterPollService>();
        services.AddSingleton<RouterPoller>();
        services.AddSingleton<VersionCatalog>();
        return services;
    }
}
