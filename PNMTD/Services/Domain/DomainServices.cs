namespace PNMTD.Services.Domain;

/// <summary>
/// The domain services (docs/webui-migration-plan.md §3.7): the logic behind /api/v1, one per
/// resource, scoped to the request's database context.
/// </summary>
public static class DomainServicesSetup
{
    public static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<PublicUrls>();
        services.AddScoped<MonitoringQueries>();
        services.AddScoped<MonitoringReader>();
        services.AddScoped<HostService>();
        services.AddScoped<SensorService>();
        services.AddScoped<EventService>();
        services.AddScoped<DashboardService>();
        services.AddSingleton<DashboardCache>();
        services.AddScoped<NotificationRuleService>();
        services.AddScoped<DnsZoneService>();
        services.AddScoped<MailService>();
        services.AddScoped<KeyValueService>();
        services.AddScoped<SystemService>();
        services.AddScoped<SearchService>();
        services.AddScoped<DebugService>();
        services.AddScoped<UplinkService>();
        services.AddScoped<NotificationManagerService>();
        services.AddScoped<AiService>();
        services.AddScoped<AiAnalysisService>();
        services.AddScoped<DecisionService>();
        // UDP nodes (docs/udp-iot-plan.md §5).
        services.AddScoped<UdpEndpoint>();
        services.AddScoped<UdpNodeService>();
        services.AddScoped<UdpCommandService>();
        services.AddScoped<UdpOtaService>();
        services.AddScoped<UdpProtocolDocs>();
        // MikroTik routers (docs/mikrotik-plan.md §10).
        services.AddScoped<MikrotikRouterService>();
        services.AddScoped<MikrotikBackupService>();
        services.AddScoped<MikrotikBackupSettingsService>();
        services.AddScoped<MikrotikJobService>();
        services.AddScoped<MikrotikCommandService>();
        services.AddScoped<WireGuardService>();
        services.AddScoped<WireGuardTunnelService>();
        // Servers with an agent, Windows and Linux (docs/windows-sensor-plan.md §9.2; docs/linux-sensor-plan.md §10).
        services.AddScoped<AgentServerService>();
        services.AddScoped<DiagnosticsSetService>();
        services.AddScoped<AgentScriptService>();
        return services;
    }
}
