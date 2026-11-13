using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Windows;

namespace PNMTD.Services.Domain;

/// <summary>
/// Windows servers in the API (docs/windows-sensor-plan.md §9.2): the fleet, a server's agent, its switches, revoking
/// and re-joining, requests to the agent, the agent log, and join tokens.
/// </summary>
public sealed class WindowsServerService(
    PnmtdDbContext db,
    MonitoringReader reader,
    AgentHub hub,
    AgentKeyStore keys,
    PublicUrls urls,
    IHttpContextAccessor accessor,
    IOptionsMonitor<WindowsOptions> options,
    WindowsDownloads downloads)
{
    private static readonly string[] SortFields = ["name", "host", "state", "hostname", "lastseen", "os"];

    public async Task<Paged<WindowsServerItem>> ListAsync(WindowsServerQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "host", SortFields);
        var items = await ItemsAsync(query.HostId is { } host ? a => a.Sensor.ParentId == host : null, ct);
        var filtered = items
            .Where(i => query.State is not { } state || i.State == state)
            .Where(i => query.Unsupported is not { } u || !i.OsSupported == u)
            .Where(i => query.Outdated is not { } o || i.Outdated == o)
            .Where(i => query.Connected is not { } c || i.Connected == c)
            .Where(i => query.RemoteAccess is not { } r || !string.IsNullOrEmpty(i.RemoteAccess) == r)
            .Where(i => query.AuditGaps is not { } g || i.AuditGaps > 0 == g)
            .Where(i => query.DiagnosticsFailing is not { } d || i.DiagnosticsFailing > 0 == d)
            .Where(i => Text.Contains(i.Name, query.Search) || Text.Contains(i.Host.Name, query.Search) || Text.Contains(i.Hostname, query.Search)
                || Text.Contains(i.OsCaption, query.Search));
        IOrderedEnumerable<WindowsServerItem> sorted = field switch
        {
            "name" => filtered.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "state" => filtered.OrderBy(i => SensorService.StateOrder(i.State)),
            "hostname" => filtered.OrderBy(i => i.Hostname, StringComparer.OrdinalIgnoreCase),
            "lastseen" => filtered.OrderBy(i => i.LastSeenAt ?? DateTime.MinValue),
            "os" => filtered.OrderBy(i => i.OsCaption ?? "", StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(i => i.Host.Name, StringComparer.OrdinalIgnoreCase),
        };
        var list = (descending ? sorted.Reverse() : sorted.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return Paging.Page(list, query);
    }

    private async Task<List<WindowsServerItem>> ItemsAsync(System.Linq.Expressions.Expression<Func<WindowsAgentEntity, bool>>? filter, CancellationToken ct)
    {
        var query = db.WindowsAgents.AsNoTracking().Where(a => a.Sensor.Type == SensorType.WINDOWS);
        if (filter is not null)
        {
            query = query.Where(filter);
        }
        var agents = await query.Select(a => new { Agent = a, a.Sensor.Name, HostId = a.Sensor.ParentId, HostName = a.Sensor.Parent.Name }).ToListAsync(ct);
        var ids = agents.Select(a => a.Agent.SensorId).ToList();
        var snapshot = await reader.SnapshotAsync(s => ids.Contains(s.Id), ct);
        var extras = await WindowsExtras.LoadAsync(db, ids, ct);
        return agents.Select(a =>
        {
            var status = snapshot.Sensors.GetValueOrDefault(a.Agent.SensorId);
            return ToItem(a.Agent, a.Name, new Ref(a.HostId, a.HostName), status, extras);
        }).ToList();
    }

    private WindowsServerItem ToItem(WindowsAgentEntity a, string name, Ref host, SensorStatus? status, WindowsExtras extras) => new(
        a.SensorId, host, name, status?.State ?? SensorState.NoData, status?.LastCode, status?.LastMessage, a.Hostname, a.Domain, a.OsCaption, a.OsBuild,
        a.OsSupported, a.Architecture, a.AgentVersion, IsOutdated(a.AgentVersion), a.Connected && hub.IsConnected(a.SensorId), a.LastSeenAt, a.BootTime,
        a.ClockSkewSeconds, a.RevokedAt is not null, a.UninstalledAt is not null, a.LastProblemAt,
        extras.RemoteAccess.GetValueOrDefault(a.SensorId), extras.AuditGaps.GetValueOrDefault(a.SensorId), extras.DiagnosticsFailing.GetValueOrDefault(a.SensorId));

    /// <summary>An agent older than PNMTD (§10.1): the release of PNMTD's version is newer.</summary>
    public static bool IsOutdated(string? agentVersion) =>
        Version.TryParse(agentVersion?.Split('-', '+')[0], out var agent) && Version.TryParse(ServerInfo.Version.Split('-', '+')[0], out var pnmtd)
        && agent < pnmtd;

    public async Task<WindowsServerDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var agent = await AgentAsync(id, ct);
        var items = await ItemsAsync(a => a.SensorId == id, ct);
        var key = await keys.GetAsync(ct);
        var settings = await keys.SettingsAsync(db, ct);
        var problems = await db.WindowsAgentLog.AsNoTracking()
            .Where(l => l.SensorId == id && (l.Kind == WindowsAgentLogKind.Problem || l.Kind == WindowsAgentLogKind.Crashed))
            .OrderByDescending(l => l.At).Take(10).Select(LogItem).ToListAsync(ct);
        var same = agent.MachineGuid is { } machine
            ? await db.WindowsAgents.AsNoTracking().Where(a => a.MachineGuid == machine && a.SensorId != id)
                .Select(a => new Ref(a.SensorId, a.Sensor.Parent.Name + " / " + a.Sensor.Name)).ToListAsync(ct)
            : [];
        var sections = await WindowsExtras.SectionsAsync(db, id, ct);
        Heartbeat? heartbeat = null;
        if (agent.Heartbeat is { } json)
        {
            try
            {
                heartbeat = AgentJson.Deserialize<Heartbeat>(json);
            }
            catch (JsonException)
            {
            }
        }
        return new WindowsServerDetail(items[0], agent.AgentId, agent.KeyFingerprint, key.Fingerprint, agent.JoinedAt, agent.ConnectedAt,
            agent.DisconnectedAt, agent.RemoteAddress, agent.Fqdn, agent.OsEdition, agent.OsDisplayVersion, agent.MachineGuid, agent.Capabilities,
            agent.ConfigVersion, settings.ConfigVersion, heartbeat,
            new WindowsServerSettings(agent.AdminLoginEvents, agent.FailedLoginEvents, agent.FailedLoginThreshold, agent.CriticalEvents),
            problems, same, sections, agent.RevokedReason, agent.RevokedAt, agent.UninstalledAt, ServerInfo.Version);
    }

    public async Task<WindowsServerDetail> SettingsAsync(Guid id, WindowsServerSettings request, CancellationToken ct)
    {
        new ApiValidator()
            .Check(request.FailedLoginThreshold is >= 2 and <= 1000, "failedLoginThreshold", "Between 2 and 1000 failed logins.")
            .ThrowIfInvalid();
        var agent = await db.WindowsAgents.SingleOrDefaultAsync(a => a.SensorId == id, ct) ?? throw ApiException.NotFound("Windows server");
        agent.AdminLoginEvents = request.AdminLoginEvents;
        agent.FailedLoginEvents = request.FailedLoginEvents;
        agent.FailedLoginThreshold = request.FailedLoginThreshold;
        agent.CriticalEvents = request.CriticalEvents;
        UdpViews.Audit(db, accessor, AuthEvents.WindowsSettingsChanged,
            $"{await WindowsAgentLog.NameAsync(db, id, ct)}: admin logins {On(request.AdminLoginEvents)}, failed-login bursts {On(request.FailedLoginEvents)} " +
            $"({request.FailedLoginThreshold}), critical entries {On(request.CriticalEvents)}");
        await db.SaveChangesAsync(ct);
        return await DetailAsync(id, ct);
    }

    private static string On(bool value) => value ? "on" : "off";

    /// <summary>Revoke (§3.4): the key goes, the connection closes, the sensor and its history stay.</summary>
    public async Task<WindowsServerDetail> RevokeAsync(Guid id, WindowsRevokeRequest request, CancellationToken ct)
    {
        var agent = await db.WindowsAgents.SingleOrDefaultAsync(a => a.SensorId == id, ct) ?? throw ApiException.NotFound("Windows server");
        var reason = Text.Trimmed(request.Reason);
        new ApiValidator().Check(reason is null || reason.Length <= 500, "reason", "At most 500 characters.").ThrowIfInvalid();
        var name = await WindowsAgentLog.NameAsync(db, id, ct);
        agent.RevokedAt = Clock.UtcNow;
        agent.RevokedReason = reason;
        agent.PublicKey = null;
        agent.Connected = false;
        var (_, user) = UdpViews.User(accessor);
        WindowsAgentLog.Add(db, id, name, WindowsAgentLogKind.Revoked, $"Revoked by {user ?? "an admin"}{(reason is null ? "" : ": " + reason)}. Its key ({agent.KeyFingerprint}) is deleted.");
        UdpViews.Audit(db, accessor, AuthEvents.WindowsAgentRevoked, $"{name}: revoked{(reason is null ? "" : ": " + reason)}");
        await db.SaveChangesAsync(ct);
        await hub.DisconnectAsync(id, AgentCloseCodes.Revoked, $"revoked{(reason is null ? "" : ": " + reason)}");
        return await DetailAsync(id, ct);
    }

    public async Task<WindowsJoinTokenCreated> RejoinTokenAsync(Guid id, WindowsRejoinTokenRequest request, CancellationToken ct)
    {
        var sensor = await db.Sensors.AsNoTracking().Where(s => s.Id == id && s.Type == SensorType.WINDOWS).Select(s => new { s.ParentId, s.Name })
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Windows server");
        return await CreateTokenAsync(sensor.ParentId, sensor.Name, request.ValidHours, id, ct);
    }

    /// <summary>A section, typed (§9.2); access and event-log need CanWrite.</summary>
    public async Task<WindowsSectionView> SectionAsync(Guid id, string section, bool canWrite, CancellationToken ct)
    {
        await AgentAsync(id, ct);
        if (!SectionNames.IsKnown(section))
        {
            throw ApiException.NotFound("Section");
        }
        if (section is SectionNames.Access or SectionNames.EventLog && !canWrite)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Logins and the event log need the User role: they hold user names and addresses.");
        }
        var row = await db.WindowsSections.AsNoTracking().SingleOrDefaultAsync(s => s.SensorId == id && s.Section == section, ct)
            ?? throw ApiException.NotFound("Section (not reported yet)");
        var view = new WindowsSectionView(row.Section, row.CollectedAt, row.ReceivedAt, row.ChangedAt, row.Errors);
        return section switch
        {
            SectionNames.System => view with { System = WindowsSections.Parse<SystemSection>(row.Content) },
            SectionNames.Hardware => view with { Hardware = WindowsSections.Parse<HardwareSection>(row.Content) },
            SectionNames.Network => view with { Network = WindowsSections.Parse<NetworkSection>(row.Content) },
            SectionNames.Services => view with { Services = WindowsSections.Parse<ServicesSection>(row.Content) },
            SectionNames.RemoteAccess => view with { RemoteAccess = WindowsSections.Parse<RemoteAccessSection>(row.Content) },
            SectionNames.Access => view with { Access = WindowsSections.Parse<AccessSection>(row.Content) },
            SectionNames.EventLog => view with { EventLog = WindowsSections.Parse<EventLogSection>(row.Content) },
            _ => view,
        };
    }

    /// <summary>Logins and connections (§4.7), newest first. CanWrite: they hold user names and addresses.</summary>
    public async Task<Paged<WindowsLoginItem>> LoginsAsync(Guid id, WindowsLoginQuery query, CancellationToken ct)
    {
        await AgentAsync(id, ct);
        var q = db.WindowsLogins.AsNoTracking().Where(l => l.SensorId == id);
        if (query.Outcome is { } outcome)
        {
            q = q.Where(l => l.Outcome == outcome);
        }
        if (query.Admin is { } admin)
        {
            q = q.Where(l => l.IsAdmin == admin);
        }
        if (query.Kind is { } kind)
        {
            q = q.Where(l => l.Kind == kind);
        }
        if (Text.Pattern(query.User) is { } user)
        {
            q = q.Where(l => EF.Functions.ILike(l.User, user) || EF.Functions.ILike(l.Domain ?? "", user));
        }
        if (Text.Pattern(query.Source) is { } source)
        {
            q = q.Where(l => EF.Functions.ILike(l.SourceAddress ?? "", source) || EF.Functions.ILike(l.Workstation ?? "", source));
        }
        if (Text.Pattern(query.Search) is { } search)
        {
            q = q.Where(l => EF.Functions.ILike(l.User, search) || EF.Functions.ILike(l.SourceAddress ?? "", search) || EF.Functions.ILike(l.Workstation ?? "", search));
        }
        if (query.From is { } from)
        {
            q = q.Where(l => (l.LastAt ?? l.At) >= from.ToUniversalTime());
        }
        if (query.To is { } to)
        {
            q = q.Where(l => l.At <= to.ToUniversalTime());
        }
        return await Paging.PageAsync(q.OrderByDescending(l => l.LastAt ?? l.At).Select(l => new WindowsLoginItem(l.Id, l.At, l.LastAt, l.Count, l.Outcome,
            l.FailureReason, l.User, l.Domain, l.IsAdmin, l.Kind, l.AuthPackage, l.SourceAddress, l.SourcePort, l.Workstation, l.Elevated, l.LoggedOffAt, l.Channel)),
            query, ct);
    }

    /// <summary>The readable changes (§4.9), newest first.</summary>
    public async Task<Paged<WindowsChangeItem>> ChangesAsync(Guid id, WindowsChangeQuery query, CancellationToken ct)
    {
        await AgentAsync(id, ct);
        var q = db.WindowsChanges.AsNoTracking().Where(c => c.SensorId == id);
        if (!string.IsNullOrWhiteSpace(query.Section))
        {
            q = q.Where(c => c.Section == query.Section);
        }
        if (query.Notable is { } notable)
        {
            q = q.Where(c => c.Notable == notable);
        }
        if (Text.Pattern(query.Search) is { } search)
        {
            q = q.Where(c => EF.Functions.ILike(c.Summary, search));
        }
        return await Paging.PageAsync(q.OrderByDescending(c => c.At).ThenBy(c => c.Summary)
            .Select(c => new WindowsChangeItem(c.Id, c.Section, c.At, c.Summary, c.Notable, c.Details)), query, ct);
    }

    /// <summary>Asks the agent to collect sections now (§6): answered when the agent acknowledged it.</summary>
    public async Task<WindowsRequestResult> CollectAsync(Guid id, WindowsCollectRequest request, CancellationToken ct)
    {
        await AgentAsync(id, ct);
        var sections = (request.Sections ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        new ApiValidator().Check(sections.Count <= 20 && sections.All(s => s.Length <= 40), "sections", "At most 20 section names.").ThrowIfInvalid();
        var answer = await RequestAsync(id, AgentMessageTypes.Collect, new CollectRequest(sections), TimeSpan.FromSeconds(30), ct);
        var ack = AgentJson.Deserialize<Ack>(answer);
        return new WindowsRequestResult(ack?.Ok ?? false, ack?.Error);
    }

    /// <summary>The agent's last 500 log lines (§3.8). Admin.</summary>
    public async Task<WindowsAgentLogFile> SendLogAsync(Guid id, CancellationToken ct)
    {
        await AgentAsync(id, ct);
        var answer = await RequestAsync(id, AgentMessageTypes.SendLog, new SendLogRequest(500), TimeSpan.FromSeconds(30), ct);
        if (answer.TryGetProperty("lines", out _) && AgentJson.Deserialize<LogLines>(answer) is { } lines)
        {
            return new WindowsAgentLogFile(Clock.UtcNow, lines.Lines.Take(500).Select(l => l.Length > 4000 ? l[..4000] : l).ToList());
        }
        var ack = AgentJson.Deserialize<Ack>(answer);
        throw ApiException.Conflict("agent_refused", $"The agent refused: {ack?.Error ?? "no reason given"}.");
    }

    private async Task<JsonElement> RequestAsync(Guid id, string type, object body, TimeSpan timeout, CancellationToken ct)
    {
        var connection = hub.Connection(id) ?? throw ApiException.Conflict("agent_offline", "The agent is not connected: try again when it is.");
        try
        {
            return await connection.RequestAsync(keys, type, body, timeout, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw ApiException.Conflict("agent_timeout", $"The agent did not answer within {(int)timeout.TotalSeconds} s.");
        }
    }

    public async Task<Paged<WindowsAgentLogItem>> LogAsync(Guid? sensorId, WindowsAgentLogQuery query, CancellationToken ct)
    {
        var q = db.WindowsAgentLog.AsNoTracking();
        if ((sensorId ?? query.SensorId) is { } id)
        {
            q = q.Where(l => l.SensorId == id);
        }
        if (query.Kind is { } kind)
        {
            q = q.Where(l => l.Kind == kind);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            q = q.Where(l => EF.Functions.ILike(l.Message, pattern) || EF.Functions.ILike(l.AgentName, pattern));
        }
        return await Paging.PageAsync(q.OrderByDescending(l => l.At).Select(LogItem), query, ct);
    }

    private static readonly System.Linq.Expressions.Expression<Func<WindowsAgentLogEntity, WindowsAgentLogItem>> LogItem =
        l => new WindowsAgentLogItem(l.Id, l.SensorId, l.AgentName, l.At, l.Kind, l.Message, l.Details, l.Address);

    // --- join tokens (§3.3) ----------------------------------------------------------------------------------

    public async Task<Paged<WindowsJoinTokenItem>> TokensAsync(WindowsJoinTokenQuery query, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var q = db.WindowsJoinTokens.AsNoTracking();
        q = query.State switch
        {
            WindowsJoinTokenState.Pending => q.Where(t => t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt >= now),
            WindowsJoinTokenState.Used => q.Where(t => t.UsedAt != null),
            WindowsJoinTokenState.Revoked => q.Where(t => t.UsedAt == null && t.RevokedAt != null),
            WindowsJoinTokenState.Expired => q.Where(t => t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt < now),
            _ => q,
        };
        var page = await Paging.PageAsync(q.OrderByDescending(t => t.CreatedAt), query, ct);
        var hostIds = page.Items.Select(t => t.HostId).Distinct().ToList();
        var sensorIds = page.Items.SelectMany(t => new[] { t.SensorId, t.ResultSensorId }).OfType<Guid>().Distinct().ToList();
        var hosts = await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
        var sensors = await db.Sensors.AsNoTracking().Where(s => sensorIds.Contains(s.Id)).Select(s => new { s.Id, Name = s.Parent.Name + " / " + s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return new Paged<WindowsJoinTokenItem>(page.Items.Select(t => ToItem(t, hosts, sensors, now)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    private static WindowsJoinTokenItem ToItem(WindowsJoinTokenEntity t, IReadOnlyDictionary<Guid, string> hosts, IReadOnlyDictionary<Guid, string> sensors, DateTime now) =>
        new(t.Id, t.Prefix, new Ref(t.HostId, hosts.GetValueOrDefault(t.HostId, "?")), t.Name,
            t.SensorId is { } s && sensors.TryGetValue(s, out var sn) ? new Ref(s, sn) : null, t.CreatedAt, t.CreatedByName, t.ExpiresAt, t.UsedAt,
            t.UsedFromAddress, t.ResultSensorId is { } r && sensors.TryGetValue(r, out var rn) ? new Ref(r, rn) : null, t.RevokedAt,
            t.UsedAt is not null ? WindowsJoinTokenState.Used : t.RevokedAt is not null ? WindowsJoinTokenState.Revoked
                : t.ExpiresAt < now ? WindowsJoinTokenState.Expired : WindowsJoinTokenState.Pending);

    public Task<WindowsJoinTokenCreated> CreateTokenAsync(WindowsJoinTokenRequest request, CancellationToken ct) =>
        CreateTokenAsync(request.HostId, request.Name, request.ValidHours, null, ct);

    private async Task<WindowsJoinTokenCreated> CreateTokenAsync(Guid hostId, string? name, int? validHours, Guid? rejoin, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var hours = validHours ?? Math.Clamp(settings.TokenHours, 1, 168);
        name = Text.Trimmed(name);
        var v = new ApiValidator()
            .Check(hours is >= 1 and <= 168, "validHours", "Between 1 and 168 hours (7 days).")
            .Check(name is null || name.Length <= 200, "name", "At most 200 characters.");
        var host = await db.Hosts.AsNoTracking().Where(h => h.Id == hostId).Select(h => new { h.Id, h.Name }).SingleOrDefaultAsync(ct);
        v.Check(host is not null, "hostId", "No such host: a server is added to an existing host.");
        v.ThrowIfInvalid();
        if (!settings.Enabled)
        {
            throw ApiException.Conflict("windows_disabled", "Windows agents are turned off (Windows__Enabled).");
        }

        var token = JoinTokens.Generate();
        var (userId, userName) = UdpViews.User(accessor);
        var entity = new WindowsJoinTokenEntity
        {
            Id = Guid.NewGuid(),
            TokenHash = JoinTokens.Hash(token),
            Prefix = JoinTokens.Prefix(token),
            HostId = host!.Id,
            SensorId = rejoin,
            Name = rejoin is null ? name : null,
            CreatedAt = Clock.UtcNow,
            CreatedBy = userId,
            CreatedByName = userName,
            ExpiresAt = Clock.UtcNow.AddHours(hours),
        };
        db.WindowsJoinTokens.Add(entity);
        var what = rejoin is null ? $"host {host.Name}{(name is null ? "" : $", name {name}")}" : $"re-join of {host.Name} / {name}";
        UdpViews.Audit(db, accessor, AuthEvents.WindowsJoinTokenCreated, $"token {entity.Prefix}… for {what}, valid {hours} h");
        await db.SaveChangesAsync(ct);

        var key = await keys.GetAsync(ct);
        var url = urls.Base;
        var downloadInfo = await downloads.GetAsync(ct, wait: false);
        // Plain http:// works after a confirmation: on the command line, --allow-http.
        var plainHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var command = $"pnmt-sensor-x64.exe join --url {url} --token {token}{(entity.Name is { } n ? $" --name \"{n.Replace("\"", "")}\"" : "")}{(plainHttp ? " --allow-http" : "")}";
        var sensors = rejoin is { } r ? new Dictionary<Guid, string> { [r] = $"{host.Name} / {name}" } : new Dictionary<Guid, string>();
        return new WindowsJoinTokenCreated(ToItem(entity, new Dictionary<Guid, string> { [host.Id] = host.Name }, sensors, Clock.UtcNow),
            token, url, key.Fingerprint, command, downloadInfo);
    }

    public async Task RevokeTokenAsync(Guid id, CancellationToken ct)
    {
        var token = await db.WindowsJoinTokens.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw ApiException.NotFound("Join token");
        if (token.UsedAt is not null)
        {
            throw ApiException.Conflict("token_used", "This token was used: revoke the server instead.");
        }
        if (token.RevokedAt is null)
        {
            token.RevokedAt = Clock.UtcNow;
            UdpViews.Audit(db, accessor, AuthEvents.WindowsJoinTokenRevoked, $"token {token.Prefix}… revoked");
            await db.SaveChangesAsync(ct);
        }
    }

    public Task<WindowsAgentDownloads> DownloadsAsync(CancellationToken ct) => downloads.GetAsync(ct, wait: true);

    private async Task<WindowsAgentEntity> AgentAsync(Guid id, CancellationToken ct) =>
        await db.WindowsAgents.AsNoTracking().SingleOrDefaultAsync(a => a.SensorId == id && a.Sensor.Type == SensorType.WINDOWS, ct)
        ?? throw ApiException.NotFound("Windows server");
}

/// <summary>What the list shows beyond the agent row: filled in by the later phases (remote access, audit gaps, diagnostics).</summary>
public sealed record WindowsExtras(
    IReadOnlyDictionary<Guid, string> RemoteAccess,
    IReadOnlyDictionary<Guid, int> AuditGaps,
    IReadOnlyDictionary<Guid, int> DiagnosticsFailing)
{
    public static async Task<WindowsExtras> LoadAsync(PnmtdDbContext db, IReadOnlyCollection<Guid> sensorIds, CancellationToken ct)
    {
        var agents = await db.WindowsAgents.AsNoTracking().Where(a => sensorIds.Contains(a.SensorId))
            .Select(a => new { a.SensorId, a.RemoteAccessSummary, a.AuditGaps }).ToListAsync(ct);
        // Diagnostics failing: assignments whose last finished run did not pass (§10.1).
        var lastRuns = await db.DiagnosticRuns.AsNoTracking().Where(r => r.Outcome != null)
            .Join(db.DiagnosticAssignments.Where(a => sensorIds.Contains(a.TargetSensorId)), r => r.AssignmentId, a => a.Id, (r, a) => new { r.AssignmentId, a.TargetSensorId, r.StartedAt, r.Outcome })
            .GroupBy(x => x.AssignmentId).Select(g => g.OrderByDescending(x => x.StartedAt).First()).ToListAsync(ct);
        return new WindowsExtras(
            agents.Where(a => a.RemoteAccessSummary != null).ToDictionary(a => a.SensorId, a => a.RemoteAccessSummary!),
            agents.ToDictionary(a => a.SensorId, a => a.AuditGaps),
            lastRuns.Where(r => r.Outcome != Lib.Agent.DiagnosticOutcome.Pass).GroupBy(r => r.TargetSensorId).ToDictionary(g => g.Key, g => g.Count()));
    }

    public static async Task<IReadOnlyList<WindowsSectionInfo>> SectionsAsync(PnmtdDbContext db, Guid sensorId, CancellationToken ct) =>
        await db.WindowsSections.AsNoTracking().Where(s => s.SensorId == sensorId).OrderBy(s => s.Section)
            .Select(s => new WindowsSectionInfo(s.Section, s.CollectedAt, s.ReceivedAt, s.Errors)).ToListAsync(ct);
}
