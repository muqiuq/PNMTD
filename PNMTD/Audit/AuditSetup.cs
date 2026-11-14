using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Enums;

namespace PNMTD.Audit;

/// <summary>How long the audit log keeps entries (docs/audit-redmine-zabbix-plan.md §3.8): Audit__KeepDays, Audit__MaxEntries.</summary>
public sealed class AuditOptions
{
    public const string Section = "Audit";

    /// <summary>A year and a bit, for a yearly review.</summary>
    public int KeepDays { get; set; } = 400;

    /// <summary>A ceiling for busy installations.</summary>
    public int MaxEntries { get; set; } = 500_000;
}

public static class AuditSetup
{
    public static IServiceCollection AddAudit(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuditOptions>(configuration.GetSection(AuditOptions.Section));
        services.AddScoped<AuditFilter>();
        services.Configure<MvcOptions>(o => o.Filters.AddService<AuditFilter>(int.MinValue));
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditDeniedHandler>();
        services.AddSingleton<AuditRetentionTask>();
        return services;
    }
}

/// <summary>
/// Once a day (§3.8): entries older than Audit:KeepDays go, then the oldest beyond Audit:MaxEntries. Nothing else deletes
/// entries. In task health as "Audit retention".
/// </summary>
public sealed class AuditRetentionTask(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<AuditOptions> options, ILogger<AuditRetentionTask> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "The audit log's retention failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var before = Clock.UtcNow.AddDays(-Math.Max(1, settings.KeepDays));
        var removed = await db.AuditLog.Where(l => l.CreatedAt < before).ExecuteDeleteAsync(ct);
        var max = Math.Max(1000, settings.MaxEntries);
        removed += await db.Database.ExecuteSqlAsync($"""
            DELETE FROM audit_log WHERE "CreatedAt" <= (SELECT "CreatedAt" FROM audit_log ORDER BY "CreatedAt" DESC OFFSET {max} LIMIT 1)
            """, ct);
        db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_AUDIT_RETENTION);
        if (removed > 0)
        {
            logger.LogInformation("Audit retention removed {Count} entries", removed);
        }
        return removed;
    }
}
