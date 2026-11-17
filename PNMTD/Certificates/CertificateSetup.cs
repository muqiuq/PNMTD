namespace PNMTD.Certificates;

public static class CertificateSetup
{
    /// <summary>SSL certificate sensors (docs/ssl-certificate-plan.md §5): the options, the checks, the timer, the services.</summary>
    public static IServiceCollection AddCertificates(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CertificateOptions>(configuration.GetSection(CertificateOptions.Section));
        services.AddSingleton<CertificateCheckService>();
        services.AddSingleton<CertificateChecker>();
        services.AddScoped<CertificateSensorService>();
        services.AddScoped<CertificateAuthorityService>();
        return services;
    }
}
