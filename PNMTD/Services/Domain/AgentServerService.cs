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
/// Servers with an agent in the API (docs/windows-sensor-plan.md §9.2; docs/linux-sensor-plan.md §10), of both platforms:
/// the fleet, a server's agent, its switches, revoking and re-joining, requests to the agent, the agent log, join tokens and
/// the downloads; a Windows server's typed sections and WMI, which stay under /api/v1/windows/, and a Linux server's typed
/// sections under /api/v1/linux/.
/// </summary>
public sealed class AgentServerService(
    PnmtdDbContext db,
    MonitoringReader reader,
    AgentHub hub,
    AgentKeyStore keys,
    PublicUrls urls,
    IHttpContextAccessor accessor,
    IOptionsMonitor<AgentOptions> options,
    AgentDownloads downloads,
    ILogger<AgentServerService> logger)
{
    private static readonly string[] SortFields = ["name", "host", "state", "hostname", "lastseen", "os"];

    public async Task<Paged<AgentServerItem>> ListAsync(AgentServerQuery query, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "host", SortFields);
        var items = await ItemsAsync(query.HostId is { } host ? a => a.Sensor.ParentId == host : null, ct);
        var filtered = items
            .Where(i => query.Platform is not { } platform || i.Platform == platform)
            .Where(i => query.State is not { } state || i.State == state)
            .Where(i => query.Unsupported is not { } u || !i.OsSupported == u)
            .Where(i => query.Outdated is not { } o || i.Outdated == o)
            .Where(i => query.Connected is not { } c || i.Connected == c)
            .Where(i => query.RemoteAccess is not { } r || !string.IsNullOrEmpty(i.RemoteAccess) == r)
            .Where(i => query.AuditGaps is not { } g || i.AuditGaps > 0 == g)
            .Where(i => query.DiagnosticsFailing is not { } d || i.DiagnosticsFailing > 0 == d)
            .Where(i => query.SecurityUpdates is not { } u || (i.SecurityUpdates ?? 0) > 0 == u)
            .Where(i => query.KeyRemoval is not { } k || (i.KeyRemovalAllowed ?? false) == k)
            .Where(i => query.UnregisteredKeys is not { } uk || (i.UnregisteredKeys ?? 0) > 0 == uk)
            .Where(i => query.RetiredKeys is not { } rk || (i.RetiredKeysAuthorized ?? 0) > 0 == rk)
            .Where(i => Text.Contains(i.Name, query.Search) || Text.Contains(i.Host.Name, query.Search) || Text.Contains(i.Hostname, query.Search)
                || Text.Contains(i.OsCaption, query.Search) || Text.Contains(i.Distribution, query.Search));
        IOrderedEnumerable<AgentServerItem> sorted = field switch
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

    private async Task<List<AgentServerItem>> ItemsAsync(System.Linq.Expressions.Expression<Func<AgentEntity, bool>>? filter, CancellationToken ct)
    {
        var query = db.Agents.AsNoTracking().Where(a => AgentPlatformInfo.SensorTypes.Contains(a.Sensor.Type));
        if (filter is not null)
        {
            query = query.Where(filter);
        }
        var agents = await query.Select(a => new { Agent = a, a.Sensor.Name, HostId = a.Sensor.ParentId, HostName = a.Sensor.Parent.Name }).ToListAsync(ct);
        var ids = agents.Select(a => a.Agent.SensorId).ToList();
        var snapshot = await reader.SnapshotAsync(s => ids.Contains(s.Id), ct);
        var extras = await AgentExtras.LoadAsync(db, ids, ct);
        var chosen = await ChosenAsync(ct);
        return agents.Select(a =>
        {
            var status = snapshot.Sensors.GetValueOrDefault(a.Agent.SensorId);
            return ToItem(a.Agent, a.Name, new Ref(a.HostId, a.HostName), status, extras, chosen);
        }).ToList();
    }

    /// <summary>The versions the platforms' agents get (docs/agent-update-plan.md §4), as <see cref="AgentVersionService.ChosenAsync"/> reads them.</summary>
    private async Task<AgentChosenVersions> ChosenAsync(CancellationToken ct)
    {
        var settings = await db.AgentSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == AgentSettingsEntity.SingletonId, ct);
        var chosen = AgentVersionService.Resolve(settings?.WindowsAgentVersion, settings?.LinuxAgentVersion);
        downloads.Remember(chosen);
        return chosen;
    }

    private AgentServerItem ToItem(AgentEntity a, string name, Ref host, SensorStatus? status, AgentExtras extras, AgentChosenVersions chosen)
    {
        var target = chosen.For(a.Platform);
        var linux = a.Platform == AgentPlatform.Linux;
        var rights = linux ? ServerRights.Read(a.Rights) : null;
        return new(
            a.SensorId, host, name, a.Platform, status?.State ?? SensorState.NoData, status?.LastCode, status?.LastMessage, a.Hostname, a.Domain, a.OsCaption, a.OsBuild,
            a.OsSupported, a.Architecture, a.AgentVersion, AgentVersionService.IsOutdated(a.AgentVersion, target), a.Connected && hub.IsConnected(a.SensorId),
            a.LastSeenAt, a.BootTime,
            a.ClockSkewSeconds, a.RevokedAt is not null, a.UninstalledAt is not null, a.LastProblemAt,
            extras.RemoteAccess.GetValueOrDefault(a.SensorId), extras.AuditGaps.GetValueOrDefault(a.SensorId), extras.DiagnosticsFailing.GetValueOrDefault(a.SensorId),
            // Linux (docs/linux-sensor-plan.md §10): for the fleet's columns and filters.
            linux ? a.Distribution ?? a.OsCaption : null, linux ? a.KernelRelease ?? a.OsBuild : null, linux ? a.SecurityUpdates : null,
            linux ? rights is not null && rights.KeyRemoval && !ServerRights.IsRefused(rights) : null,
            linux ? extras.UnregisteredKeys.GetValueOrDefault(a.SensorId) : null, linux ? extras.RetiredKeys.GetValueOrDefault(a.SensorId) : null,
            // Outdated (docs/agent-update-plan.md §4): older than the version its platform's agents get; ahead: newer than it.
            target, AgentVersionService.IsAhead(a.AgentVersion, target));
    }

    public async Task<AgentServerDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var agent = await AgentAsync(id, ct);
        var items = await ItemsAsync(a => a.SensorId == id, ct);
        var key = await keys.GetAsync(ct);
        var settings = await keys.SettingsAsync(db, ct);
        var problems = await db.AgentLog.AsNoTracking()
            .Where(l => l.SensorId == id && (l.Kind == AgentLogKind.Problem || l.Kind == AgentLogKind.Crashed))
            .OrderByDescending(l => l.At).Take(10).Select(LogItem).ToListAsync(ct);
        var same = agent.MachineId is { } machine
            ? await db.Agents.AsNoTracking().Where(a => a.MachineId == machine && a.SensorId != id)
                .Select(a => new Ref(a.SensorId, a.Sensor.Parent.Name + " / " + a.Sensor.Name)).ToListAsync(ct)
            : [];
        var sections = await AgentExtras.SectionsAsync(db, id, ct);
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
        return new AgentServerDetail(items[0], agent.AgentId, agent.KeyFingerprint, key.Fingerprint, agent.JoinedAt, agent.ConnectedAt,
            agent.DisconnectedAt, agent.RemoteAddress, agent.Fqdn, agent.OsEdition, agent.OsDisplayVersion, agent.MachineId, agent.Capabilities,
            agent.ConfigVersion, settings.ConfigVersion, heartbeat,
            new AgentServerSettings(agent.AdminLoginEvents, agent.FailedLoginEvents, agent.FailedLoginThreshold, agent.CriticalEvents,
                agent.KeyAddedEvents, agent.SecurityUpdateEvents, agent.SecurityUpdateDays),
            problems, same, sections, agent.RevokedReason, agent.RevokedAt, agent.UninstalledAt, ServerInfo.Version,
            ServerRights.Read(agent.Rights), agent.Platform == AgentPlatform.Linux ? agent.SecurityUpdatesSince : null);
    }

    /// <summary>The switches (§4.7, §4.8); a Linux server's own too (docs/linux-sensor-plan.md §11.3). Audited.</summary>
    public async Task<AgentServerDetail> SettingsAsync(Guid id, AgentServerSettings request, CancellationToken ct)
    {
        new ApiValidator()
            .Check(request.FailedLoginThreshold is >= 2 and <= 1000, "failedLoginThreshold", "Between 2 and 1000 failed logins.")
            .Check(request.SecurityUpdateDays is >= 1 and <= 365, "securityUpdateDays", "Between 1 and 365 days.")
            .ThrowIfInvalid();
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.SensorId == id, ct) ?? throw ApiException.NotFound("Server");
        agent.AdminLoginEvents = request.AdminLoginEvents;
        agent.FailedLoginEvents = request.FailedLoginEvents;
        agent.FailedLoginThreshold = request.FailedLoginThreshold;
        agent.CriticalEvents = request.CriticalEvents;
        var linux = "";
        if (agent.Platform == AgentPlatform.Linux)
        {
            agent.KeyAddedEvents = request.KeyAddedEvents;
            agent.SecurityUpdateEvents = request.SecurityUpdateEvents;
            agent.SecurityUpdateDays = request.SecurityUpdateDays;
            linux = $", unregistered keys {On(request.KeyAddedEvents)}, security updates {On(request.SecurityUpdateEvents)} ({request.SecurityUpdateDays} days)";
        }
        UdpViews.Audit(db, accessor, AuditEvents.AgentSettingsChanged,
            $"{await AgentLog.NameAsync(db, id, ct)}: admin logins {On(request.AdminLoginEvents)}, failed-login bursts {On(request.FailedLoginEvents)} " +
            $"({request.FailedLoginThreshold}), critical entries {On(request.CriticalEvents)}{linux}");
        await db.SaveChangesAsync(ct);
        return await DetailAsync(id, ct);
    }

    private static string On(bool value) => value ? "on" : "off";

    // --- WMI (§16), Windows' own -------------------------------------------------------------------------------------

    /// <summary>A Windows server's WMI settings, and its queries and own queries' rows as the agent last reported them.</summary>
    public async Task<WindowsWmiView> WmiAsync(Guid id, CancellationToken ct)
    {
        var agent = await db.Agents.AsNoTracking().Where(a => a.SensorId == id && a.Platform == AgentPlatform.Windows).Select(a => new { a.WmiSettings })
            .SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Windows server");
        var row = await db.AgentSections.AsNoTracking().SingleOrDefaultAsync(s => s.SensorId == id && s.Section == SectionNames.Wmi, ct);
        return new WindowsWmiView(WindowsWmi.Settings(agent.WmiSettings), row?.CollectedAt, row is null ? null : AgentSections.Parse<WmiSection>(row.Content),
            hub.IsConnected(id));
    }

    /// <summary>
    /// Saves a server's WMI settings and sends the agent its new configuration: built-in queries off or with another timeout, and
    /// own read-only queries (checked here and on the agent). Audited.
    /// </summary>
    public async Task<WindowsWmiView> SaveWmiAsync(Guid id, WmiConfig request, CancellationToken ct)
    {
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.SensorId == id && a.Platform == AgentPlatform.Windows, ct)
            ?? throw ApiException.NotFound("Windows server");
        var overrides = (request.Overrides ?? []).Where(o => !o.Enabled || o.TimeoutSeconds is not null)
            .Select(o => o with { Id = o.Id?.Trim() ?? "" }).ToList();
        var custom = (request.Custom ?? []).Select(c => c with
        {
            Id = c.Id == Guid.Empty ? Guid.NewGuid() : c.Id, Name = c.Name?.Trim() ?? "", Namespace = c.Namespace?.Trim() ?? "", Wql = c.Wql?.Trim() ?? "",
        }).ToList();
        var v = new ApiValidator()
            .Check(overrides.Count <= 200, "overrides", "At most 200 settings.")
            .Check(overrides.All(o => o.Id.Length is >= 3 and <= 300 && o.Id.Contains(':')), "overrides", "A query's id is its namespace and class, root\\cimv2:Win32_Service.")
            .Check(overrides.All(o => o.TimeoutSeconds is null or (>= 1 and <= 600)), "overrides", "A timeout between 1 and 600 seconds.")
            .Check(overrides.Select(o => o.Id.ToLowerInvariant()).Distinct().Count() == overrides.Count, "overrides", "Each query once.")
            .Check(custom.Count <= WmiQueries.MaxCustom, "custom", $"At most {WmiQueries.MaxCustom} own queries.")
            .Check(custom.Select(c => c.Id).Distinct().Count() == custom.Count, "custom", "Each own query once.");
        for (var i = 0; i < custom.Count; i++)
        {
            var c = custom[i];
            v.Check(c.Name.Length is >= 1 and <= 100, $"custom.{i}.name", "A name of 1 to 100 characters.")
                .Check(custom.Count(o => string.Equals(o.Name, c.Name, StringComparison.OrdinalIgnoreCase)) == 1, $"custom.{i}.name", "Another own query has this name.")
                .Check(c.IntervalMinutes is >= 1 and <= 1440, $"custom.{i}.intervalMinutes", "Every 1 to 1440 minutes.")
                .Check(c.TimeoutSeconds is >= 1 and <= 600, $"custom.{i}.timeoutSeconds", "A timeout between 1 and 600 seconds.");
            if (WmiQueries.Problem(c.Namespace, c.Wql) is { } problem)
            {
                v.Check(false, $"custom.{i}.wql", problem);
            }
        }
        v.ThrowIfInvalid();
        var settings = new WmiConfig(overrides, custom);
        var before = WindowsWmi.Settings(agent.WmiSettings);
        agent.WmiSettings = overrides.Count == 0 && custom.Count == 0 ? null : AgentJson.Serialize(settings);
        UdpViews.Audit(db, accessor, AuditEvents.WindowsSettingsChanged,
            $"{await AgentLog.NameAsync(db, id, ct)}: WMI {overrides.Count(o => !o.Enabled)} built-in queries off, {overrides.Count(o => o.TimeoutSeconds is not null)} " +
            $"with another timeout, {custom.Count} own queries ({string.Join(", ", custom.Select(c => $"{c.Name}: {c.Namespace} {c.Wql}"))})" +
            (before.Custom.Count == custom.Count && before.Overrides.Count == overrides.Count ? "" : $"; before {before.Overrides.Count} settings, {before.Custom.Count} own"));
        var keySettings = await keys.SettingsAsync(db, ct);
        keySettings.ConfigVersion++;
        await db.SaveChangesAsync(ct);
        await hub.PushConfigAsync([id], ct);
        return await WmiAsync(id, ct);
    }

    /// <summary>Revoke (§3.4): the key goes, the connection closes, the sensor and its history stay.</summary>
    public async Task<AgentServerDetail> RevokeAsync(Guid id, AgentRevokeRequest request, CancellationToken ct)
    {
        var agent = await db.Agents.SingleOrDefaultAsync(a => a.SensorId == id, ct) ?? throw ApiException.NotFound("Server");
        var reason = Text.Trimmed(request.Reason);
        new ApiValidator().Check(reason is null || reason.Length <= 500, "reason", "At most 500 characters.").ThrowIfInvalid();
        var name = await AgentLog.NameAsync(db, id, ct);
        agent.RevokedAt = Clock.UtcNow;
        agent.RevokedReason = reason;
        agent.PublicKey = null;
        agent.Connected = false;
        var (_, user) = UdpViews.User(accessor);
        AgentLog.Add(db, id, name, AgentLogKind.Revoked, $"Revoked by {user ?? "an admin"}{(reason is null ? "" : ": " + reason)}. Its key ({agent.KeyFingerprint}) is deleted.");
        UdpViews.Audit(db, accessor, AuditEvents.AgentRevoked, $"{name}: revoked{(reason is null ? "" : ": " + reason)}");
        await db.SaveChangesAsync(ct);
        await hub.DisconnectAsync(id, AgentCloseCodes.Revoked, $"revoked{(reason is null ? "" : ": " + reason)}");
        return await DetailAsync(id, ct);
    }

    /// <summary>A re-join token is for the server's own platform: the agent that replaces the key must be of it.</summary>
    public async Task<AgentJoinTokenCreated> RejoinTokenAsync(Guid id, AgentRejoinTokenRequest request, CancellationToken ct)
    {
        var agent = await db.Agents.AsNoTracking().Where(a => a.SensorId == id && AgentPlatformInfo.SensorTypes.Contains(a.Sensor.Type))
            .Select(a => new { a.Sensor.ParentId, a.Sensor.Name, a.Platform }).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("Server");
        return await CreateTokenAsync(agent.Platform, agent.ParentId, agent.Name, request.ValidHours, id, ct);
    }

    /// <summary>A Windows server's section, typed (§9.2); access and event-log need CanWrite. Linux's typed sections come with L3.</summary>
    public async Task<WindowsSectionView> SectionAsync(Guid id, string section, bool canWrite, CancellationToken ct)
    {
        if ((await AgentAsync(id, ct)).Platform != AgentPlatform.Windows)
        {
            throw ApiException.NotFound("Windows server");
        }
        if (!SectionNames.IsKnown(AgentPlatform.Windows, section))
        {
            throw ApiException.NotFound("Section");
        }
        if (section == SectionNames.Scripts)
        {
            // Script texts and accounts: Admin only, through …/local-scripts.
            throw ApiException.NotFound("Section (the registered scripts: GET /api/v1/windows/servers/{id}/local-scripts, Admin)");
        }
        if (section == SectionNames.Wmi)
        {
            // Own queries' rows may hold anything WMI knows: Admin only, through …/wmi.
            throw ApiException.NotFound("Section (WMI: GET /api/v1/windows/servers/{id}/wmi, Admin)");
        }
        if (section is SectionNames.Access or SectionNames.EventLog && !canWrite)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Logins and the event log need the User role: they hold user names and addresses.");
        }
        var row = await db.AgentSections.AsNoTracking().SingleOrDefaultAsync(s => s.SensorId == id && s.Section == section, ct)
            ?? throw ApiException.NotFound("Section (not reported yet)");
        var view = new WindowsSectionView(row.Section, row.CollectedAt, row.ReceivedAt, row.ChangedAt, row.Errors);
        // A section PNMT can't read shows why (the JSON path), instead of looking collected and empty.
        string? error = null;
        T? Read<T>() where T : class => AgentSections.Parse<T>(row.Content, out error);
        view = section switch
        {
            SectionNames.System => view with { System = Read<SystemSection>() },
            SectionNames.Hardware => view with { Hardware = Read<HardwareSection>() },
            SectionNames.Network => view with { Network = Read<NetworkSection>() },
            SectionNames.Services => view with { Services = Read<ServicesSection>() },
            SectionNames.RemoteAccess => view with { RemoteAccess = Read<RemoteAccessSection>() },
            SectionNames.Access => view with { Access = Read<AccessSection>() },
            SectionNames.EventLog => view with { EventLog = Read<EventLogSection>() },
            _ => view,
        };
        if (error is not null)
        {
            logger.LogWarning("Windows server {Id}: the section {Section} could not be read: {Error}", id, section, error);
            view = view with { Errors = [error, .. view.Errors] };
        }
        return view;
    }

    /// <summary>
    /// A Linux server's section, typed by Linux's records (docs/linux-sensor-plan.md §10): 404 for a Windows server, as Windows'
    /// sections are for a Linux one. access, journal and ssh-keys need CanWrite: user names, addresses, key comments.
    /// </summary>
    public async Task<LinuxSectionView> LinuxSectionAsync(Guid id, string section, bool canWrite, CancellationToken ct)
    {
        if ((await AgentAsync(id, ct)).Platform != AgentPlatform.Linux)
        {
            throw ApiException.NotFound("Linux server");
        }
        if (!SectionNames.IsKnown(AgentPlatform.Linux, section))
        {
            throw ApiException.NotFound("Section");
        }
        if (section is SectionNames.Access or SectionNames.Journal or SectionNames.SshKeys && !canWrite)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden",
                "Logins, the journal and the SSH keys need the User role: they hold user names, addresses and key comments.");
        }
        var row = await db.AgentSections.AsNoTracking().SingleOrDefaultAsync(s => s.SensorId == id && s.Section == section, ct)
            ?? throw ApiException.NotFound("Section (not reported yet)");
        var view = new LinuxSectionView(row.Section, row.CollectedAt, row.ReceivedAt, row.ChangedAt, row.Errors);
        string? error = null;
        T? Read<T>() where T : class => AgentSections.Parse<T>(row.Content, out error);
        view = section switch
        {
            SectionNames.System => view with { System = Read<LinuxSystemSection>() },
            SectionNames.Hardware => view with { Hardware = Read<HardwareSection>() },
            SectionNames.Network => view with { Network = Read<NetworkSection>() },
            SectionNames.Services => view with { Services = Read<LinuxServicesSection>() },
            SectionNames.RemoteAccess => view with { RemoteAccess = Read<LinuxRemoteAccessSection>() },
            SectionNames.Access => view with { Access = Read<LinuxAccessSection>() },
            SectionNames.Journal => view with { Journal = Read<JournalSection>() },
            SectionNames.SshKeys => view with { SshKeys = Read<SshKeysSection>() },
            _ => view,
        };
        if (error is not null)
        {
            logger.LogWarning("Linux server {Id}: the section {Section} could not be read: {Error}", id, section, error);
            view = view with { Errors = [error, .. view.Errors] };
        }
        return view;
    }

    /// <summary>Logins and connections (§4.7), newest first. CanWrite: they hold user names and addresses.</summary>
    public async Task<Paged<AgentLoginItem>> LoginsAsync(Guid id, AgentLoginQuery query, CancellationToken ct)
    {
        await AgentAsync(id, ct);
        var q = db.AgentLogins.AsNoTracking().Where(l => l.SensorId == id);
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
        if (!string.IsNullOrWhiteSpace(query.KeyFingerprint))
        {
            var fingerprint = SshKeys.NormalizeFingerprint(query.KeyFingerprint) ?? query.KeyFingerprint.Trim();
            q = q.Where(l => l.KeyFingerprint == fingerprint);
        }
        if (!string.IsNullOrWhiteSpace(query.KeyOwner))
        {
            // docs/linux-sensor-plan.md §10: by the key's registered owner (a user's id, or a text), joined by the fingerprint's value.
            var owned = await SshKeyManager.SshKeyRegistry.OwnedBy(db, Guid.TryParse(query.KeyOwner, out var keyOwner) ? keyOwner : null, Text.Pattern(query.KeyOwner))
                .ToListAsync(ct);
            q = q.Where(l => l.KeyFingerprint != null && owned.Contains(l.KeyFingerprint));
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
        var page = await Paging.PageAsync(q.OrderByDescending(l => l.LastAt ?? l.At).Select(l => new AgentLoginItem(l.Id, l.At, l.LastAt, l.Count, l.Outcome,
            l.FailureReason, l.User, l.Domain, l.IsAdmin, l.Kind, l.AuthPackage, l.SourceAddress, l.SourcePort, l.Workstation, l.Elevated, l.LoggedOffAt, l.Channel,
            l.KeyFingerprint, l.TargetUser, l.Command, l.TriedUsers, null, null, null)),
            query, ct);
        // The keys' names (docs/linux-sensor-plan.md §6.4), for a key registered now: past logins are named too.
        var names = await SshKeyManager.SshKeyRegistry.ByFingerprintAsync(db, page.Items.Select(l => l.KeyFingerprint).OfType<string>(), ct);
        page = names.Count == 0 ? page : page with
        {
            Items = page.Items.Select(l => l.KeyFingerprint is { } f && names.TryGetValue(f, out var key) ? l with { KeyName = key.Name, KeyOwner = key.Owner, KeyId = key.Id } : l)
                .ToList(),
        };
        return await WithReviewsAsync(db, page, ct);
    }

    /// <summary>Each login's review by the access watch (docs/cookie-access-plan.md §10.3): its verdict and case, for the <i>Access</i> tab's badge.</summary>
    public static async Task<Paged<AgentLoginItem>> WithReviewsAsync(PnmtdDbContext db, Paged<AgentLoginItem> page, CancellationToken ct)
    {
        var ids = page.Items.Select(l => l.Id).ToList();
        var reviews = await db.AccessReviews.AsNoTracking().Where(r => r.AgentLoginId != null && ids.Contains(r.AgentLoginId.Value))
            .Select(r => new { r.Id, LoginId = r.AgentLoginId!.Value, r.CaseId, r.State }).ToListAsync(ct);
        if (reviews.Count == 0)
        {
            return page;
        }
        var byLogin = reviews.GroupBy(r => r.LoginId).ToDictionary(g => g.Key, g => g.First());
        return page with
        {
            Items = page.Items.Select(l => byLogin.TryGetValue(l.Id, out var r) ? l with { Review = new AccessReviewRef(r.Id, r.CaseId, r.State) } : l).ToList(),
        };
    }

    /// <summary>The readable changes (§4.9), newest first.</summary>
    public async Task<Paged<AgentChangeItem>> ChangesAsync(Guid id, AgentChangeQuery query, CancellationToken ct)
    {
        await AgentAsync(id, ct);
        var q = db.AgentChanges.AsNoTracking().Where(c => c.SensorId == id);
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
            .Select(c => new AgentChangeItem(c.Id, c.Section, c.At, c.Summary, c.Notable, c.Details)), query, ct);
    }

    /// <summary>Asks the agent to collect sections now (§6): answered when the agent acknowledged it.</summary>
    public async Task<AgentRequestResult> CollectAsync(Guid id, AgentCollectRequest request, CancellationToken ct)
    {
        await AgentAsync(id, ct);
        var sections = (request.Sections ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        new ApiValidator().Check(sections.Count <= 20 && sections.All(s => s.Length <= 40), "sections", "At most 20 section names.").ThrowIfInvalid();
        var answer = await RequestAsync(id, AgentMessageTypes.Collect, new CollectRequest(sections), TimeSpan.FromSeconds(30), ct);
        var ack = AgentJson.Deserialize<Ack>(answer);
        return new AgentRequestResult(ack?.Ok ?? false, ack?.Error);
    }

    /// <summary>The agent's last 500 log lines (§3.8). Admin.</summary>
    public async Task<AgentLogFile> SendLogAsync(Guid id, CancellationToken ct)
    {
        await AgentAsync(id, ct);
        var answer = await RequestAsync(id, AgentMessageTypes.SendLog, new SendLogRequest(500), TimeSpan.FromSeconds(30), ct);
        if (answer.TryGetProperty("lines", out _) && AgentJson.Deserialize<LogLines>(answer) is { } lines)
        {
            return new AgentLogFile(Clock.UtcNow, lines.Lines.Take(500).Select(l => l.Length > 4000 ? l[..4000] : l).ToList());
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

    public async Task<Paged<AgentLogItem>> LogAsync(Guid? sensorId, AgentLogQuery query, CancellationToken ct)
    {
        var q = db.AgentLog.AsNoTracking();
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

    private static readonly System.Linq.Expressions.Expression<Func<AgentLogEntity, AgentLogItem>> LogItem =
        l => new AgentLogItem(l.Id, l.SensorId, l.AgentName, l.At, l.Kind, l.Message, l.Details, l.Address);

    // --- join tokens (§3.3) ----------------------------------------------------------------------------------

    public async Task<Paged<AgentJoinTokenItem>> TokensAsync(AgentJoinTokenQuery query, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var q = db.AgentJoinTokens.AsNoTracking();
        q = query.State switch
        {
            AgentJoinTokenState.Pending => q.Where(t => t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt >= now),
            AgentJoinTokenState.Used => q.Where(t => t.UsedAt != null),
            AgentJoinTokenState.Revoked => q.Where(t => t.UsedAt == null && t.RevokedAt != null),
            AgentJoinTokenState.Expired => q.Where(t => t.UsedAt == null && t.RevokedAt == null && t.ExpiresAt < now),
            _ => q,
        };
        var page = await Paging.PageAsync(q.OrderByDescending(t => t.CreatedAt), query, ct);
        var hostIds = page.Items.Select(t => t.HostId).Distinct().ToList();
        var sensorIds = page.Items.SelectMany(t => new[] { t.SensorId, t.ResultSensorId }).OfType<Guid>().Distinct().ToList();
        var hosts = await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
        var sensors = await db.Sensors.AsNoTracking().Where(s => sensorIds.Contains(s.Id)).Select(s => new { s.Id, Name = s.Parent.Name + " / " + s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return new Paged<AgentJoinTokenItem>(page.Items.Select(t => ToItem(t, hosts, sensors, now)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    private static AgentJoinTokenItem ToItem(AgentJoinTokenEntity t, IReadOnlyDictionary<Guid, string> hosts, IReadOnlyDictionary<Guid, string> sensors, DateTime now) =>
        new(t.Id, t.Platform, t.Prefix, new Ref(t.HostId, hosts.GetValueOrDefault(t.HostId, "?")), t.Name,
            t.SensorId is { } s && sensors.TryGetValue(s, out var sn) ? new Ref(s, sn) : null, t.CreatedAt, t.CreatedByName, t.ExpiresAt, t.UsedAt,
            t.UsedFromAddress, t.ResultSensorId is { } r && sensors.TryGetValue(r, out var rn) ? new Ref(r, rn) : null, t.RevokedAt,
            t.UsedAt is not null ? AgentJoinTokenState.Used : t.RevokedAt is not null ? AgentJoinTokenState.Revoked
                : t.ExpiresAt < now ? AgentJoinTokenState.Expired : AgentJoinTokenState.Pending);

    public Task<AgentJoinTokenCreated> CreateTokenAsync(AgentJoinTokenRequest request, CancellationToken ct) =>
        CreateTokenAsync(request.Platform, request.HostId, request.Name, request.ValidHours, null, ct);

    private async Task<AgentJoinTokenCreated> CreateTokenAsync(AgentPlatform platform, Guid hostId, string? name, int? validHours, Guid? rejoin,
        CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var hours = validHours ?? Math.Clamp(settings.TokenHours, 1, 168);
        name = Text.Trimmed(name);
        var v = new ApiValidator()
            .Check(Enum.IsDefined(platform), "platform", "Windows or Linux.")
            .Check(hours is >= 1 and <= 168, "validHours", "Between 1 and 168 hours (7 days).")
            .Check(name is null || name.Length <= 200, "name", "At most 200 characters.");
        var host = await db.Hosts.AsNoTracking().Where(h => h.Id == hostId).Select(h => new { h.Id, h.Name }).SingleOrDefaultAsync(ct);
        v.Check(host is not null, "hostId", "No such host: a server is added to an existing host.");
        v.ThrowIfInvalid();
        if (!settings.Enabled)
        {
            throw ApiException.Conflict("agents_disabled", "The agents are turned off (Agents__Enabled).");
        }

        var token = JoinTokens.Generate();
        var (userId, userName) = UdpViews.User(accessor);
        var entity = new AgentJoinTokenEntity
        {
            Id = Guid.NewGuid(),
            Platform = platform,
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
        db.AgentJoinTokens.Add(entity);
        var what = rejoin is null ? $"host {host.Name}{(name is null ? "" : $", name {name}")}" : $"re-join of {host.Name} / {name}";
        UdpViews.Audit(db, accessor, AuditEvents.AgentJoinTokenCreated, $"{platform} token {entity.Prefix}… for {what}, valid {hours} h");
        await db.SaveChangesAsync(ct);

        var key = await keys.GetAsync(ct);
        var url = urls.Base;
        var chosen = await ChosenAsync(ct);
        if (platform == AgentPlatform.Linux && settings.ServeDownloads)
        {
            // docs/linux-sensor-plan.md §4.4: the dialog asks ahead, so the line's download finds the files here.
            downloads.FetchAhead(chosen.Linux, AgentDownloads.Of(AgentPlatform.Linux));
        }
        // A Linux token's line needs the binary's hash: the release's is waited for (at most 5 s), as the Download tab does.
        var downloadInfo = await downloads.GetAsync(chosen, url, ct, wait: platform == AgentPlatform.Linux);
        // Plain http:// works after a confirmation: on the command line, --allow-http.
        var plainHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var command = platform == AgentPlatform.Linux ? LinuxCommandLine(downloadInfo, url, token)
            : $"pnmt-sensor-x64.exe join --url {url} --token {token}{(entity.Name is { } n ? $" --name \"{n.Replace("\"", "")}\"" : "")}{(plainHttp ? " --allow-http" : "")}";
        var sensors = rejoin is { } r ? new Dictionary<Guid, string> { [r] = $"{host.Name} / {name}" } : new Dictionary<Guid, string>();
        return new AgentJoinTokenCreated(ToItem(entity, new Dictionary<Guid, string> { [host.Id] = host.Name }, sensors, Clock.UtcNow),
            token, url, key.Fingerprint, command, downloadInfo);
    }

    public async Task RevokeTokenAsync(Guid id, CancellationToken ct)
    {
        var token = await db.AgentJoinTokens.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw ApiException.NotFound("Join token");
        if (token.UsedAt is not null)
        {
            throw ApiException.Conflict("token_used", "This token was used: revoke the server instead.");
        }
        if (token.RevokedAt is null)
        {
            token.RevokedAt = Clock.UtcNow;
            UdpViews.Audit(db, accessor, AuditEvents.AgentJoinTokenRevoked, $"token {token.Prefix}… revoked");
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// A Linux token's one line (docs/linux-sensor-plan.md §4.4): x64, from PNMT (GitHub when PNMT doesn't serve the files), with wget,
    /// the token in it; only the join while the binary's hash isn't known.
    /// </summary>
    private static string LinuxCommandLine(AgentDownloadLinks downloads, string url, string token)
    {
        var x64 = downloads.Linux.FirstOrDefault(l => l.Architecture == "x64");
        var line = x64?.Pnmt ?? x64?.GitHub;
        return line is null ? AgentDownloads.JoinLine(url, token) : line.Replace(AgentDownloads.TokenPlaceholder, token, StringComparison.Ordinal);
    }

    public async Task<AgentDownloadLinks> DownloadsAsync(CancellationToken ct) => await downloads.GetAsync(await ChosenAsync(ct), urls.Base, ct, wait: true);

    /// <summary>
    /// <i>Fetch again</i> (docs/linux-sensor-plan.md §4.4): the files named (or every one that isn't here and verified) from the
    /// release, replacing those that don't match. Answers at once, with the files being fetched. Audited.
    /// </summary>
    public async Task<AgentDownloadLinks> FetchDownloadsAsync(AgentDownloadFetchRequest request, CancellationToken ct)
    {
        var names = (request.Files ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct().ToList();
        new ApiValidator()
            .Check(names.All(AgentDownloadAssets.Executables.Contains), "files", "Only the agents' files: " + string.Join(", ", AgentDownloadAssets.Executables) + ".")
            .ThrowIfInvalid();
        if (!options.CurrentValue.ServeDownloads || !options.CurrentValue.Enabled)
        {
            throw ApiException.Conflict("downloads_off", "PNMT doesn't serve the agents' files (Agents__ServeDownloads, Agents__Enabled): GitHub's links remain.");
        }
        var chosen = await ChosenAsync(ct);
        string VersionOf(string file) => chosen.For(AgentDownloadAssets.PlatformOf(file));
        var files = names.Count > 0 ? names : AgentDownloadAssets.Executables.Where(e => downloads.Check(VersionOf(e), e).File is null).ToList();
        foreach (var group in files.GroupBy(VersionOf))
        {
            downloads.FetchAhead(group.Key, group, again: true);
        }
        UdpViews.Audit(db, accessor, AuditEvents.AgentDownloadsFetched,
            files.Count == 0 ? "fetch again: every file is here and verified" : $"fetch again: {string.Join(", ", files.Select(f => $"{f} ({VersionOf(f)})"))}");
        await db.SaveChangesAsync(ct);
        return await downloads.GetAsync(chosen, urls.Base, ct, wait: false);
    }

    private async Task<AgentEntity> AgentAsync(Guid id, CancellationToken ct) =>
        await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.SensorId == id && AgentPlatformInfo.SensorTypes.Contains(a.Sensor.Type), ct)
        ?? throw ApiException.NotFound("Server");
}

/// <summary>
/// What the list shows beyond the agent row: filled in by the later phases (remote access, audit gaps, diagnostics; on Linux the
/// keys found and not registered, and the retired keys still authorized, by fingerprint).
/// </summary>
public sealed record AgentExtras(
    IReadOnlyDictionary<Guid, string> RemoteAccess,
    IReadOnlyDictionary<Guid, int> AuditGaps,
    IReadOnlyDictionary<Guid, int> DiagnosticsFailing,
    IReadOnlyDictionary<Guid, int> UnregisteredKeys,
    IReadOnlyDictionary<Guid, int> RetiredKeys)
{
    public static async Task<AgentExtras> LoadAsync(PnmtdDbContext db, IReadOnlyCollection<Guid> sensorIds, CancellationToken ct)
    {
        var agents = await db.Agents.AsNoTracking().Where(a => sensorIds.Contains(a.SensorId))
            .Select(a => new { a.SensorId, a.RemoteAccessSummary, a.AuditGaps }).ToListAsync(ct);
        // Diagnostics failing: assignments whose last finished run did not pass (§10.1).
        var lastRuns = await db.DiagnosticRuns.AsNoTracking().Where(r => r.Outcome != null)
            .Join(db.DiagnosticAssignments.Where(a => sensorIds.Contains(a.TargetSensorId)), r => r.AssignmentId, a => a.Id, (r, a) => new { r.AssignmentId, a.TargetSensorId, r.StartedAt, r.Outcome })
            .GroupBy(x => x.AssignmentId).Select(g => g.OrderByDescending(x => x.StartedAt).First()).ToListAsync(ct);
        // docs/linux-sensor-plan.md §6.3, §11.1: each server's keys by their registration, joined by the fingerprint's value.
        var keys = await db.SshKeyAuthorizations.AsNoTracking().Where(a => sensorIds.Contains(a.SensorId))
            .Select(a => new { a.SensorId, a.Fingerprint, State = db.SshKeys.Where(k => k.Fingerprint == a.Fingerprint).Select(k => (SshKeyState?)k.State).FirstOrDefault() })
            .Distinct().ToListAsync(ct);
        return new AgentExtras(
            agents.Where(a => a.RemoteAccessSummary != null).ToDictionary(a => a.SensorId, a => a.RemoteAccessSummary!),
            agents.ToDictionary(a => a.SensorId, a => a.AuditGaps),
            lastRuns.Where(r => r.Outcome != Lib.Agent.DiagnosticOutcome.Pass).GroupBy(r => r.TargetSensorId).ToDictionary(g => g.Key, g => g.Count()),
            keys.Where(k => k.State is null).GroupBy(k => k.SensorId).ToDictionary(g => g.Key, g => g.Count()),
            keys.Where(k => k.State == SshKeyState.Retired).GroupBy(k => k.SensorId).ToDictionary(g => g.Key, g => g.Count()));
    }

    public static async Task<IReadOnlyList<AgentSectionInfo>> SectionsAsync(PnmtdDbContext db, Guid sensorId, CancellationToken ct) =>
        await db.AgentSections.AsNoTracking().Where(s => s.SensorId == sensorId).OrderBy(s => s.Section)
            .Select(s => new AgentSectionInfo(s.Section, s.CollectedAt, s.ReceivedAt, s.Errors)).ToListAsync(ct);
}
