namespace PNMTD.Advanced;

public static class AdvancedSetup
{
    /// <summary>Advanced sensors (docs/advanced-sensor-plan.md §7): the options, the evaluator, the rate limit, the service.</summary>
    public static IServiceCollection AddAdvanced(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AdvancedOptions>(configuration.GetSection(AdvancedOptions.Section));
        services.AddSingleton<AdvancedEvaluator>();
        services.AddSingleton<ReportGate>();
        services.AddSingleton<AdvancedTestGate>();
        services.AddScoped<AdvancedSensorService>();
        // gzip and friends on /report only (§4.2): the frozen paths stay untouched.
        services.AddRequestDecompression();
        return services;
    }

    /// <summary>Request decompression for the reports' path only.</summary>
    public static IApplicationBuilder UseReportDecompression(this IApplicationBuilder app) =>
        app.UseWhen(c => ReportPaths.IsReportPath(c.Request.Path), b => b.UseRequestDecompression());

    /// <summary><c>POST /report</c> (the token in the header) and <c>POST /report/{token}</c> (§4.1), anonymous.</summary>
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(ReportPaths.Path, ReportEndpoint.HeaderAsync).AllowAnonymous().ExcludeFromDescription().DisableAntiforgery();
        app.MapPost(ReportPaths.Path + "/{token}", ReportEndpoint.PathAsync).AllowAnonymous().ExcludeFromDescription().DisableAntiforgery();
        return app;
    }
}
