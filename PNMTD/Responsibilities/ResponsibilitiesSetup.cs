namespace PNMTD.Responsibilities;

public static class ResponsibilitiesSetup
{
    /// <summary>Responsibilities (docs/responsibilities-plan.md §10.6): their reads and their changes, per request.</summary>
    public static IServiceCollection AddResponsibilities(this IServiceCollection services)
    {
        services.AddScoped<ResponsibilityQueries>();
        services.AddScoped<ResponsibilityService>();
        services.AddScoped<ResponsibilityMapService>();
        return services;
    }
}
