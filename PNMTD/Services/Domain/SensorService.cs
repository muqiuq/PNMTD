using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Logic.IntervalDescriptions;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

public sealed class SensorService(PnmtdDbContext db, MonitoringReader reader, PublicUrls urls, NotificationRuleService rules, UdpNodeService udp,
    MikrotikRouterService mikrotik, Agents.AgentHub agents, Zabbix.ZabbixSensorService zabbix, Certificates.CertificateSensorService certificates,
    Advanced.AdvancedSensorService advanced, Licenses.LicenseQueries licenses, IConfiguration configuration)
{
    private static readonly string[] SortFields = ["name", "host", "state", "type", "lastevent"];

    public async Task<Paged<SensorListItem>> ListAsync(SensorQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "name", SortFields);
        var hostId = query.HostId;
        var snapshot = await reader.SnapshotAsync(hostId is { } h ? s => s.ParentId == h : null, ct);
        var items = snapshot.Sensors.Values
            .Where(s => query.Type is not { } type || s.Info.Type == type)
            .Where(s => query.State is not { } state || s.State == state)
            .Where(s => query.Importance is not { } importance || s.Info.EffectiveImportance == importance)
            .Where(s => Text.Contains(s.Info.Name, query.Search) || Text.Contains(s.Info.HostName, query.Search))
            .Select(ToListItem);
        IOrderedEnumerable<SensorListItem> sorted = field switch
        {
            "host" => items.OrderBy(i => i.Host.Name, StringComparer.OrdinalIgnoreCase),
            "state" => items.OrderBy(i => StateOrder(i.State)),
            "type" => items.OrderBy(i => i.Type.ToString(), StringComparer.Ordinal),
            "lastevent" => items.OrderBy(i => i.LastEventAt ?? DateTime.MinValue),
            _ => items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = (descending ? sorted.Reverse() : sorted.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return Paging.Page(list, query);
    }

    internal static int StateOrder(SensorState state) => state switch
    {
        SensorState.Failing => 0,
        SensorState.NoData => 1,
        SensorState.Ok => 2,
        SensorState.Ignored => 3,
        _ => 4,
    };

    private static SensorListItem ToListItem(SensorStatus s) => new(s.Info.Id, new Ref(s.Info.HostId, s.Info.HostName),
        s.Info.ParentSensorId, s.Info.Name, s.Info.Type, s.Info.Enabled, s.Info.Ignore, s.State, s.LastCode, s.LastMessage,
        s.LastEventAt, s.FailingSince, s.Info.Importance, s.Info.EffectiveImportance);

    public async Task<SensorDetail> GetAsync(Guid id, bool withSecrets, CancellationToken ct)
    {
        var sensor = await db.Sensors.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Sensor");
        var parentId = sensor.OlderSiblingId;
        // The sensor, its children, and its ENCAPSULADED parent (whose Enabled and Ignore it inherits).
        var snapshot = await reader.SnapshotAsync(s => s.Id == id || s.OlderSiblingId == id || s.Id == parentId, ct);
        var status = snapshot.Sensors[id];
        var uptimeIds = status.IsContainer ? status.Children.Select(c => c.Info.Id).ToList() : [id];
        var days = await reader.DaysAsync(uptimeIds, 90, ct);
        var hours = await reader.HoursAsync(uptimeIds, 24, ct);

        var isUdp = sensor.Type == SensorType.UDP_NODE;
        SensorIntegration? integration = withSecrets
            ? new SensorIntegration(sensor.SecretWriteToken, sensor.SecretReadToken, urls.WriteUrl(sensor.SecretWriteToken),
                sensor.SecretReadToken is { } read ? urls.ReadUrl(read) : null, isUdp ? await udp.SecretsAsync(id, ct) : null,
                sensor.Type == SensorType.ADVANCED ? urls.ReportUrl : null)
            : null;

        return new SensorDetail(sensor.Id, new Ref(status.Info.HostId, status.Info.HostName), sensor.OlderSiblingId, sensor.Name,
            sensor.Type, sensor.Enabled, sensor.Ignore, sensor.Interval, sensor.GracePeriod, sensor.Parameters, sensor.TextId,
            string.IsNullOrEmpty(sensor.Status) ? null : sensor.Status, Instants.Utc(sensor.Created), status.State,
            status.LastCode, status.LastMessage, status.LastEventAt, status.FailingSince,
            hours.AverageOverall(uptimeIds, 24), days.AverageOverall(uptimeIds, 30), days.Series(days.Average(uptimeIds)),
            status.Children.OrderBy(c => c.Info.Name, StringComparer.OrdinalIgnoreCase).Select(c => c.ToSummary()).ToList(),
            integration, sensor.Notes, isUdp ? await udp.DetailAsync(id, ct) : null,
            sensor.Type == SensorType.MIKROTIK ? await mikrotik.DetailAsync(id, ct) : null,
            sensor.Importance, status.Info.EffectiveImportance,
            sensor.Type == SensorType.ZABBIX ? await zabbix.DetailAsync(id, ct) : null,
            sensor.Type == SensorType.SSL_CERTIFICATE ? await certificates.SummaryAsync(id, ct) : null,
            sensor.Type == SensorType.ADVANCED ? await advanced.DetailAsync(id, ct) : null,
            // docs/licences-plan.md §5.3, §5.4: the item a sensor stands for, the items linked to it; nothing while the feature is off.
            sensor.Type == SensorType.LICENSE && Licenses.LicenseSetup.Enabled(configuration) ? await licenses.SensorSummaryAsync(id, ct) : null,
            Licenses.LicenseSetup.Enabled(configuration) ? await licenses.LinkedItemsAsync(id, ct) : []);
    }

    /// <summary>Many sensors' importance at once (docs/ai-operations-plan.md §11.1); null: their hosts'.</summary>
    public async Task<int> SetImportanceAsync(ImportanceRequest request, CancellationToken ct)
    {
        new ApiValidator()
            .Check(request.Ids.Count is >= 1 and <= 5000, "ids", "Between 1 and 5000 sensors.")
            .Check(request.Importance is null || Enum.IsDefined(request.Importance.Value), "importance", "Low, Normal, High, or null for the host's.")
            .ThrowIfInvalid();
        var ids = request.Ids.Distinct().ToList();
        // A bulk update: the audit learns the sensors here (docs/audit-redmine-zabbix-plan.md §3.3).
        var sensors = await db.Sensors.AsNoTracking().Where(s => ids.Contains(s.Id) && s.Importance != request.Importance)
            .OrderBy(s => s.Parent.Name).ThenBy(s => s.Name).Select(s => new { s.Id, Name = s.Parent.Name + " / " + s.Name }).ToListAsync(ct);
        AuditLog.Target(AuditTarget.Many(AuditTargets.Sensor, sensors.Select(s => (s.Id, s.Name)).ToList()));
        AuditLog.Summary($"{sensors.Count} sensor{(sensors.Count == 1 ? "" : "s")}: importance {request.Importance?.ToString() ?? "the host's"}");
        return await db.Sensors.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(u => u.SetProperty(s => s.Importance, request.Importance), ct);
    }

    public async Task<SensorDetail> CreateAsync(SensorRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, existing: null, ct);
        var sensor = new SensorEntity
        {
            Id = Guid.NewGuid(),
            Created = Clock.UtcNow,
        };
        Apply(sensor, request);
        sensor.SetNewSecretToken();
        db.Sensors.Add(sensor);
        if (sensor.Type == SensorType.UDP_NODE)
        {
            await udp.ApplyAsync(sensor, request.Udp, ct);
        }
        if (sensor.Type == SensorType.MIKROTIK)
        {
            await mikrotik.ApplyAsync(sensor, request.Mikrotik, ct);
        }
        if (sensor.Type == SensorType.ZABBIX)
        {
            await zabbix.ApplyAsync(sensor, request.Zabbix, ct);
        }
        if (sensor.Type == SensorType.SSL_CERTIFICATE)
        {
            // Checked within 15 seconds (docs/ssl-certificate-plan.md §5.2).
            await certificates.ApplyAsync(sensor, request.Certificate, null, 0, ct);
        }
        if (sensor.Type == SensorType.ADVANCED)
        {
            // The side row and the starter rule (docs/advanced-sensor-plan.md §6.5); the first report starts the verdict.
            await advanced.ApplyAsync(sensor, request.Advanced, null, ct);
        }
        await db.SaveChangesAsync(ct);
        udp.Changed();
        await mikrotik.NotifyGatewayAsync(ct);
        if (sensor.Type == SensorType.ZABBIX)
        {
            await zabbix.EvaluateAsync(sensor.Id, ct);
        }
        return await GetAsync(sensor.Id, withSecrets: true, ct);
    }

    /// <summary>The writable fields only: tokens, Created and Status cannot be set (issues 1–2).</summary>
    public async Task<SensorDetail> UpdateAsync(Guid id, SensorRequest request, bool withSecrets, CancellationToken ct)
    {
        var sensor = await db.Sensors.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Sensor");
        if (sensor.Type is SensorType.SYSTEM or SensorType.WINDOWS or SensorType.LINUX or SensorType.DIAGNOSTICS)
        {
            // PNMT owns its system sensors (docs/ai-operations-plan.md §4), a server with an agent is its agent's and a
            // diagnostics sensor its assignment's (docs/windows-sensor-plan.md §8.1; docs/linux-sensor-plan.md §9.1): a new name,
            // host, notes, Enabled and Ignore only.
            var name = request.Name?.Trim();
            var v = new ApiValidator()
                .Check(request.Type == sensor.Type, "type", $"A {sensor.Type} sensor keeps its type.")
                .Check(name is { Length: >= 1 and <= 200 }, "name", "The name must have 1 to 200 characters.")
                .Check(request.Notes is null || request.Notes.Length <= UdpNodeService.MaxNotes, "notes", $"At most {UdpNodeService.MaxNotes} characters.");
            v.Check(await db.Hosts.AnyAsync(h => h.Id == request.HostId, ct), "hostId", "No such host.");
            v.ThrowIfInvalid();
            sensor.Name = name!;
            sensor.ParentId = request.HostId;
            sensor.Enabled = request.Enabled;
            sensor.Ignore = request.Ignore;
            sensor.Importance = request.Importance;
            sensor.Notes = Text.Trimmed(request.Notes) is null ? null : request.Notes;
        }
        else if (sensor.Type == SensorType.LICENSE)
        {
            // An item sensor is its assignment's (docs/licences-plan.md §5.3): its name and host follow the item.
            new ApiValidator()
                .Check(request.Type == SensorType.LICENSE, "type", "These sensors are made by assigning a licence or service.")
                .Check(request.Notes is null || request.Notes.Length <= UdpNodeService.MaxNotes, "notes", $"At most {UdpNodeService.MaxNotes} characters.")
                .ThrowIfInvalid();
            sensor.Enabled = request.Enabled;
            sensor.Ignore = request.Ignore;
            sensor.Importance = request.Importance;
            sensor.Notes = Text.Trimmed(request.Notes) is null ? null : request.Notes;
        }
        else if (sensor.Type == SensorType.DNS)
        {
            // DnsCheckTask owns DNS sensors and finds them by host and zone name.
            new ApiValidator()
                .Check(request.Type == SensorType.DNS, "type", "A DNS sensor keeps its type.")
                .ThrowIfInvalid();
            new ApiValidator().Check(request.Notes is null || request.Notes.Length <= UdpNodeService.MaxNotes, "notes",
                $"At most {UdpNodeService.MaxNotes} characters.").ThrowIfInvalid();
            sensor.Enabled = request.Enabled;
            sensor.Ignore = request.Ignore;
            sensor.Importance = request.Importance;
            sensor.Notes = Text.Trimmed(request.Notes) is null ? null : request.Notes;
        }
        else
        {
            var wasUdp = sensor.Type == SensorType.UDP_NODE;
            var wasMikrotik = sensor.Type == SensorType.MIKROTIK;
            var wasZabbix = sensor.Type == SensorType.ZABBIX;
            var wasCertificate = sensor.Type == SensorType.SSL_CERTIFICATE;
            var wasAdvanced = sensor.Type == SensorType.ADVANCED;
            var (oldParameters, oldInterval, oldGrace) = (sensor.Parameters, sensor.Interval, sensor.GracePeriod);
            await ValidateAsync(request, sensor, ct);
            Apply(sensor, request);
            if (sensor.Type == SensorType.UDP_NODE)
            {
                await udp.ApplyAsync(sensor, request.Udp, ct);
            }
            else if (wasUdp)
            {
                await udp.RemoveAsync(sensor.Id, ct);
            }
            if (sensor.Type == SensorType.MIKROTIK)
            {
                await mikrotik.ApplyAsync(sensor, request.Mikrotik, ct);
            }
            else if (wasMikrotik)
            {
                await mikrotik.RemoveAsync(sensor.Id, ct);
            }
            if (sensor.Type == SensorType.ZABBIX)
            {
                await zabbix.ApplyAsync(sensor, request.Zabbix, ct);
            }
            else if (wasZabbix)
            {
                await zabbix.RemoveAsync(sensor.Id, ct);
            }
            if (sensor.Type == SensorType.SSL_CERTIFICATE)
            {
                await certificates.ApplyAsync(sensor, request.Certificate, wasCertificate ? oldParameters : null, wasCertificate ? oldInterval : 0, ct);
            }
            else if (wasCertificate)
            {
                await certificates.RemoveAsync(sensor.Id, ct);
            }
            if (sensor.Type == SensorType.ADVANCED)
            {
                await advanced.ApplyAsync(sensor, request.Advanced, wasAdvanced ? (oldInterval, oldGrace) : null, ct);
            }
            else if (wasAdvanced)
            {
                // The rules, fields, reports and samples go (docs/advanced-sensor-plan.md §7.4).
                await advanced.RemoveAsync(sensor.Id, ct);
            }
        }
        await db.SaveChangesAsync(ct);
        udp.Changed();
        // A router's tunnel follows its sensor's Enabled and its mode.
        await mikrotik.NotifyGatewayAsync(ct, always: sensor.Type == SensorType.MIKROTIK);
        if (sensor.Type == SensorType.ZABBIX)
        {
            await zabbix.EvaluateAsync(sensor.Id, ct);
        }
        if (sensor.Type == SensorType.SSL_CERTIFICATE)
        {
            await certificates.EvaluateAsync(sensor.Id, ct);
        }
        if (sensor.Type == SensorType.ADVANCED)
        {
            // Another interval or grace: the evaluator re-judges the newest report, and the answer waits for it (§7.5).
            await advanced.AfterSaveAsync(sensor.Id, ct);
        }
        return await GetAsync(id, withSecrets, ct);
    }

    /// <summary>The sensor, its ENCAPSULADED children, and their events and rule links.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var sensor = await db.Sensors.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new { s.Type, s.Name, s.ParentId, Host = s.Parent.Name, s.Parent.LocationId }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Sensor");
        var type = sensor.Type;
        if (type == SensorType.SSL_CERTIFICATE)
        {
            certificates.EnsureMayEdit();
        }
        // Deleted by ExecuteDelete below: the audit learns the sensor here (docs/audit-redmine-zabbix-plan.md §3.3).
        AuditLog.Target(AuditTarget.Sensor(id, $"{sensor.Host} / {sensor.Name}", sensor.ParentId, sensor.LocationId));
        AuditLog.Summary($"{sensor.Host} / {sensor.Name} deleted ({sensor.Type})");
        if (type == SensorType.SYSTEM)
        {
            throw ApiException.Conflict("system_sensor", "A system sensor cannot be deleted (PNMT would create it again): disable it instead.");
        }
        if (type == SensorType.DIAGNOSTICS)
        {
            throw ApiException.Conflict("diagnostics_sensor", "A diagnostics sensor goes with its assignment: delete the assignment on Admin → Diagnostics.");
        }
        if (type == SensorType.LICENSE)
        {
            throw ApiException.Conflict("license_sensor", "Unassign it on the licence or service instead.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (Agents.AgentPlatformInfo.IsAgentType(type))
        {
            // Its diagnostics assignments' sensors go with it (docs/windows-sensor-plan.md §5.2).
            var assignments = await db.DiagnosticAssignments.AsNoTracking().Where(a => a.TargetSensorId == id).ToListAsync(ct);
            await DiagnosticsSetService.DeleteAssignmentsAsync(db, assignments, ct);
        }
        var ids = await db.Sensors.Where(s => s.Id == id || s.OlderSiblingId == id).Select(s => s.Id).ToListAsync(ct);
        await db.MailInputs.Where(m => m.SensorOutputId != null && ids.Contains(m.SensorOutputId.Value))
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.SensorOutputId, (Guid?)null), ct);
        await db.Sensors.Where(s => s.OlderSiblingId == id).ExecuteDeleteAsync(ct);
        await db.Sensors.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        // A UDP node goes with its sensor (cascade); its commands, updates and log stay (§4.3).
        udp.Changed();
        // So does a router's tunnel: the gateway drops the peer (docs/mikrotik-plan.md §4.6).
        await mikrotik.NotifyGatewayAsync(ct, always: true);
        // And a server's agent: its connection ends (docs/windows-sensor-plan.md §8.1).
        if (Agents.AgentPlatformInfo.IsAgentType(type))
        {
            await agents.DisconnectAsync(id, Lib.Agent.AgentCloseCodes.Revoked, "the server was deleted in PNMT");
        }
    }

    public async Task<SensorIntegration> RotateTokensAsync(Guid id, RotateTokensRequest request, CancellationToken ct)
    {
        var sensor = await db.Sensors.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw ApiException.NotFound("Sensor");
        new ApiValidator().Check(request.Write || request.Read, "write", "Rotate the write token, the read token or both.").ThrowIfInvalid();
        if (request.Write)
        {
            sensor.SetNewWriteToken();
        }
        if (request.Read)
        {
            sensor.SetRandomSecretReadToken();
        }
        await db.SaveChangesAsync(ct);
        return new SensorIntegration(sensor.SecretWriteToken, sensor.SecretReadToken, urls.WriteUrl(sensor.SecretWriteToken),
            sensor.SecretReadToken is { } read ? urls.ReadUrl(read) : null, ReportUrl: sensor.Type == SensorType.ADVANCED ? urls.ReportUrl : null);
    }

    public async Task<TokenLookupResult> LookupAsync(TokenLookupRequest request, CancellationToken ct)
    {
        var token = request.Token?.Trim();
        new ApiValidator().Check(!string.IsNullOrEmpty(token), "token", "A write or read token.").ThrowIfInvalid();
        var match = await db.Sensors.AsNoTracking()
            .Where(s => s.SecretWriteToken == token || s.SecretReadToken == token)
            .OrderBy(s => s.Created)
            .Select(s => new { s.Id, s.Name, s.ParentId, HostName = s.Parent.Name, Write = s.SecretWriteToken == token })
            .FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("A sensor with this token");
        return new TokenLookupResult(match.Id, match.Name, new Ref(match.ParentId, match.HostName), match.Write ? "write" : "read");
    }

    public async Task<IReadOnlyList<NotificationRuleItem>> NotificationRulesAsync(Guid id, bool withSecrets, CancellationToken ct)
    {
        await EnsureExistsAsync(id, ct);
        return await rules.ForSensorAsync(id, withSecrets, ct);
    }

    /// <summary>Idempotent: linking twice is one link.</summary>
    public async Task LinkRuleAsync(Guid id, Guid ruleId, CancellationToken ct)
    {
        await EnsureExistsAsync(id, ct);
        await rules.LinkAsync(ruleId, id, ct);
    }

    public async Task UnlinkRuleAsync(Guid id, Guid ruleId, CancellationToken ct)
    {
        await EnsureExistsAsync(id, ct);
        await rules.UnlinkAsync(ruleId, id, ct);
    }

    public async Task EnsureExistsAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Sensors.AnyAsync(s => s.Id == id, ct))
        {
            throw ApiException.NotFound("Sensor");
        }
    }

    private async Task ValidateAsync(SensorRequest request, SensorEntity? existing, CancellationToken ct)
    {
        if (request.Type == SensorType.SSL_CERTIFICATE || existing?.Type == SensorType.SSL_CERTIFICATE)
        {
            // Certificates: Edit besides the endpoint's Monitoring: Edit (docs/ssl-certificate-plan.md §6.1).
            certificates.EnsureMayEdit();
        }
        var v = new ApiValidator();
        var name = request.Name?.Trim();
        v.Check(name is { Length: >= 1 and <= 200 }, "name", "The name must have 1 to 200 characters.");
        v.Check(Enum.IsDefined(request.Type), "type", "Unknown sensor type.");
        v.Check(request.Type != SensorType.DNS, "type", "DNS sensors are created by their DNS zone.");
        v.Check(request.Type != SensorType.SYSTEM, "type", "System sensors are PNMT's own; it creates them.");
        v.Check(request.Type != SensorType.WINDOWS || existing?.Type == SensorType.WINDOWS, "type", "A Windows server joins with a token: Add server.");
        v.Check(request.Type != SensorType.LINUX || existing?.Type == SensorType.LINUX, "type", "A Linux server joins with a token: Add server.");
        v.Check(request.Type != SensorType.DIAGNOSTICS || existing?.Type == SensorType.DIAGNOSTICS, "type", "A diagnostics sensor is created by assigning a set on Admin → Diagnostics.");
        v.Check(request.Type != SensorType.LICENSE, "type", "These sensors are made by assigning a licence or service.");
        v.Check(request.Interval is >= 0 and <= 31_536_000, "interval", "Between 0 and 31536000 seconds.");
        v.Check(request.GracePeriod is >= 0 and <= 31_536_000, "gracePeriod", "Between 0 and 31536000 seconds.");
        v.Check(request.TextId is null || request.TextId.Length <= 200, "textId", "At most 200 characters.");
        v.Check(request.Notes is null || request.Notes.Length <= UdpNodeService.MaxNotes, "notes", $"At most {UdpNodeService.MaxNotes} characters.");
        v.Check(request.Importance is null || Enum.IsDefined(request.Importance.Value), "importance", "Low, Normal, High, or none for the host's.");
        if (request.Type == SensorType.UDP_NODE)
        {
            var node = existing is null ? null : await db.UdpNodes.AsNoTracking().SingleOrDefaultAsync(n => n.SensorId == existing.Id, ct);
            await udp.ValidateAsync(request.Udp, node, existing?.Id, v, ct);
        }
        if (request.Type == SensorType.MIKROTIK)
        {
            var router = existing is null || existing.Type != SensorType.MIKROTIK
                ? null
                : await db.MikrotikRouters.AsNoTracking().SingleOrDefaultAsync(r => r.SensorId == existing.Id, ct);
            mikrotik.Validate(request.Mikrotik, router, v);
            v.Check(request.Interval >= Mikrotik.RouterPoller.MinimumIntervalSeconds, "interval",
                $"A MikroTik router is polled at most every {Mikrotik.RouterPoller.MinimumIntervalSeconds} seconds: an interval of {Mikrotik.RouterPoller.MinimumIntervalSeconds} or more.");
        }
        if (request.Type == SensorType.ZABBIX)
        {
            var row = existing is null || existing.Type != SensorType.ZABBIX
                ? null
                : await db.ZabbixSensors.AsNoTracking().SingleOrDefaultAsync(z => z.SensorId == existing.Id, ct);
            await zabbix.ValidateAsync(request.Zabbix, row, v, ct);
        }
        if (request.Type == SensorType.SSL_CERTIFICATE)
        {
            var row = existing is null || existing.Type != SensorType.SSL_CERTIFICATE
                ? null
                : await db.CertificateSensors.AsNoTracking().SingleOrDefaultAsync(c => c.SensorId == existing.Id, ct);
            await certificates.ValidateAsync(request, row, v, ct);
        }
        if (request.Type == SensorType.ADVANCED)
        {
            Advanced.AdvancedSensorService.Validate(request, v);
        }
        if (request.Type is SensorType.HEARTBEAT or SensorType.HEARTBEAT_VALUECHECK or SensorType.PING or SensorType.UDP_NODE)
        {
            v.Check(request.Interval > 0, "interval", $"A {request.Type} sensor needs an interval.");
        }
        var parameters = request.Parameters?.Trim();
        switch (request.Type)
        {
            case SensorType.PING:
                v.Check(parameters is { Length: > 0 and <= 253 } && !parameters.Any(char.IsWhiteSpace), "parameters",
                    "The host name or address to ping.");
                break;
            case SensorType.VALUECHECK or SensorType.HEARTBEAT_VALUECHECK:
                v.Check(!string.IsNullOrEmpty(request.Parameters) && IsRegex(request.Parameters), "parameters",
                    "A regular expression; a message that matches it is OK (275), any other fails (475).");
                break;
            case SensorType.ONE_WITHIN_TIMESPAN or SensorType.ALL_WITHIN_TIMESPAN:
                v.Check(IntervalDescriptionHelper.TryParse(parameters, out _), "parameters",
                    "An interval descriptor, such as \"daily 8\", \"weekly monday\" or \"monthly 15\".");
                break;
        }
        if (existing is null || existing.ParentId != request.HostId)
        {
            v.Check(await db.Hosts.AnyAsync(h => h.Id == request.HostId, ct), "hostId", "No such host.");
        }
        v.ThrowIfInvalid();
    }

    internal static bool IsRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Apply(SensorEntity sensor, SensorRequest request)
    {
        sensor.ParentId = request.HostId;
        sensor.Type = request.Type;
        sensor.Name = request.Name!.Trim();
        sensor.Enabled = request.Enabled;
        sensor.Ignore = request.Ignore;
        sensor.Interval = request.Interval;
        sensor.GracePeriod = request.GracePeriod;
        // Value checks keep their regular expression exactly as typed.
        sensor.Parameters = request.Type is SensorType.VALUECHECK or SensorType.HEARTBEAT_VALUECHECK
            ? request.Parameters
            : Text.Trimmed(request.Parameters);
        sensor.TextId = Text.Trimmed(request.TextId);
        // As on hosts: blank is null, otherwise exactly as typed (line breaks matter).
        sensor.Notes = Text.Trimmed(request.Notes) is null ? null : request.Notes;
        sensor.Importance = request.Importance;
    }
}
