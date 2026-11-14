using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;

namespace PNMTD.Zabbix;

public static class ZabbixSetup
{
    /// <summary>Zabbix__Url and Zabbix__ApiToken set (docs/audit-redmine-zabbix-plan.md §7.1).</summary>
    public static bool Configured(IConfiguration c) => new ZabbixOptions { Url = c["Zabbix:Url"], ApiToken = c["Zabbix:ApiToken"] }.Enabled;

    public static IServiceCollection AddZabbix(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ZabbixOptions>(configuration.GetSection(ZabbixOptions.Section));
        services.AddHttpClient(ZabbixClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => ZabbixClient.Handler(sp.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().CurrentValue.CertificateSha256));
        services.AddSingleton<ZabbixClient>();
        services.AddSingleton<ZabbixGroups>();
        services.AddSingleton<ZabbixSnapshot>();
        services.AddSingleton<ZabbixHealth>();
        services.AddSingleton<ZabbixPoller>();
        services.AddSingleton<ZabbixHostSync>();
        services.AddSingleton<ZabbixSensors>();
        services.AddScoped<ZabbixService>();
        services.AddScoped<ZabbixSensorService>();
        return services;
    }

    /// <summary>The mirror's hosts gone for longer than a week, unless a sensor is attached to them (it shows 491 with the name).</summary>
    internal static Task<int> DeleteGoneAsync(PnmtdDbContext db, DateTime cutoff, CancellationToken ct) =>
        db.ZabbixHosts.Where(h => h.GoneAt != null && h.GoneAt < cutoff && !db.ZabbixSensors.Any(z => z.ZabbixHostId == h.HostId)).ExecuteDeleteAsync(ct);
}
