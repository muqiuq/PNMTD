using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Zabbix;

/// <summary>
/// Every Zabbix__HostSyncMinutes (docs/audit-redmine-zabbix-plan.md §7.3): Zabbix's hosts (of Zabbix__HostGroups, or all the
/// token sees) into <c>zabbix_hosts</c>: names, status, maintenance, groups, interfaces and their availability. A host
/// Zabbix no longer returns is marked gone, and removed a week later unless a sensor is attached to it.
/// </summary>
public sealed class ZabbixHostSync(ZabbixClient client, ZabbixGroups groups, ZabbixHealth health, ZabbixSensors sensors,
    IServiceScopeFactory scopes, IOptionsMonitor<ZabbixOptions> options, ILogger<ZabbixHostSync> logger) : BackgroundService
{
    public static readonly TimeSpan KeepGone = TimeSpan.FromDays(7);

    private readonly SemaphoreSlim running = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.CurrentValue.Enabled)
            {
                await RunAsync(stoppingToken);
            }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.HostSyncMinutes)), stoppingToken);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await running.WaitAsync(ct);
        try
        {
            await SyncAsync(ct);
        }
        catch (ZabbixException e) when (e.Failure != ZabbixFailure.NotConfigured)
        {
            await health.FailedAsync(e.Message, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ZabbixException)
        {
            logger.LogWarning(e, "Zabbix's host sync failed");
        }
        finally
        {
            running.Release();
        }
    }

    /// <summary>The mirror's first fill, for a page opened before the sync ran (and for the tests).</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            return;
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();
        if (!db.KeyValueKeyExists(KeyValueKeyEnums.LAST_ZABBIX_HOST_SYNC))
        {
            await RunAsync(ct);
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        var version = await client.VersionAsync(ct);
        var scope = await groups.ResolveAsync(true, ct);
        List<ZxHost> hosts = [];
        if (scope.Ids is not { Count: 0 })
        {
            var p = new Dictionary<string, object?>
            {
                ["output"] = new[] { "hostid", "host", "name", "status", "maintenance_status" },
                [version >= ZabbixVersion.HostGroupsFrom ? "selectHostGroups" : "selectGroups"] = new[] { "groupid", "name" },
                ["selectInterfaces"] = new[] { "interfaceid", "type", "main", "useip", "ip", "dns", "port", "available", "error" },
            };
            if (scope.Ids is { } ids)
            {
                p["groupids"] = ids;
            }
            hosts = await client.CallAsync<List<ZxHost>>("host.get", p, ct);
        }
        await using var s = scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<PnmtdDbContext>();
        var now = Clock.UtcNow;
        var known = await db.ZabbixHosts.ToDictionaryAsync(h => h.HostId, ct);
        var seen = new HashSet<long>();
        foreach (var h in hosts)
        {
            var id = ZabbixPoller.Int(h.Hostid);
            if (id <= 0 || !seen.Add(id))
            {
                continue;
            }
            if (!known.TryGetValue(id, out var row))
            {
                row = new ZabbixHostEntity { HostId = id };
                db.ZabbixHosts.Add(row);
            }
            Apply(row, h, now);
        }
        foreach (var row in known.Values.Where(r => !seen.Contains(r.HostId) && r.GoneAt is null))
        {
            row.GoneAt = now;
        }
        await db.SaveChangesAsync(ct);
        var cutoff = now - KeepGone;
        await ZabbixSetup.DeleteGoneAsync(db, cutoff, ct);
        db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_ZABBIX_HOST_SYNC);
        // A host gone or disabled: its sensor's 491 now, not at the next poll.
        await sensors.EvaluateAsync(null, ct);
        if (scope.NotFound.Count > 0)
        {
            await health.FailedAsync($"Not found in Zabbix: {string.Join(", ", scope.NotFound)} (Zabbix__HostGroups).", ct);
            return;
        }
        await health.SucceededAsync(ct);
    }

    internal static void Apply(ZabbixHostEntity row, ZxHost h, DateTime now)
    {
        row.TechnicalName = Cut(h.Host);
        row.VisibleName = Cut(string.IsNullOrWhiteSpace(h.Name) ? h.Host : h.Name);
        row.Monitored = h.Status == "0";
        row.InMaintenance = h.MaintenanceStatus == "1";
        row.Groups = h.AllGroups.Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var interfaces = Interfaces(h.Interfaces ?? []);
        row.Interfaces = JsonSerializer.Serialize(interfaces, ZabbixClient.Json);
        row.Addresses = (h.Interfaces ?? []).SelectMany(i => new[] { i.Ip, i.Dns }).Where(a => !string.IsNullOrWhiteSpace(a) && a != "0.0.0.0")
            .Select(a => a.Trim().ToLowerInvariant()).Distinct().ToList();
        row.Availability = Worst(interfaces);
        row.SyncedAt = now;
        row.GoneAt = null;
    }

    public static IReadOnlyList<ZabbixInterfaceItem> Interfaces(IEnumerable<ZxInterface> interfaces) => interfaces
        .OrderByDescending(i => i.Main == "1").ThenBy(i => i.Type)
        .Select(i => new ZabbixInterfaceItem(TypeName(i.Type), i.Useip == "1" || string.IsNullOrWhiteSpace(i.Dns) ? i.Ip : i.Dns, i.Port,
            i.Available switch { "1" => ZabbixAvailability.Available, "2" => ZabbixAvailability.Unavailable, _ => ZabbixAvailability.Unknown },
            string.IsNullOrWhiteSpace(i.Error) ? null : i.Error, i.Main == "1"))
        .ToList();

    public static ZabbixAvailability Worst(IReadOnlyList<ZabbixInterfaceItem> interfaces) =>
        interfaces.Any(i => i.Availability == ZabbixAvailability.Unavailable) ? ZabbixAvailability.Unavailable
        : interfaces.Any(i => i.Availability == ZabbixAvailability.Available) ? ZabbixAvailability.Available
        : ZabbixAvailability.Unknown;

    private static string TypeName(string type) => type switch { "1" => "Agent", "2" => "SNMP", "3" => "IPMI", "4" => "JMX", _ => "Other" };

    private static string Cut(string text) => text.Length > 255 ? text[..255] : text;
}
