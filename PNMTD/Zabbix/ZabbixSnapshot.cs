using System.Globalization;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Enums;
using PNMTD.Services.Events;

namespace PNMTD.Zabbix;

/// <summary>An open problem as PNMT holds it (docs/audit-redmine-zabbix-plan.md §7.3): with the hosts its trigger is on.</summary>
public sealed record ZabbixProblem(string EventId, string TriggerId, string Name, ZabbixSeverity Severity, DateTime Since, bool Acknowledged,
    int Updates, bool Suppressed, string? OperationalData, IReadOnlyList<ZxTag> Tags, IReadOnlyList<ZxTriggerHost> Hosts);

/// <summary>
/// Zabbix's open problems in memory (§7.3), replaced by every poll: the summary, the problems list and the Zabbix sensors
/// read the same one, whatever the page views. A failed poll keeps the problems and records the error.
/// </summary>
public sealed class ZabbixSnapshot
{
    public sealed record State(IReadOnlyList<ZabbixProblem> Problems, DateTime? AsOf, string? Error, DateTime? ErrorAt);

    private volatile State state = new([], null, null, null);

    public State Current => state;

    public void Replace(IReadOnlyList<ZabbixProblem> problems, DateTime asOf) => state = new State(problems, asOf, null, null);

    public void Fail(string error) => state = state with { Error = error, ErrorAt = Clock.UtcNow };

    /// <summary>For the tests: as after a restart.</summary>
    public void Clear() => state = new State([], null, null, null);
}

/// <summary>
/// Zabbix__HostGroups resolved to ids (§7.1), refreshed with every host sync: <c>null</c> when every group the token sees
/// counts; the names Zabbix doesn't know are reported.
/// </summary>
public sealed class ZabbixGroups(ZabbixClient client, IOptionsMonitor<ZabbixOptions> options)
{
    public sealed record Resolved(IReadOnlyList<string>? Ids, IReadOnlyList<string> NotFound);

    private Resolved? resolved;
    private string? resolvedFor;

    public async Task<Resolved> ResolveAsync(bool refresh, CancellationToken ct)
    {
        var names = options.CurrentValue.HostGroupList;
        var key = string.Join("\n", names);
        if (!refresh && resolved is { } known && resolvedFor == key)
        {
            return known;
        }
        Resolved result;
        if (names.Count == 0)
        {
            result = new Resolved(null, []);
        }
        else
        {
            var groups = await client.CallAsync<List<ZxGroup>>("hostgroup.get",
                new Dictionary<string, object?> { ["output"] = new[] { "groupid", "name" }, ["filter"] = new Dictionary<string, object?> { ["name"] = names } }, ct);
            var found = groups.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            result = new Resolved(groups.Select(g => g.Groupid).ToList(), names.Where(n => !found.Contains(n)).ToList());
        }
        resolved = result;
        resolvedFor = key;
        return result;
    }
}

/// <summary>
/// The Zabbix system sensor (§8.5), for the poller and the host sync together: the second failure in a row fails it (430)
/// once; the next success clears it (220).
/// </summary>
public sealed class ZabbixHealth(SystemSensors systemSensors, ILogger<ZabbixHealth> logger)
{
    private int failures;
    private bool? failing;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task FailedAsync(string message, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            failures++;
            logger.LogWarning("Zabbix: {Message}", message);
            if (failures == 2)
            {
                await systemSensors.WriteAsync(SystemSensorKeys.Zabbix, PNMTStatusCodes.ZABBIX_FAILING, message, ct);
                failing = true;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SucceededAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            failures = 0;
            // After a restart the sensor may still be failing from before.
            failing ??= (await systemSensors.LastAsync(SystemSensorKeys.Zabbix, ct))?.Code == PNMTStatusCodes.ZABBIX_FAILING;
            if (failing == true)
            {
                await systemSensors.WriteAsync(SystemSensorKeys.Zabbix, PNMTStatusCodes.SYSTEM_OK, "Zabbix answers again.", ct);
                failing = false;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
/// Every Zabbix__PollSeconds (§7.3): the open problems in one <c>problem.get</c>, the hosts of triggers not seen before in
/// one <c>trigger.get</c> (Zabbix's problems don't name their host), into the snapshot; then the ZABBIX sensors' state. A
/// failure keeps the snapshot and writes no sensor event (§7.7); it goes to the system sensor.
/// </summary>
public sealed class ZabbixPoller(ZabbixClient client, ZabbixGroups groups, ZabbixSnapshot snapshot, ZabbixHealth health,
    ZabbixSensors sensors, IServiceScopeFactory scopes, IOptionsMonitor<ZabbixOptions> options, ILogger<ZabbixPoller> logger) : BackgroundService
{
    public const int MaxProblems = 10_000;

    private readonly Dictionary<string, IReadOnlyList<ZxTriggerHost>> triggerHosts = [];
    private readonly SemaphoreSlim running = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.CurrentValue.Enabled)
            {
                await RunAsync(stoppingToken);
            }
            await Task.Delay(options.CurrentValue.Poll, stoppingToken);
        }
    }

    /// <summary>A poll, unless one runs: then its result.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await running.WaitAsync(ct);
        try
        {
            await PollAsync(ct);
        }
        finally
        {
            running.Release();
        }
    }

