namespace PNMTD.Udp;

public static class UdpSetup
{
    /// <summary>The UDP nodes' services (docs/udp-iot-plan.md §4.4). The listener itself is a hosted service, added in Program.</summary>
    public static IServiceCollection AddUdpNodes(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<UdpOptions>(configuration.GetSection(UdpOptions.Section));
        services.AddSingleton<UdpNodeCache>();
        services.AddSingleton<UdpStats>();
        services.AddSingleton<UdpRateLimiter>();
        services.AddSingleton<UdpFirmwareCache>();
        services.AddSingleton<UdpPacketProcessor>();
        services.AddSingleton<UdpListenerState>();
        services.AddSingleton<UdpMaintenanceTask>();
        return services;
    }
}
