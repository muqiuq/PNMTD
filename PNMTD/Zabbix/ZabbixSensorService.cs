using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;
using PNMTD.Services.Events;

namespace PNMTD.Zabbix;

/// <summary>What a ZABBIX sensor's rules make of its host and problems: the code, the message, the counted problems.</summary>
public sealed record ZabbixVerdict(int Code, string Message, IReadOnlyList<string> ProblemIds);

/// <summary>
/// The ZABBIX sensor (docs/audit-redmine-zabbix-plan.md §7.7, §7.8): its settings through <c>POST</c>/<c>PUT /sensors</c>,
/// its page, the suggestions which PNMT host a Zabbix host belongs to, and attaching one or many.
/// </summary>
public sealed class ZabbixSensorService(PnmtdDbContext db, ZabbixSnapshot snapshot, ZabbixSensors sensors, ZabbixService zabbix,
    IOptionsMonitor<ZabbixOptions> options, IHttpContextAccessor accessor)
{
    public const int MaxMessage = 500;

    public const int MaxAttach = 500;

    // --- the rules -------------------------------------------------------------------------------------------------------

    public static string Label(ZabbixSeverity severity) => severity == ZabbixSeverity.NotClassified ? "Not classified" : severity.ToString();

    public static bool Counts(ZabbixSensorEntity rules, ZabbixProblem p) =>
        p.Severity >= rules.MinSeverity && (rules.CountAcknowledged || !p.Acknowledged) && (rules.CountSuppressed || !p.Suppressed);

    /// <summary>The state of §7.7: gone (491), a counted problem (490), an unavailable interface if that fails it (492), else 290.</summary>
    public static ZabbixVerdict Evaluate(ZabbixSensorEntity rules, ZabbixHostEntity? host, IReadOnlyList<ZabbixProblem> problems)
    {
        if (host is null || host.GoneAt is not null || !host.Monitored)
        {
            return new ZabbixVerdict(PNMTStatusCodes.ZABBIX_HOST_MISSING,
                "Not monitored in Zabbix any more: deleted, disabled or out of the token's host groups.", []);
        }
        var counted = problems.Where(p => Counts(rules, p)).OrderByDescending(p => p.Severity).ThenByDescending(p => p.Since).ToList();
        if (counted.Count > 0)
        {
            var message = $"{counted.Count} problem{(counted.Count == 1 ? "" : "s")}: "
                + string.Join("; ", counted.Select(p => $"{Label(p.Severity)}: {p.Name}"));
            return new ZabbixVerdict(PNMTStatusCodes.ZABBIX_PROBLEM, Cut(message), counted.Select(p => p.EventId).ToList());
        }
        if (rules.UnavailableFails)
        {
            var interfaces = JsonSerializer.Deserialize<List<ZabbixInterfaceItem>>(host.Interfaces, ZabbixClient.Json) ?? [];
            if (interfaces.FirstOrDefault(i => i.Availability == ZabbixAvailability.Unavailable) is { } down)
            {
                return new ZabbixVerdict(PNMTStatusCodes.ZABBIX_HOST_UNAVAILABLE,
                    Cut($"{down.Type} interface {down.Address}:{down.Port} unavailable{(down.Error is { } e ? $": {e}" : "")}"), []);
            }
        }
        var lesser = problems.Where(p => rules.CountSuppressed || !p.Suppressed).GroupBy(p => p.Severity).OrderByDescending(g => g.Key)
            .Select(g => $"{g.Count()} {Label(g.Key)}").ToList();
        return new ZabbixVerdict(PNMTStatusCodes.ZABBIX_OK,
            $"No problems at {Label(rules.MinSeverity)} or above{(lesser.Count > 0 ? $" ({string.Join(", ", lesser)})" : "")}", []);
    }

    private static string Cut(string text) => text.Length <= MaxMessage ? text : text[..(MaxMessage - 1)] + "…";

    // --- the sensor through /sensors -------------------------------------------------------------------------------------

    public async Task ValidateAsync(ZabbixSensorRequest? request, ZabbixSensorEntity? existing, ApiValidator v, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            throw ApiException.Conflict("zabbix_not_configured", "Zabbix is not configured (Zabbix__Url, Zabbix__ApiToken).");
        }
        if (request is null)
        {
            v.Check(existing is not null, "zabbix", "A Zabbix sensor needs its Zabbix host.");
            return;
        }
        v.Check(request.MinSeverity is null || Enum.IsDefined(request.MinSeverity.Value), "zabbix.minSeverity",
            "Not classified, Information, Warning, Average, High or Disaster.");
        if (existing is not null)
        {
            v.Check(request.ZabbixHostId == existing.ZabbixHostId, "zabbix.zabbixHostId",
                "A Zabbix sensor keeps its Zabbix host: attach the other one as a sensor of its own.");
            return;
        }
        var host = await db.ZabbixHosts.AsNoTracking().SingleOrDefaultAsync(h => h.HostId == request.ZabbixHostId, ct);
        v.Check(host is { GoneAt: null }, "zabbix.zabbixHostId", "No such Zabbix host (Zabbix's hosts are read every few minutes).");
        await EnsureNotAttachedAsync([request.ZabbixHostId], ct);
    }

    private async Task EnsureNotAttachedAsync(IReadOnlyList<long> hostIds, CancellationToken ct)
    {
        var taken = await db.ZabbixSensors.AsNoTracking().Where(z => hostIds.Contains(z.ZabbixHostId))
            .Select(z => new { z.ZabbixHostId, Host = z.Sensor.Parent.Name, z.Sensor.Name }).FirstOrDefaultAsync(ct);
        if (taken is not null)
        {
            throw ApiException.Conflict("zabbix_host_attached",
                $"This Zabbix host is attached already, as {taken.Host} / {taken.Name}: a problem has one place in PNMT.");
        }
    }

    /// <summary>A new ZABBIX sensor's row, or its rules changed (a missing request keeps them).</summary>
    public async Task ApplyAsync(SensorEntity sensor, ZabbixSensorRequest? request, CancellationToken ct)
    {
        sensor.Interval = 0;
        sensor.GracePeriod = 0;
        var row = await db.ZabbixSensors.SingleOrDefaultAsync(z => z.SensorId == sensor.Id, ct);
        if (request is null)
        {
            return;
        }
        if (row is null)
        {
            row = new ZabbixSensorEntity { SensorId = sensor.Id, ZabbixHostId = request.ZabbixHostId, Created = Clock.UtcNow };
            db.ZabbixSensors.Add(row);
        }
        var before = (row.MinSeverity, row.CountAcknowledged, row.CountSuppressed, row.UnavailableFails);
        row.MinSeverity = request.MinSeverity ?? options.CurrentValue.MinSeverity;
        row.CountAcknowledged = request.CountAcknowledged;
        row.CountSuppressed = request.CountSuppressed;
        row.UnavailableFails = request.UnavailableFails;
        if (before != (row.MinSeverity, row.CountAcknowledged, row.CountSuppressed, row.UnavailableFails))
        {
            // New rules: the next evaluation writes the state they give, whatever the last one was.
            row.LastCode = null;
            row.LastProblemIds = null;
        }
    }

    /// <summary>The type changed away from ZABBIX: the row goes.</summary>
    public Task RemoveAsync(Guid sensorId, CancellationToken ct) => db.ZabbixSensors.Where(z => z.SensorId == sensorId).ExecuteDeleteAsync(ct);

    /// <summary>After a save: the sensor's state at once, if the snapshot is fresh.</summary>
    public Task EvaluateAsync(Guid sensorId, CancellationToken ct) => sensors.EvaluateAsync(sensorId, ct);

    public async Task<ZabbixSensorDetail?> DetailAsync(Guid sensorId, CancellationToken ct)
    {
        var row = await db.ZabbixSensors.AsNoTracking().SingleOrDefaultAsync(z => z.SensorId == sensorId, ct);
        if (row is null)
        {
            return null;
        }
        var host = await db.ZabbixHosts.AsNoTracking().SingleOrDefaultAsync(h => h.HostId == row.ZabbixHostId, ct);
        var id = row.ZabbixHostId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var state = snapshot.Current;
        var mine = state.Problems.Where(p => p.Hosts.Any(h => h.Hostid == id)).OrderByDescending(p => p.Severity).ThenByDescending(p => p.Since).ToList();
        var items = await zabbix.ProblemItemsAsync(mine, ct);
        var interfaces = host is null ? [] : JsonSerializer.Deserialize<List<ZabbixInterfaceItem>>(host.Interfaces, ZabbixClient.Json) ?? [];
        return new ZabbixSensorDetail(row.ZabbixHostId, host?.VisibleName ?? $"Zabbix host {id}", host?.TechnicalName ?? "", host is null || host.GoneAt is not null,
            host?.Monitored ?? false, host?.InMaintenance ?? false, host?.Availability ?? ZabbixAvailability.Unknown, interfaces, row.MinSeverity,
            row.CountAcknowledged, row.CountSuppressed, row.UnavailableFails,
            mine.Select((p, i) => new ZabbixSensorProblem(items[i], Counts(row, p))).ToList(),
            options.CurrentValue.Enabled && host is not null ? zabbix.HostLinks(host.TechnicalName, id) : null, state.AsOf,
            state.Error is null ? null : $"Zabbix not reached since {(state.AsOf is { } at ? Clock.ToLocal(at).ToString("HH:mm") : "PNMT started")}: {state.Error}");
    }

    // --- suggestions and attaching ---------------------------------------------------------------------------------------

    /// <summary>
    /// Unattached Zabbix hosts with the PNMT hosts they match (§7.8): by name (equal, or the same first label), or by an
    /// address PNMT knows for a host (a ping target, a router's address, an agent's hostname). One match is the suggestion.
    /// </summary>
    public async Task<IReadOnlyList<ZabbixSuggestion>> SuggestionsAsync(CancellationToken ct)
    {
        var unattached = await db.ZabbixHosts.AsNoTracking().Where(h => h.GoneAt == null && !db.ZabbixSensors.Any(z => z.ZabbixHostId == h.HostId))
            .OrderBy(h => h.VisibleName).Take(2000).ToListAsync(ct);
        if (unattached.Count == 0)
        {
            return [];
        }
        var hosts = await db.Hosts.AsNoTracking().Select(h => new { h.Id, h.Name }).ToListAsync(ct);
        var names = hosts.ToDictionary(h => h.Id, h => h.Name);
        var known = new List<(string Address, Guid HostId, string Reason)>();
        foreach (var ping in await db.Sensors.AsNoTracking().Where(s => s.Type == SensorType.PING && s.Parameters != null)
            .Select(s => new { s.ParentId, s.Parameters }).ToListAsync(ct))
        {
            known.Add((ping.Parameters!.Trim(), ping.ParentId, $"ping target {ping.Parameters!.Trim()}"));
        }
        foreach (var router in await db.MikrotikRouters.AsNoTracking().Where(r => r.Address != null)
            .Select(r => new { r.Sensor.ParentId, r.Address }).ToListAsync(ct))
        {
            known.Add((router.Address!.Trim(), router.ParentId, $"router's address {router.Address!.Trim()}"));
        }
        foreach (var agent in await db.Agents.AsNoTracking().Select(a => new { a.Sensor.ParentId, a.Hostname, a.Fqdn }).ToListAsync(ct))
        {
            foreach (var name in new[] { agent.Hostname, agent.Fqdn }.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                known.Add((name!.Trim(), agent.ParentId, $"agent's hostname {name!.Trim()}"));
            }
        }
        var byAddress = known.ToLookup(k => k.Address.ToLowerInvariant());
        var byName = hosts.ToLookup(h => h.Name.Trim().ToLowerInvariant());
        var byLabel = hosts.ToLookup(h => FirstLabel(h.Name));
        var items = await zabbix.HostItemsAsync(unattached, ct);
        return unattached.Select((z, i) =>
        {
            var candidates = new List<ZabbixCandidate>();
            void Add(Guid hostId, string reason)
            {
                if (candidates.All(c => c.HostId != hostId))
                {
                    candidates.Add(new ZabbixCandidate(hostId, names.GetValueOrDefault(hostId) ?? "", reason));
                }
            }
            foreach (var name in new[] { z.TechnicalName, z.VisibleName }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var h in byName[name.Trim().ToLowerInvariant()])
                {
                    Add(h.Id, "same name");
                }
            }
            foreach (var name in new[] { z.TechnicalName, z.VisibleName })
            {
                foreach (var h in byLabel[FirstLabel(name)])
                {
                    Add(h.Id, $"{h.Name} = {name}");
                }
            }
            foreach (var address in z.Addresses)
            {
                foreach (var k in byAddress[address])
                {
                    Add(k.HostId, k.Reason);
                }
                if (!System.Net.IPAddress.TryParse(address, out var _))
                {
                    foreach (var h in byLabel[FirstLabel(address)])
                    {
                        Add(h.Id, $"{h.Name} = {address}");
                    }
                }
            }
            return new ZabbixSuggestion(items[i], candidates, candidates.Count == 1 ? candidates[0].HostId : null);
        }).OrderBy(s => s.Suggested is null).ThenBy(s => s.Host.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"web01.example.com" and "WEB01" are "web01"; an address stays as it is.</summary>
    internal static string FirstLabel(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return System.Net.IPAddress.TryParse(n, out _) ? n : n.Split('.')[0];
    }

    /// <summary>
    /// Attaches Zabbix hosts as ZABBIX sensors (§7.8), all or none: each to a PNMT host, or to a new one named as asked. One
    /// is a SensorCreated in the audit log; several are one ZabbixHostsAttached listing them.
    /// </summary>
    public async Task<IReadOnlyList<ZabbixAttached>> AttachAsync(ZabbixAttachRequest request, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
        {
            throw new ZabbixException(ZabbixFailure.NotConfigured, "Zabbix is not configured (Zabbix__Url, Zabbix__ApiToken).");
        }
        var items = request.Items ?? [];
        var v = new ApiValidator()
            .Check(items.Count is >= 1 and <= MaxAttach, "items", $"Between 1 and {MaxAttach} Zabbix hosts.")
            .Check(items.Select(i => i.ZabbixHostId).Distinct().Count() == items.Count, "items", "Each Zabbix host once.");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var newName = item.NewHostName?.Trim();
            v.Check(item.HostId is null != string.IsNullOrEmpty(newName), $"items[{i}].hostId", "A PNMT host, or the name of a new one.");
            v.Check(newName is null or { Length: >= 2 and <= 64 }, $"items[{i}].newHostName", "The name must have 2 to 64 characters.");
            v.Check(item.MinSeverity is null || Enum.IsDefined(item.MinSeverity.Value), $"items[{i}].minSeverity",
                "Not classified, Information, Warning, Average, High or Disaster.");
        }
        v.ThrowIfInvalid();
        var zabbixIds = items.Select(i => i.ZabbixHostId).ToList();
        var mirror = await db.ZabbixHosts.AsNoTracking().Where(h => zabbixIds.Contains(h.HostId) && h.GoneAt == null).ToDictionaryAsync(h => h.HostId, ct);
        var hostIds = items.Where(i => i.HostId is not null).Select(i => i.HostId!.Value).Distinct().ToList();
        var hosts = await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        for (var i = 0; i < items.Count; i++)
        {
            v.Check(mirror.ContainsKey(items[i].ZabbixHostId), $"items[{i}].zabbixHostId", "No such Zabbix host.");
            v.Check(items[i].HostId is not { } h || hosts.ContainsKey(h), $"items[{i}].hostId", "No such host.");
        }
        v.ThrowIfInvalid();
        await EnsureNotAttachedAsync(zabbixIds, ct);

        var now = Clock.UtcNow;
        var created = new List<(SensorEntity Sensor, HostEntity Host, long ZabbixHostId)>();
        var newHosts = new Dictionary<string, HostEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var z = mirror[item.ZabbixHostId];
            HostEntity host;
            if (item.HostId is { } hostId)
            {
                host = hosts[hostId];
            }
            else if (!newHosts.TryGetValue(item.NewHostName!.Trim(), out host!))
            {
                host = new HostEntity { Id = Guid.NewGuid(), Name = item.NewHostName!.Trim(), Created = now, Enabled = true };
                db.Hosts.Add(host);
                newHosts[host.Name] = host;
            }
            var sensor = new SensorEntity
            {
                Id = Guid.NewGuid(),
                ParentId = host.Id,
                Type = SensorType.ZABBIX,
                Name = z.VisibleName.Length > 200 ? z.VisibleName[..200] : z.VisibleName,
                Enabled = true,
                Created = now,
            };
            sensor.SetNewSecretToken();
            db.Sensors.Add(sensor);
            db.ZabbixSensors.Add(new ZabbixSensorEntity
            {
                SensorId = sensor.Id,
                ZabbixHostId = z.HostId,
                MinSeverity = item.MinSeverity ?? options.CurrentValue.MinSeverity,
                Created = now,
            });
            created.Add((sensor, host, z.HostId));
        }
        if (created.Count == 1)
        {
            var (sensor, host, _) = created[0];
            AuditLog.Target(AuditTarget.Sensor(sensor.Id, $"{host.Name} / {sensor.Name}", host.Id, host.LocationId));
            AuditLog.Summary($"Zabbix host {mirror[items[0].ZabbixHostId].TechnicalName} attached to {host.Name}");
        }
        else
        {
            AuditLog.Record(db, AuditEvents.ZabbixHostsAttached, $"{created.Count} Zabbix hosts attached: "
                + string.Join(", ", created.Take(20).Select(c => $"{c.Sensor.Name} → {c.Host.Name}")) + (created.Count > 20 ? ", …" : ""),
                AuditWho.From(accessor),
                AuditTarget.Many(AuditTargets.Sensor, created.Select(c => (c.Sensor.Id, $"{c.Host.Name} / {c.Sensor.Name}")).ToList()));
        }
        await db.SaveChangesAsync(ct);
        foreach (var (sensor, _, _) in created)
        {
            await sensors.EvaluateAsync(sensor.Id, ct);
        }
        return created.Select(c => new ZabbixAttached(c.Sensor.Id, new Ref(c.Host.Id, c.Host.Name), c.ZabbixHostId, c.Sensor.Name)).ToList();
    }
}

