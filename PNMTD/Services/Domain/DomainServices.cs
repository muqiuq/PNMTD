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
        return services;
    }
}
