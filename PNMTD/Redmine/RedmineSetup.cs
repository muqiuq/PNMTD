using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Enums;
using PNMTD.Services.Events;

namespace PNMTD.Redmine;

public static class RedmineSetup
{
    /// <summary>Redmine__Url, Redmine__ApiKey and Redmine__Projects set (docs/audit-redmine-zabbix-plan.md §6.1).</summary>
    public static bool Configured(IConfiguration c) =>
        new RedmineOptions { Url = c["Redmine:Url"], ApiKey = c["Redmine:ApiKey"], Projects = c["Redmine:Projects"] }.Enabled;

    public static IServiceCollection AddRedmine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RedmineOptions>(configuration.GetSection(RedmineOptions.Section));
        services.AddHttpClient(RedmineClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp => RedmineClient.Handler(sp.GetRequiredService<IOptionsMonitor<RedmineOptions>>().CurrentValue.CertificateSha256));
        services.AddSingleton<RedmineClient>();
        services.AddScoped<RedmineService>();
        services.AddSingleton<RedmineSync>();
        return services;
    }
}

/// <summary>
/// Every Redmine:SyncMinutes (§6.7): PNMT's copy of linked tickets refreshed, a hundred a call; tickets Redmine no longer
/// returns marked missing; copies without links deleted. Two failures in a row fail the Redmine system sensor (429); the
/// next success clears it (220).
/// </summary>
public sealed class RedmineSync(IServiceScopeFactory scopes, IOptionsMonitor<RedmineOptions> options, SystemSensors systemSensors,
    ILogger<RedmineSync> logger) : BackgroundService
{
    private int failures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.CurrentValue.Enabled)
            {
                await RunAsync(stoppingToken);
            }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.SyncMinutes)), stoppingToken);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();
            var redmine = scope.ServiceProvider.GetRequiredService<RedmineService>();
            var client = scope.ServiceProvider.GetRequiredService<RedmineClient>();
            redmine.ForgetLookups();
            var lookups = await redmine.LookupsAsync(ct);
            var linked = await db.RedmineLinks.AsNoTracking().Select(l => l.IssueId).Distinct().ToListAsync(ct);
            var seen = new HashSet<int>();
            foreach (var batch in linked.Chunk(100))
            {
                var page = await client.GetAsync<RmIssues>($"/issues.json?issue_id={string.Join(",", batch)}&status_id=*&limit=100", ct);
                var allowed = page.Issues.Where(i => lookups.Allowed.Contains(i.Project.Id)).ToList();
                await redmine.UpsertCopyAsync(allowed, ct);
                seen.UnionWith(allowed.Select(i => i.Id));
            }
            var gone = linked.Where(id => !seen.Contains(id)).ToList();
            await db.RedmineIssues.Where(i => gone.Contains(i.IssueId)).ExecuteUpdateAsync(u => u.SetProperty(i => i.Missing, true), ct);
            await db.RedmineIssues.Where(i => !db.RedmineLinks.Any(l => l.IssueId == i.IssueId)).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_REDMINE_SYNC);
            if (lookups.NotFound.Count > 0)
            {
                await FailAsync($"Not found in Redmine: {string.Join(", ", lookups.NotFound)} (Redmine__Projects).", ct);
                return;
            }
            if (failures > 0 || (await systemSensors.LastAsync(SystemSensorKeys.Redmine, ct))?.Code == PNMTStatusCodes.REDMINE_FAILING)
            {
                await systemSensors.WriteAsync(SystemSensorKeys.Redmine, PNMTStatusCodes.SYSTEM_OK, "Redmine answers again.", ct);
            }
            failures = 0;
        }
        catch (RedmineException e)
        {
            await FailAsync(e.Message, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Redmine's sync failed");
        }
    }

    private async Task FailAsync(string message, CancellationToken ct)
    {
        failures++;
        logger.LogWarning("Redmine: {Message}", message);
        if (failures == 2)
        {
            await systemSensors.WriteAsync(SystemSensorKeys.Redmine, PNMTStatusCodes.REDMINE_FAILING, message, ct);
        }
    }
}
