using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;

namespace PNMTD.Zabbix;

/// <summary>
/// Zabbix for PNMT's pages (docs/audit-redmine-zabbix-plan.md §7.4–7.6): the summary and the problems from the snapshot,
/// the hosts from the mirror, a host's page and latest data from Zabbix. Everything goes through here, never through the
/// client, so Zabbix__HostGroups applies to every caller (§8.7).
/// </summary>
public sealed class ZabbixService(PnmtdDbContext db, ZabbixClient client, ZabbixGroups groups, ZabbixSnapshot snapshot, ZabbixPoller poller,
    ZabbixHostSync hostSync, IOptionsMonitor<ZabbixOptions> options)
{
    public const string NoGroup = "(no group)";

    public const string NoValue = "(none)";

    private ZabbixOptions O => options.CurrentValue;

    public ZabbixLinks Links => new(O.BrowserUrl, client.KnownVersion);

    private void EnsureConfigured()
    {
        if (!O.Enabled)
        {
            throw new ZabbixException(ZabbixFailure.NotConfigured, "Zabbix is not configured (Zabbix__Url, Zabbix__ApiToken).");
        }
    }

    // --- status ----------------------------------------------------------------------------------------------------------

    public async Task<ZabbixStatus> StatusAsync(CancellationToken ct)
    {
        if (!O.Enabled)
        {
            return new ZabbixStatus(false, null, null, [], [], null, null, null);
        }
        var state = snapshot.Current;
        string? version = client.KnownVersion?.ToString();
        string? error = state.Error;
        IReadOnlyList<string> notFound = [];
        try
        {
            version = (await client.VersionAsync(ct)).ToString();
            notFound = (await groups.ResolveAsync(false, ct)).NotFound;
        }
        catch (ZabbixException e)
        {
            error = e.Message;
        }
        var synced = await db.ZabbixHosts.AsNoTracking().MaxAsync(h => (DateTime?)h.SyncedAt, ct);
        return new ZabbixStatus(true, version, O.BrowserUrl, O.HostGroupList, notFound, state.AsOf, synced is { } s ? Clock.AsUtc(s) : null,
            error ?? (notFound.Count > 0 ? $"Not found in Zabbix: {string.Join(", ", notFound)} (Zabbix__HostGroups)." : null));
    }

    // --- the summary -----------------------------------------------------------------------------------------------------

    /// <summary>The overview (§7.4): by severity, by category (host group, or a tag's values), the hosts.</summary>
    public async Task<ZabbixSummary> SummaryAsync(string? category, bool suppressed, CancellationToken ct)
    {
        EnsureConfigured();
        await poller.EnsureAsync(ct);
        await hostSync.EnsureAsync(ct);
        var state = snapshot.Current;
        var all = state.Problems;
        var shown = all.Where(p => suppressed || !p.Suppressed).ToList();
        var severities = Enum.GetValues<ZabbixSeverity>().OrderDescending()
            .Select(s => new ZabbixSeverityCount(s, shown.Count(p => p.Severity == s), shown.Count(p => p.Severity == s && !p.Acknowledged)))
            .ToList();
        var tagNames = all.SelectMany(p => p.Tags.Select(t => t.Tag)).Distinct(StringComparer.Ordinal)
            .OrderBy(t => t switch { "class" => 0, "component" => 1, "scope" => 2, _ => 3 }).ThenBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        var key = string.IsNullOrWhiteSpace(category) || category == "group" ? "group" : category.Trim();
        IEnumerable<(string Name, ZabbixProblem Problem)> keyed;
        if (key.StartsWith("tag:", StringComparison.Ordinal) && key.Length > 4)
        {
            var tag = key[4..];
            keyed = shown.SelectMany(p =>
            {
                var values = p.Tags.Where(t => t.Tag == tag).Select(t => string.IsNullOrEmpty(t.Value) ? tag : t.Value).Distinct().ToList();
                return (values.Count == 0 ? [NoValue] : values).Select(v => (v, p));
            });
        }
        else
        {
            key = "group";
            var hostGroups = await GroupsOfAsync(shown.SelectMany(p => p.Hosts.Select(h => ZabbixPoller.Int(h.Hostid))), ct);
            keyed = shown.SelectMany(p =>
            {
                var names = p.Hosts.SelectMany(h => hostGroups.GetValueOrDefault(ZabbixPoller.Int(h.Hostid)) ?? []).Distinct().ToList();
                return (names.Count == 0 ? [NoGroup] : names).Select(n => (n, p));
            });
        }
        var rows = keyed.GroupBy(x => x.Name)
            .Select(g =>
            {
                var counts = new int[6];
                foreach (var (_, p) in g)
                {
                    counts[(int)p.Severity]++;
                }
                return new ZabbixCategoryRow(g.Key, counts, g.Count());
            })
            .OrderByDescending(r => Worst(r.Counts)).ThenByDescending(r => r.Total).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groupNames = (await db.ZabbixHosts.AsNoTracking().Where(h => h.GoneAt == null).Select(h => h.Groups).ToListAsync(ct))
            .SelectMany(g => g).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new ZabbixSummary(state.AsOf, Stale(state), state.Error, key, tagNames, severities, rows, await HostCountsAsync(ct),
            all.Count(p => p.Suppressed), Links.Dashboard, groupNames);
    }

    private static int Worst(IReadOnlyList<int> counts)
    {
        for (var i = counts.Count - 1; i >= 0; i--)
        {
            if (counts[i] > 0)
            {
                return i;
            }
        }
        return -1;
    }

    private bool Stale(ZabbixSnapshot.State state) => state.AsOf is not { } at || Clock.UtcNow - at > 3 * O.Poll;

    private async Task<ZabbixHostCounts> HostCountsAsync(CancellationToken ct)
    {
        var hosts = db.ZabbixHosts.AsNoTracking().Where(h => h.GoneAt == null);
        return new ZabbixHostCounts(
            await hosts.CountAsync(ct),
            await hosts.CountAsync(h => h.Monitored, ct),
            await hosts.CountAsync(h => !h.Monitored, ct),
            await hosts.CountAsync(h => h.Monitored && h.Availability == ZabbixAvailability.Unavailable, ct),
            await hosts.CountAsync(h => h.InMaintenance, ct),
            await AttachedCountAsync(ct));
    }

    /// <summary>Zabbix hosts with a sensor in PNMT.</summary>
    private Task<int> AttachedCountAsync(CancellationToken ct) =>
        db.ZabbixHosts.AsNoTracking().CountAsync(h => h.GoneAt == null && db.ZabbixSensors.Any(z => z.ZabbixHostId == h.HostId), ct);

    private async Task<Dictionary<long, List<string>>> GroupsOfAsync(IEnumerable<long> hostIds, CancellationToken ct)
    {
        var ids = hostIds.Distinct().ToList();
        return await db.ZabbixHosts.AsNoTracking().Where(h => ids.Contains(h.HostId)).ToDictionaryAsync(h => h.HostId, h => h.Groups, ct);
    }

    // --- problems --------------------------------------------------------------------------------------------------------

    public async Task<Paged<ZabbixProblemItem>> ProblemsAsync(ZabbixProblemQuery query, CancellationToken ct)
    {
        EnsureConfigured();
        await poller.EnsureAsync(ct);
        await hostSync.EnsureAsync(ct);
        var problems = snapshot.Current.Problems.Where(p => query.Suppressed || !p.Suppressed);
        if (query.MinSeverity is { } min)
        {
            problems = problems.Where(p => p.Severity >= min);
        }
        if (query.Severity is { } severity)
        {
            problems = problems.Where(p => p.Severity == severity);
        }
        if (query.Acknowledged is { } ack)
        {
            problems = problems.Where(p => p.Acknowledged == ack);
        }
        if (query.OlderThanMinutes is > 0 and var minutes)
        {
            var before = Clock.UtcNow.AddMinutes(-minutes);
            problems = problems.Where(p => p.Since <= before);
        }
        if (query.HostId is { } hostId)
        {
            var id = hostId.ToString(CultureInfo.InvariantCulture);
            problems = problems.Where(p => p.Hosts.Any(h => h.Hostid == id));
        }
        if (!string.IsNullOrWhiteSpace(query.Host))
        {
            var text = query.Host.Trim();
            problems = problems.Where(p => p.Hosts.Any(h => h.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || h.Host.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }
        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var (tag, value) = query.Tag.Split(':', 2) is [var t, var v] ? (t.Trim(), v.Trim()) : (query.Tag.Trim(), (string?)null);
            problems = problems.Where(p => p.Tags.Any(x => x.Tag == tag && (value is null || x.Value == value)));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var text = query.Search.Trim();
            problems = problems.Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (p.OperationalData?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)
                || p.Hosts.Any(h => h.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || h.Host.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }
        var list = problems.ToList();
        if (!string.IsNullOrWhiteSpace(query.Group))
        {
            var group = query.Group.Trim();
            var hostGroups = await GroupsOfAsync(list.SelectMany(p => p.Hosts.Select(h => ZabbixPoller.Int(h.Hostid))), ct);
            list = list.Where(p => group == NoGroup
                ? p.Hosts.All(h => hostGroups.GetValueOrDefault(ZabbixPoller.Int(h.Hostid)) is not { Count: > 0 })
                : p.Hosts.Any(h => hostGroups.GetValueOrDefault(ZabbixPoller.Int(h.Hostid))?.Contains(group, StringComparer.OrdinalIgnoreCase) == true)).ToList();
        }
        var size = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(1, query.Page);
        var ordered = list.OrderByDescending(p => p.Severity).ThenByDescending(p => p.Since).ToList();
        return new Paged<ZabbixProblemItem>(await ProblemItemsAsync(ordered.Skip((page - 1) * size).Take(size), ct), page, size, ordered.Count);
    }

    public async Task<IReadOnlyList<ZabbixProblemItem>> ProblemItemsAsync(IEnumerable<ZabbixProblem> problems, CancellationToken ct)
    {
        var list = problems.ToList();
        var attached = await AttachedAsync(list.SelectMany(p => p.Hosts.Select(h => ZabbixPoller.Int(h.Hostid))), ct);
        var links = Links;
        return list.Select(p => new ZabbixProblemItem(p.EventId, p.Name, p.OperationalData, p.Severity, p.Since, p.Acknowledged, p.Updates, p.Suppressed,
            p.Tags.Select(t => new ZabbixTagItem(t.Tag, t.Value)).ToList(),
            p.Hosts.Select(h => new ZabbixHostRef(ZabbixPoller.Int(h.Hostid), h.Name)).ToList(),
            p.Hosts.Select(h => attached.TryGetValue(ZabbixPoller.Int(h.Hostid), out var a) ? a.Host : null).FirstOrDefault(r => r is not null),
            p.Hosts.Select(h => attached.TryGetValue(ZabbixPoller.Int(h.Hostid), out var a) ? a.SensorId : (Guid?)null).FirstOrDefault(r => r is not null),
            links.Event(p.TriggerId, p.EventId))).ToList();
    }

    /// <summary>The PNMT hosts the Zabbix hosts are attached to, and the sensors.</summary>
    private async Task<Dictionary<long, (Ref Host, Guid SensorId)>> AttachedAsync(IEnumerable<long> hostIds, CancellationToken ct)
    {
        var ids = hostIds.Distinct().ToList();
        return await db.ZabbixSensors.AsNoTracking().Where(z => ids.Contains(z.ZabbixHostId))
            .Select(z => new { z.ZabbixHostId, z.SensorId, z.Sensor.ParentId, z.Sensor.Parent.Name })
            .ToDictionaryAsync(z => z.ZabbixHostId, z => (new Ref(z.ParentId, z.Name), z.SensorId), ct);
    }

    /// <summary>A row opened (§7.5): the trigger's description, and with Details the updates with their messages.</summary>
    public async Task<ZabbixProblemDetail> ProblemAsync(string eventId, bool details, CancellationToken ct)
    {
        EnsureConfigured();
        if (eventId.Length is 0 or > 20 || !eventId.All(char.IsAsciiDigit))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "zabbix_problem_not_found", "No such problem.");
        }
        var scope = await groups.ResolveAsync(false, ct);
        var p = new Dictionary<string, object?>
        {
            ["eventids"] = new[] { eventId },
            ["output"] = new[] { "eventid", "objectid" },
            ["selectAcknowledges"] = details ? "extend" : new[] { "acknowledgeid" },
        };
        if (scope.Ids is { } ids)
        {
            p["groupids"] = ids;
        }
        var found = scope.Ids is { Count: 0 } ? [] : await client.CallAsync<List<ZxProblem>>("problem.get", p, ct);
        if (found.Count == 0)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "zabbix_problem_not_found", "Zabbix has no such open problem, or not for PNMT's token.");
        }
        var problem = found[0];
        var trigger = (await client.CallAsync<List<ZxTrigger>>("trigger.get", new Dictionary<string, object?>
        {
            ["triggerids"] = new[] { problem.Objectid },
            ["output"] = new[] { "triggerid", "comments", "url" },
            ["expandComment"] = true,
        }, ct)).FirstOrDefault();
        var updates = details
            ? (problem.Acknowledges ?? []).OrderBy(a => ZabbixPoller.Int(a.Clock)).Select(a => new ZabbixUpdateItem(
                DateTime.UnixEpoch.AddSeconds(ZabbixPoller.Int(a.Clock)), $"Zabbix user {a.Userid}", Actions(ZabbixPoller.Int(a.Action)),
                string.IsNullOrWhiteSpace(a.Message) ? null : a.Message)).ToList()
            : null;
        return new ZabbixProblemDetail(problem.Eventid, string.IsNullOrWhiteSpace(trigger?.Comments) ? null : trigger.Comments,
            Web(trigger?.Url), updates);
    }

    /// <summary>A problem update's action, a bit mask in Zabbix.</summary>
    private static IReadOnlyList<string> Actions(long action)
    {
        var words = new List<string>();
        void Add(long bit, string word)
        {
            if ((action & bit) != 0)
            {
                words.Add(word);
            }
        }
        Add(1, "closed");
        Add(2, "acknowledged");
        Add(16, "unacknowledged");
        Add(8, "severity changed");
        Add(32, "suppressed");
        Add(64, "unsuppressed");
        Add(4, "message");
        return words;
    }

    /// <summary>A URL of Zabbix's for a link, only http(s).</summary>
    private static string? Web(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.ToString() : null;

    // --- hosts -----------------------------------------------------------------------------------------------------------

    public async Task<Paged<ZabbixHostItem>> HostsAsync(ZabbixHostQuery query, CancellationToken ct)
    {
        EnsureConfigured();
        await poller.EnsureAsync(ct);
        await hostSync.EnsureAsync(ct);
        var hosts = db.ZabbixHosts.AsNoTracking().Where(h => h.GoneAt == null);
        if (Text.Pattern(query.Search) is { } pattern)
        {
            hosts = hosts.Where(h => EF.Functions.ILike(h.VisibleName, pattern, @"\") || EF.Functions.ILike(h.TechnicalName, pattern, @"\")
                || h.Addresses.Any(a => EF.Functions.ILike(a, pattern, @"\")));
        }
        if (!string.IsNullOrWhiteSpace(query.Group))
        {
            var group = query.Group.Trim();
            hosts = hosts.Where(h => h.Groups.Contains(group));
        }
        hosts = query.Status switch
        {
            "monitored" => hosts.Where(h => h.Monitored),
            "disabled" => hosts.Where(h => !h.Monitored),
            "maintenance" => hosts.Where(h => h.InMaintenance),
            _ => hosts,
        };
        if (query.Availability is { } availability)
        {
            hosts = hosts.Where(h => h.Availability == availability);
        }
        hosts = FilterAttached(hosts, query.Attached);
        var size = Math.Clamp(query.PageSize, 1, PageQuery.MaxPageSize);
        var page = Math.Max(1, query.Page);
        var total = await hosts.CountAsync(ct);
        var rows = await hosts.OrderBy(h => h.VisibleName).ThenBy(h => h.HostId).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<ZabbixHostItem>(await HostItemsAsync(rows, ct), page, size, total);
    }

    private IQueryable<ZabbixHostEntity> FilterAttached(IQueryable<ZabbixHostEntity> hosts, bool? attached) => attached switch
    {
        true => hosts.Where(h => db.ZabbixSensors.Any(z => z.ZabbixHostId == h.HostId)),
        false => hosts.Where(h => !db.ZabbixSensors.Any(z => z.ZabbixHostId == h.HostId)),
        _ => hosts,
    };

    public async Task<IReadOnlyList<ZabbixHostItem>> HostItemsAsync(IReadOnlyList<ZabbixHostEntity> rows, CancellationToken ct)
    {
        var problems = ProblemsByHost();
        var attached = await AttachedAsync(rows.Select(r => r.HostId), ct);
        return rows.Select(r =>
        {
            var mine = problems.GetValueOrDefault(r.HostId);
            return new ZabbixHostItem(r.HostId, r.VisibleName, r.TechnicalName, r.Monitored, r.InMaintenance, r.Groups, r.Addresses,
                JsonSerializer.Deserialize<List<ZabbixInterfaceItem>>(r.Interfaces, ZabbixClient.Json) ?? [], r.Availability,
                mine?.Count ?? 0, mine?.Max(p => p.Severity), attached.TryGetValue(r.HostId, out var a) ? a.Host : null,
                attached.TryGetValue(r.HostId, out var b) ? b.SensorId : null, r.GoneAt is not null);
        }).ToList();
    }

    /// <summary>The open problems, not suppressed, by Zabbix host.</summary>
    private Dictionary<long, List<ZabbixProblem>> ProblemsByHost() => snapshot.Current.Problems.Where(p => !p.Suppressed)
        .SelectMany(p => p.Hosts.Select(h => (Id: ZabbixPoller.Int(h.Hostid), Problem: p)))
        .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.Select(x => x.Problem).ToList());

    /// <summary>A host's page (§7.6), asked of Zabbix; the mirror's data with the error when Zabbix doesn't answer.</summary>
    public async Task<ZabbixHostDetail> HostAsync(long hostId, bool details, CancellationToken ct)
    {
        EnsureConfigured();
        await poller.EnsureAsync(ct);
        var row = await db.ZabbixHosts.AsNoTracking().FirstOrDefaultAsync(h => h.HostId == hostId, ct);
        var id = hostId.ToString(CultureInfo.InvariantCulture);
        ZxHost? host;
        try
        {
            var version = await client.VersionAsync(ct);
            var scope = await groups.ResolveAsync(false, ct);
            var p = new Dictionary<string, object?>
            {
                ["hostids"] = new[] { id },
                ["output"] = new[] { "hostid", "host", "name", "status", "maintenance_status", "description" },
                [version >= ZabbixVersion.HostGroupsFrom ? "selectHostGroups" : "selectGroups"] = new[] { "groupid", "name" },
                ["selectInterfaces"] = new[] { "interfaceid", "type", "main", "useip", "ip", "dns", "port", "available", "error" },
                ["selectTags"] = new[] { "tag", "value" },
                ["selectParentTemplates"] = new[] { "templateid", "name" },
            };
            if (details)
            {
                p["selectInventory"] = "extend";
            }
            if (scope.Ids is { } ids)
            {
                p["groupids"] = ids;
            }
            host = scope.Ids is { Count: 0 } ? null : (await client.CallAsync<List<ZxHost>>("host.get", p, ct)).FirstOrDefault();
        }
        catch (ZabbixException e) when (e.Failure is not ZabbixFailure.NotConfigured && row is not null)
        {
            var item = (await HostItemsAsync([row], ct))[0];
            return new ZabbixHostDetail(item, null, [], [], null, await HostProblemsAsync(hostId, ct), HostLinks(row.TechnicalName, id), true, e.Message);
        }
        if (host is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "zabbix_host_not_found", "Zabbix has no such host, or not in PNMT's host groups.");
        }
        // The page refreshes the mirror's row: what it shows is what Zabbix said just now.
        if (row is not null)
        {
            var tracked = await db.ZabbixHosts.FirstAsync(h => h.HostId == hostId, ct);
            ZabbixHostSync.Apply(tracked, host, Clock.UtcNow);
            await db.SaveChangesAsync(ct);
            row = tracked;
        }
        else
        {
            row = new ZabbixHostEntity { HostId = hostId };
            ZabbixHostSync.Apply(row, host, Clock.UtcNow);
        }
        var hostItem = (await HostItemsAsync([row], ct))[0];
        return new ZabbixHostDetail(hostItem, string.IsNullOrWhiteSpace(host.Description) ? null : host.Description,
            (host.ParentTemplates ?? []).Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            (host.Tags ?? []).Select(t => new ZabbixTagItem(t.Tag, t.Value)).ToList(),
            details ? Inventory(host.Inventory) : null, await HostProblemsAsync(hostId, ct), HostLinks(host.Host, id), false, null);
    }

    private async Task<IReadOnlyList<ZabbixProblemItem>> HostProblemsAsync(long hostId, CancellationToken ct)
    {
        var id = hostId.ToString(CultureInfo.InvariantCulture);
        return await ProblemItemsAsync(snapshot.Current.Problems.Where(p => p.Hosts.Any(h => h.Hostid == id))
            .OrderByDescending(p => p.Severity).ThenByDescending(p => p.Since), ct);
    }

    public ZabbixHostLinks HostLinks(string technicalName, string id)
    {
        var links = Links;
        return new ZabbixHostLinks(links.HostView(technicalName), links.HostDashboards(id), links.HostProblems(id), links.HostLatest(id),
            links.HostConfiguration(id));
    }

    /// <summary>The inventory's fields that have a value (§7.6), "os_full" as "OS full".</summary>
    private static IReadOnlyList<ZabbixFieldItem> Inventory(JsonElement? inventory)
    {
        if (inventory is not { ValueKind: JsonValueKind.Object } fields)
        {
            return [];
        }
        return fields.EnumerateObject()
            .Where(f => f.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(f.Value.GetString()) && f.Name is not ("hostid" or "inventory_mode"))
            .Select(f => new ZabbixFieldItem(FieldName(f.Name), f.Value.GetString()!.Trim()))
            .ToList();
    }

    private static string FieldName(string name)
    {
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => w switch
        {
            "os" => "OS", "url" => "URL", "hw" => "HW", "sw" => "SW", "mac" => "MAC", "ip" => "IP", "poc" => "POC", "a" or "b" => w.ToUpperInvariant(),
            _ => w,
        }).ToList();
        if (words.Count == 0)
        {
            return name;
        }
        words[0] = words[0].Length > 0 && char.IsLower(words[0][0]) ? char.ToUpperInvariant(words[0][0]) + words[0][1..] : words[0];
        return string.Join(' ', words);
    }

    /// <summary>A host's latest data (§7.6): at most 200 items by name, <c>search</c> in their names.</summary>
    public async Task<IReadOnlyList<ZabbixItemItem>> ItemsOfHostAsync(long hostId, string? search, CancellationToken ct)
    {
        EnsureConfigured();
        var scope = await groups.ResolveAsync(false, ct);
        if (scope.Ids is { Count: 0 })
        {
            return [];
        }
        var p = new Dictionary<string, object?>
        {
            ["hostids"] = new[] { hostId.ToString(CultureInfo.InvariantCulture) },
            ["output"] = new[] { "itemid", "name", "key_", "lastvalue", "lastclock", "units", "value_type", "state", "error", "status" },
            ["sortfield"] = "name",
            ["limit"] = 200,
        };
        if (scope.Ids is { } ids)
        {
            p["groupids"] = ids;
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            p["search"] = new Dictionary<string, object?> { ["name"] = search.Trim() };
        }
        var items = await client.CallAsync<List<ZxItem>>("item.get", p, ct);
        var links = Links;
        return items.Select(i => new ZabbixItemItem(i.Itemid, i.Name, i.Key, i.Lastvalue, string.IsNullOrWhiteSpace(i.Units) ? null : i.Units,
            ZabbixPoller.Int(i.Lastclock) is > 0 and var clock ? DateTime.UnixEpoch.AddSeconds(clock) : null,
            i.State == "1" && !string.IsNullOrWhiteSpace(i.Error) ? i.Error : null, links.ItemGraph(i.Itemid))).ToList();
    }

    // --- the test --------------------------------------------------------------------------------------------------------

    /// <summary><c>POST /zabbix/test</c> (§8.5): the version, a poll and a host sync now, the host groups found.</summary>
    public async Task<ZabbixTestResult> TestAsync(CancellationToken ct)
    {
        EnsureConfigured();
        client.ForgetVersion();
        var version = await client.VersionAsync(ct);
        var scope = await groups.ResolveAsync(true, ct);
        await poller.RunAsync(ct);
        await hostSync.RunAsync(ct);
        if (snapshot.Current.Error is { } error)
        {
            throw new ZabbixException(ZabbixFailure.Unreachable, error);
        }
        var hosts = await db.ZabbixHosts.CountAsync(h => h.GoneAt == null, ct);
        var found = O.HostGroupList.Where(n => !scope.NotFound.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        return new ZabbixTestResult(version.ToString(), hosts, snapshot.Current.Problems.Count, found, scope.NotFound);
    }
}