    /// <summary>The first poll, for a page opened before the poller ran (and for the tests, which run no tasks).</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        if (snapshot.Current.AsOf is null && snapshot.Current.Error is null && options.CurrentValue.Enabled)
        {
            await RunAsync(ct);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            var scope = await groups.ResolveAsync(false, ct);
            List<ZxProblem> problems;
            if (scope.Ids is { Count: 0 })
            {
                problems = [];
            }
            else
            {
                var p = new Dictionary<string, object?>
                {
                    ["output"] = new[] { "eventid", "objectid", "name", "severity", "clock", "acknowledged", "suppressed", "opdata" },
                    ["selectTags"] = new[] { "tag", "value" },
                    ["selectAcknowledges"] = new[] { "acknowledgeid" },
                    ["recent"] = false,
                    ["sortfield"] = new[] { "eventid" },
                    ["sortorder"] = "DESC",
                    ["limit"] = MaxProblems,
                };
                if (scope.Ids is { } ids)
                {
                    p["groupids"] = ids;
                }
                problems = await client.CallAsync<List<ZxProblem>>("problem.get", p, ct);
            }
            var unknown = problems.Select(x => x.Objectid).Distinct().Where(id => !triggerHosts.ContainsKey(id)).ToList();
            foreach (var chunk in unknown.Chunk(500))
            {
                var triggers = await client.CallAsync<List<ZxTrigger>>("trigger.get", new Dictionary<string, object?>
                {
                    ["triggerids"] = chunk,
                    ["output"] = new[] { "triggerid" },
                    ["selectHosts"] = new[] { "hostid", "host", "name" },
                }, ct);
                foreach (var t in triggers)
                {
                    triggerHosts[t.Triggerid] = t.Hosts ?? [];
                }
                // A trigger the token can't read: no host, asked again next time.
            }
            var current = problems.Select(x => x.Objectid).ToHashSet();
            foreach (var gone in triggerHosts.Keys.Where(k => !current.Contains(k)).ToList())
            {
                triggerHosts.Remove(gone);
            }
            snapshot.Replace(problems.Select(Map).ToList(), Clock.UtcNow);
            await using (var s = scopes.CreateAsyncScope())
            {
                s.ServiceProvider.GetRequiredService<PnmtdDbContext>().UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_ZABBIX_POLL);
            }
            await health.SucceededAsync(ct);
            await sensors.EvaluateAsync(null, ct);
        }
        catch (ZabbixException e) when (e.Failure != ZabbixFailure.NotConfigured)
        {
            snapshot.Fail(e.Message);
            await health.FailedAsync(e.Message, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ZabbixException)
        {
            logger.LogWarning(e, "Zabbix's poll failed");
            snapshot.Fail("PNMT couldn't read Zabbix's problems; its log says why.");
        }
    }

    private ZabbixProblem Map(ZxProblem p) => new(p.Eventid, p.Objectid, p.Name, Severity(p.Severity),
        DateTime.UnixEpoch.AddSeconds(Int(p.Clock)), p.Acknowledged == "1", p.Acknowledges?.Count ?? 0, p.Suppressed == "1",
        string.IsNullOrWhiteSpace(p.Opdata) ? null : p.Opdata, p.Tags ?? [], triggerHosts.GetValueOrDefault(p.Objectid) ?? []);

    internal static ZabbixSeverity Severity(string? value) => Int(value) is >= 0 and <= 5 and var n ? (ZabbixSeverity)(int)n : ZabbixSeverity.NotClassified;

    internal static long Int(string? value) => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