/// <summary>
/// The ZABBIX sensors' state after every poll (§7.7): an event when the code or the counted problems change, through
/// <see cref="SensorEventWriter"/>, so notifications apply. Nothing while the snapshot is stale (Zabbix not answering is
/// the system sensor's, not every sensor's), nor before the hosts were synced once.
/// </summary>
public sealed class ZabbixSensors(IServiceScopeFactory scopes, ZabbixSnapshot snapshot, ILogger<ZabbixSensors> logger)
{
    public const string Source = "Zabbix";

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task EvaluateAsync(Guid? only, CancellationToken ct)
    {
        var state = snapshot.Current;
        if (state.AsOf is null || state.Error is not null)
        {
            return;
        }
        await gate.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PnmtdDbContext>();
            // Before the first host sync the mirror is empty: every host would look gone (491).
            if (!db.KeyValueKeyExists(Models.Enums.KeyValueKeyEnums.LAST_ZABBIX_HOST_SYNC))
            {
                return;
            }
            var rows = await db.ZabbixSensors.Where(z => z.Sensor.Enabled && (only == null || z.SensorId == only)).ToListAsync(ct);
            if (rows.Count == 0)
            {
                return;
            }
            var ids = rows.Select(r => r.ZabbixHostId).ToList();
            var hosts = await db.ZabbixHosts.AsNoTracking().Where(h => ids.Contains(h.HostId)).ToDictionaryAsync(h => h.HostId, ct);
            var byHost = state.Problems.SelectMany(p => p.Hosts.Select(h => (Id: ZabbixPoller.Int(h.Hostid), Problem: p)))
                .ToLookup(x => x.Id, x => x.Problem);
            foreach (var row in rows)
            {
                var verdict = ZabbixSensorService.Evaluate(row, hosts.GetValueOrDefault(row.ZabbixHostId), byHost[row.ZabbixHostId].ToList());
                var problemIds = string.Join(",", verdict.ProblemIds.Order(StringComparer.Ordinal));
                if (row.LastCode == verdict.Code && (row.LastProblemIds ?? "") == problemIds)
                {
                    continue;
                }
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await SensorEventWriter.WriteAsync(db, row.SensorId, verdict.Code, verdict.Message, Source, ct);
                row.LastCode = verdict.Code;
                row.LastProblemIds = problemIds.Length > 4000 ? problemIds[..4000] : problemIds;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "The Zabbix sensors' evaluation failed");
        }
        finally
        {
            gate.Release();
        }
    }
}
