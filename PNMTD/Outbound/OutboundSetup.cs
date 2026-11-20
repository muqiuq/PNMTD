namespace PNMTD.Outbound;

/// <summary>The two outbound logs (docs/redmine-queue-outbound-plan.md §6): the log, its writer, and the queries for System health.</summary>
public static class OutboundSetup
{
    public static IServiceCollection AddOutbound(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutboundOptions>(configuration.GetSection(OutboundOptions.Section));
        services.AddSingleton<OutboundLog>();
        services.AddSingleton<OutboundLogWriter>();
        services.AddScoped<OutboundQueries>();
        return services;
    }
}
