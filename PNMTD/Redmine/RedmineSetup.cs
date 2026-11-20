using Microsoft.Extensions.Options;

namespace PNMTD.Redmine;

public static class RedmineSetup
{
    /// <summary>Redmine__Url, Redmine__ApiKey and Redmine__Projects set (docs/audit-redmine-zabbix-plan.md §6.1).</summary>
    public static bool Configured(IConfiguration c) =>
        new RedmineOptions { Url = c["Redmine:Url"], ApiKey = c["Redmine:ApiKey"], Projects = c["Redmine:Projects"] }.Enabled;

    /// <summary>
    /// Redmine: the client, used by the worker only, the worker (the queue, its snapshots, its scheduler and the linked copy,
    /// docs/redmine-queue-outbound-plan.md §3), and the service the controllers and Cookie read through.
    /// </summary>
    public static IServiceCollection AddRedmine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RedmineOptions>(configuration.GetSection(RedmineOptions.Section));
        services.AddHttpClient(RedmineClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => RedmineClient.Handler(sp.GetRequiredService<IOptionsMonitor<RedmineOptions>>().CurrentValue.CertificateSha256));
        services.AddSingleton<RedmineClient>();
        services.AddSingleton<RedmineWorker>();
        services.AddScoped<RedmineService>();
        return services;
    }
}
