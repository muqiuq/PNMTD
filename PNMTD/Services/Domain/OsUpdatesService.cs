using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.OsUpdates;

namespace PNMTD.Services.Domain;

/// <summary>
/// OS updates in the API (docs/linux-os-updates-plan.md §9): a Linux server's state, its runs and their logs, starting a run (checked here,
/// then by the agent, which may refuse), marking a failure handled, the server's switches, acknowledging high-risk findings (§6.5), the
/// dashboard's summary, and the advisory sources. Every change is audited; the names of people only with Monitoring's Details.
/// </summary>
public sealed class OsUpdatesService(
    PnmtdDbContext db,
    AgentHub hub,
    AgentKeyStore keys,
    IHttpContextAccessor accessor,
    IOptionsMonitor<OsUpdatesOptions> options,
    OsUpdateConditions conditions,
    OsAdvisoryNudge nudge,
    IServiceProvider services,
    ILogger<OsUpdatesService> logger)
{
    /// <summary>How long PNMTD waits for the agent's answer to a run: its checks simulate the upgrade first.</summary>
    private static readonly TimeSpan RunAnswer = TimeSpan.FromSeconds(90);

    // --- a server -------------------------------------------------------------------------------------------------------------

    public async Task<OsUpdatesView> ViewAsync(Guid id, bool details, CancellationToken ct)
    {
        var agent = await LinuxAgentAsync(id, ct);
        var o = options.CurrentValue;
        var section = await SectionAsync(id, ct);
        var rights = ServerRights.Read(agent.Rights);
        var refused = ServerRights.IsRefused(rights);
        var capable = agent.Capabilities.Contains(AgentCapabilities.OsUpdates);
        var connected = agent.Connected && hub.IsConnected(id);
        var now = Clock.UtcNow;
        var runs = await db.OsUpdateRuns.AsNoTracking().Where(r => r.SensorId == id).OrderByDescending(r => r.RequestedAt).Take(400).ToListAsync(ct);
        var current = runs.FirstOrDefault(r => r.State is OsUpdateState.Running or OsUpdateState.Requested);
        var lastUpdate = runs.FirstOrDefault(r => r.Kind == OsUpdateKind.Update && r.Finished);
        var lastUpgrade = runs.FirstOrDefault(r => r.Kind == OsUpdateKind.Upgrade && r.Finished);
        var findings = await db.OsUpdateFindings.AsNoTracking().Where(f => f.SensorId == id && f.ResolvedAt == null)
            .OrderByDescending(f => f.Rating).ThenBy(f => f.SourcePackage).ThenBy(f => f.Cve).ToListAsync(ct);
        var packages = await db.OsPendingPackages.AsNoTracking().Where(p => p.SensorId == id).OrderBy(p => p.Name).ToListAsync(ct);
        var distribution = agent.OsDistribution;
        var changelogs = distribution is null ? []
            : await db.OsPackageChangelogs.AsNoTracking().Where(c => c.Distribution == distribution)
                .Join(db.OsPendingPackages.Where(p => p.SensorId == id), c => new { S = c.SourcePackage, V = c.Version }, p => new { S = p.Source, V = p.SourceVersion },
                    (c, _) => new { c.SourcePackage, c.Version }).Distinct().ToListAsync(ct);
        var proxmox = agent.OsEdition == "proxmox" || (agent.Distribution?.Contains("Proxmox", StringComparison.OrdinalIgnoreCase) ?? false);
        var unavailable = !o.Enabled ? "OS updates are off in PNMT (OsUpdates__Enabled=false)."
            : !agent.OsUpdatesEnabled ? "OS updates are off for this server (its settings)."
            : !capable ? $"Needs agent {ServerInfo.Version} or newer on the server: pnmt-agent update."
            : refused ? "config.yaml is refused on the server: it grants nothing."
            : !connected ? "The server is offline."
            : null;
        return new OsUpdatesView(
            id, agent.Hostname, o.Enabled && agent.OsUpdatesEnabled, o.Advisories.Enabled, connected, capable, unavailable, rights?.OsUpdates, refused,
            distribution, proxmox, agent.ListsUpdatedAt, section?.NextUpdateAt?.UtcDateTime, o.DailyUpdate, o.Start, o.WindowLength,
            agent.PendingUpdates, agent.PendingSecurity,
            findings.Count(f => f.Rating == OsFindingRating.HighRisk && !f.IsAcknowledged(now)),
            findings.Count(f => f.Rating == OsFindingRating.HighRisk && f.IsAcknowledged(now)),
            findings.Count(f => f.Rating == OsFindingRating.Important),
            agent.DpkgInterrupted, section?.RebootRequired ?? false, section?.RebootPackages ?? [], section?.NeedsRestart, section?.Warnings ?? [],
            section?.PhasedDeferred ?? [], section?.Upgrade, section?.FullUpgrade, proxmox ? OsUpgradeMode.FullUpgrade : OsUpgradeMode.Upgrade,
            current is null ? null : Item(current, details), lastUpdate is null ? null : Item(lastUpdate, details), lastUpgrade is null ? null : Item(lastUpgrade, details),
            packages.Select(p =>
            {
                var mine = findings.Where(f => f.SourcePackage == p.Source).ToList();
                return new OsPendingPackageView(p.Name, p.Arch, p.Installed, p.Candidate, p.Source, p.SourceVersion, p.Origin, p.Security, p.InUpgrade, p.Phased,
                    p.FirstSeenAt, mine.Select(f => f.Cve).Distinct().ToList(), mine.Count == 0 ? null : mine.Max(f => f.Rating),
                    changelogs.Any(c => c.SourcePackage == p.Source && c.Version == p.SourceVersion));
            }).ToList(),
            findings.Select(f => Finding(f, details, now)).ToList(),
            new OsUpdateServerSettings(agent.OsUpdatesEnabled, agent.HighRiskEvents, agent.UpdateFailedEvents),
            agent.UpdateFailedMessage, agent.HighRiskMessage, Math.Clamp(o.UpgradeWarnMinutes, 1, 24 * 60), o.Advisories.MaxAcknowledgeDays);
    }

    public async Task<Paged<OsUpdateRunItem>> RunsAsync(Guid id, OsUpdateRunQuery query, bool details, CancellationToken ct)
    {
        await LinuxAgentAsync(id, ct);
        var runs = db.OsUpdateRuns.AsNoTracking().Where(r => r.SensorId == id);
        if (query.Kind is { } kind)
        {
            runs = runs.Where(r => r.Kind == kind);
        }
        var size = Math.Clamp(query.PageSize, 1, PageQuery.MaxPageSize);
        var page = Math.Max(1, query.Page);
        var total = await runs.CountAsync(ct);
        var rows = await runs.OrderByDescending(r => r.RequestedAt).ThenByDescending(r => r.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<OsUpdateRunItem>(rows.Select(r => Item(r, details)).ToList(), page, size, total);
    }

    public async Task<OsUpdateRunDetail> RunAsync(Guid id, Guid runId, bool details, CancellationToken ct)
    {
        var run = await db.OsUpdateRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.SensorId == id, ct) ?? throw ApiException.NotFound("Run");
        return new OsUpdateRunDetail(Item(run, details), OsUpdateRuns.ReadSummary(run.Summary));
    }

    public async Task<OsUpdateLogPart> LogAsync(Guid id, Guid runId, long from, CancellationToken ct)
    {
        var run = await db.OsUpdateRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.SensorId == id, ct) ?? throw ApiException.NotFound("Run");
        var (text, next) = await OsUpdateRuns.ReadLogAsync(db, runId, Math.Max(0, from), ct);
        return new OsUpdateLogPart(text, next, run.Finished, run.LogBytes, run.LogTruncated, run.LogIncomplete);
    }

    /// <summary>The whole log as a file: <c>web-01-2026-11-20-full-upgrade.log</c>.</summary>
    public async Task<(string FileName, string Text)> LogFileAsync(Guid id, Guid runId, CancellationToken ct)
    {
        var agent = await LinuxAgentAsync(id, ct);
        var run = await db.OsUpdateRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.SensorId == id, ct) ?? throw ApiException.NotFound("Run");
        var (text, _) = await OsUpdateRuns.ReadLogAsync(db, runId, 0, ct);
        var what = run.Kind == OsUpdateKind.Update ? "update" : run.Mode == OsUpgradeMode.FullUpgrade ? "full-upgrade" : "upgrade";
        var host = new string(agent.Hostname.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_').ToArray());
        return ($"{host}-{(run.StartedAt ?? run.RequestedAt):yyyy-MM-dd-HHmm}-{what}.log", text);
    }

    public async Task<OsPackageChangelogView> ChangelogAsync(Guid id, string package, CancellationToken ct)
    {
        var agent = await LinuxAgentAsync(id, ct);
        var pending = await db.OsPendingPackages.AsNoTracking().FirstOrDefaultAsync(p => p.SensorId == id && (p.Name == package || p.Source == package), ct)
                      ?? throw ApiException.NotFound("Pending package");
        var changelog = await db.OsPackageChangelogs.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Distribution == agent.OsDistribution && c.SourcePackage == pending.Source && c.Version == pending.SourceVersion, ct)
            ?? throw ApiException.NotFound("Changelog");
        var (text, cves, urgencies) = changelog.InstalledVersion == pending.InstalledSourceVersion || pending.InstalledSourceVersion is null
            ? (changelog.Text, changelog.Cves, changelog.Urgencies)
            : DebianChangelog.Since(changelog.Text, pending.InstalledSourceVersion);
        return new OsPackageChangelogView(changelog.SourcePackage, changelog.Version, pending.InstalledSourceVersion, cves, urgencies, text, changelog.FetchedFrom,
            changelog.ReceivedAt);
    }

    // --- starting a run (§5.1) ---------------------------------------------------------------------------------------------------

    public Task<OsUpdateStarted> UpdateAsync(Guid id, CancellationToken ct) => StartAsync(id, OsUpdateKind.Update, null, null, ct);

    public Task<OsUpdateStarted> UpgradeAsync(Guid id, OsUpdateUpgradeRequest request, CancellationToken ct) =>
        StartAsync(id, OsUpdateKind.Upgrade, request.Mode, request.RemovalsHash, ct);

    private async Task<OsUpdateStarted> StartAsync(Guid id, OsUpdateKind kind, OsUpgradeMode? mode, string? removalsHash, CancellationToken ct)
    {
        var agent = await LinuxAgentAsync(id, ct);
        var o = options.CurrentValue;
        if (!o.Enabled || !agent.OsUpdatesEnabled)
        {
            throw ApiException.Conflict("os_updates_off", !o.Enabled ? "OS updates are off in PNMT." : "OS updates are off for this server.");
        }
        if (!agent.Capabilities.Contains(AgentCapabilities.OsUpdates))
        {
            throw ApiException.Conflict("agent_outdated", $"The agent doesn't know OS updates yet: update it to {ServerInfo.Version} (pnmt-agent update).");
        }
        var rights = ServerRights.Read(agent.Rights);
        var granted = ServerRights.IsRefused(rights) ? null : rights?.OsUpdates;
        var allowed = kind == OsUpdateKind.Update ? granted?.Update : mode == OsUpgradeMode.FullUpgrade ? granted?.FullUpgrade : granted?.Upgrade;
        if (allowed != true)
        {
            var key = kind == OsUpdateKind.Update ? "update" : mode == OsUpgradeMode.FullUpgrade ? "full_upgrade" : "upgrade";
            throw ApiException.Conflict("refused_by_server", $"Not allowed on the server: os_updates.{key} in /etc/pnmt-agent/config.yaml.");
        }
        if (await db.OsUpdateRuns.AnyAsync(r => r.SensorId == id && (r.State == OsUpdateState.Requested || r.State == OsUpdateState.Running), ct))
        {
            throw ApiException.Conflict("run_in_progress", "A run is in progress on this server.");
        }
        var connection = hub.Require(id);
        var (userId, userName) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        var run = new OsUpdateRunEntity
        {
            Id = Guid.NewGuid(), SensorId = id, Kind = kind, Mode = kind == OsUpdateKind.Upgrade ? mode ?? OsUpgradeMode.Upgrade : null, Trigger = OsUpdateTrigger.Person,
            RequestedById = userId, RequestedByName = Cuts.CutOrNull(userName, 200), RequestedAt = now, State = OsUpdateState.Requested, LastHeardAt = now,
        };
        db.OsUpdateRuns.Add(run);
        var name = await AgentLog.NameAsync(db, id, ct);
        var section = kind == OsUpdateKind.Upgrade && run.Mode == OsUpgradeMode.FullUpgrade ? await SectionAsync(id, ct) : null;
        var removals = section?.FullUpgrade?.Removals ?? [];
        UdpViews.Audit(db, accessor, AuditEvents.OsUpdateRequested,
            $"{name}: {OsUpdateRuns.What(kind, run.Mode)}" + (removals.Count > 0 ? $", removing {string.Join(", ", removals.Take(20))}" : ""),
            AuditTarget.Sensor(id, name, agent.Sensor.ParentId));
        await db.SaveChangesAsync(ct);
        OsUpdateState state;
        string? message = null;
        try
        {
            var answer = await connection.RequestAsync(keys, AgentMessageTypes.OsUpdateRun,
                new OsUpdateRunRequest(run.Id, kind, run.Mode, userName, run.Mode == OsUpgradeMode.FullUpgrade ? removalsHash : null), RunAnswer, ct);
            var ack = AgentJson.Deserialize<Ack>(answer);
            state = ack?.Ok == true ? OsUpdateState.Running : OsUpdateState.Refused;
            message = ack?.Ok == true ? null : Cuts.CutOrNull(ack?.Error ?? "The server refused.", 2000);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            state = OsUpdateState.Lost;
            message = "The server didn't answer within 90 seconds.";
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or IOException)
        {
            state = OsUpdateState.Lost;
            message = $"The connection broke: {e.Message}";
        }
        await db.Entry(run).ReloadAsync(ct);
        if (state == OsUpdateState.Running)
        {
            if (run.State == OsUpdateState.Requested)
            {
                run.State = OsUpdateState.Running;
            }
            run.StartedAt ??= Clock.UtcNow;
        }
        else if (run.State == OsUpdateState.Requested)
        {
            run.State = state;
            run.Message = message;
            run.FinishedAt = Clock.UtcNow;
            AgentLog.Add(db, id, name, AgentLogKind.Request, $"OS updates: {OsUpdateRuns.What(kind, run.Mode)} for {userName}: {state.ToString().ToLowerInvariant()}. {message}");
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("OS updates on {Name}: {What} for {User}: {State}", name, OsUpdateRuns.What(kind, run.Mode), userName, run.State);
        return new OsUpdateStarted(run.Id, run.State, run.Message);
    }

    /// <summary>A failed upgrade marked handled: its 467 ends (§5.4).</summary>
    public async Task<OsUpdateRunItem> DismissAsync(Guid id, Guid runId, bool details, CancellationToken ct)
    {
        var agent = await LinuxAgentAsync(id, ct, tracked: true);
        var run = await db.OsUpdateRuns.SingleOrDefaultAsync(r => r.Id == runId && r.SensorId == id, ct) ?? throw ApiException.NotFound("Run");
        if (run.State != OsUpdateState.Failed || run.Kind != OsUpdateKind.Upgrade)
        {
            throw ApiException.Conflict("not_failed", "Only a failed upgrade can be marked handled.");
        }
        run.DismissedAt = Clock.UtcNow;
        run.DismissedByName = Cuts.CutOrNull(UdpViews.User(accessor).Name, 200);
        var name = await AgentLog.NameAsync(db, id, ct);
        UdpViews.Audit(db, accessor, AuditEvents.OsUpdateFailureDismissed,
            $"{name}: the failed {OsUpdateRuns.What(run.Kind, run.Mode)} of {(run.FinishedAt ?? run.RequestedAt):yyyy-MM-dd HH:mm} UTC marked handled",
            AuditTarget.Sensor(id, name, agent.Sensor.ParentId));
        await db.SaveChangesAsync(ct);
        await conditions.RefreshAsync(db, agent, ct);
        await db.SaveChangesAsync(ct);
        return Item(run, details);
    }

    public async Task<OsUpdatesView> SettingsAsync(Guid id, OsUpdateServerSettings request, bool details, CancellationToken ct)
    {
        var agent = await LinuxAgentAsync(id, ct, tracked: true);
        var changedSchedule = agent.OsUpdatesEnabled != request.Enabled;
        agent.OsUpdatesEnabled = request.Enabled;
        agent.HighRiskEvents = request.HighRiskEvents;
        agent.UpdateFailedEvents = request.UpdateFailedEvents;
        var name = await AgentLog.NameAsync(db, id, ct);
        UdpViews.Audit(db, accessor, AuditEvents.OsUpdateSettingsChanged,
            $"{name}: OS updates {On(request.Enabled)}, high-risk alerts {On(request.HighRiskEvents)}, update failures {On(request.UpdateFailedEvents)}",
            AuditTarget.Sensor(id, name, agent.Sensor.ParentId));
        await conditions.RefreshAsync(db, agent, ct);
        if (changedSchedule)
        {
            // The daily update follows the switch: a new configuration goes to this server.
            var settings = await keys.SettingsAsync(db, ct);
            settings.ConfigVersion++;
        }
        await db.SaveChangesAsync(ct);
        if (changedSchedule)
        {
            await hub.PushConfigAsync([id], ct);
        }
        nudge.Sensor(id);
        return await ViewAsync(id, details, ct);
    }

    private static string On(bool value) => value ? "on" : "off";

    // --- acknowledging (§6.5) ------------------------------------------------------------------------------------------------------

    public async Task<OsUpdatesView> AcknowledgeAsync(Guid id, OsFindingAcknowledgeRequest request, bool details, CancellationToken ct)
    {
        var max = options.CurrentValue.Advisories.MaxAcknowledgeDays;
        var reason = request.Reason?.Trim() ?? "";
        new ApiValidator()
            .Check(request.FindingIds is { Count: > 0 and <= 500 }, "findingIds", "One or more findings of this server.")
            .Check(reason.Length is > 0 and <= 500, "reason", "A reason is required, at most 500 characters.")
            .Check(request.Days >= 1 && request.Days <= max, "days", $"From 1 to {max} days (OsUpdates__Advisories__AcknowledgeMaxDays).")
            .ThrowIfInvalid();
        var agent = await LinuxAgentAsync(id, ct);
        var findings = await db.OsUpdateFindings.Where(f => f.SensorId == id && request.FindingIds.Contains(f.Id)).ToListAsync(ct);
        if (findings.Count != request.FindingIds.Distinct().Count())
        {
            throw ApiException.NotFound("Finding");
        }
        if (findings.FirstOrDefault(f => f.ResolvedAt is not null) is { } resolved)
        {
            throw ApiException.Conflict("finding_resolved", $"{resolved.Cve} in {resolved.SourcePackage} is resolved: its update was installed.");
        }
        if (findings.FirstOrDefault(f => f.Rating != OsFindingRating.HighRisk) is { } important)
        {
            throw ApiException.Conflict("not_high_risk", $"{important.Cve} in {important.SourcePackage} isn't high risk: only high-risk findings alert, so only they are acknowledged.");
        }
        var (userId, userName) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        var until = now.AddDays(request.Days);
        var name = await AgentLog.NameAsync(db, id, ct);
        foreach (var finding in findings)
        {
            var kinds = OsAdvisoryEvaluator.Reasons(finding.Reasons).Where(r => r.High).Select(r => r.Kind).Distinct().ToList();
            finding.AcknowledgedAt = now;
            finding.AcknowledgedById = userId;
            finding.AcknowledgedByName = Cuts.CutOrNull(userName, 200);
            finding.AcknowledgedReason = reason;
            finding.AcknowledgedUntil = until;
            finding.AcknowledgedKinds = kinds;
            finding.AcknowledgementEndedAt = null;
            finding.AcknowledgementEndedReason = null;
            UdpViews.Audit(db, accessor, AuditEvents.OsUpdateFindingAcknowledged,
                $"{name}: {finding.Cve} in {finding.SourcePackage} {finding.Candidate} acknowledged until {until:yyyy-MM-dd HH:mm} UTC ({string.Join(", ", kinds)}): {reason}",
                AuditTarget.Sensor(id, name, agent.Sensor.ParentId));
        }
        await db.SaveChangesAsync(ct);
        await RefreshHighRiskAsync(id, ct);
        return await ViewAsync(id, details, ct);
    }

    public async Task<OsUpdatesView> UnacknowledgeAsync(Guid id, Guid findingId, bool details, CancellationToken ct)
    {
        var agent = await LinuxAgentAsync(id, ct);
        var finding = await db.OsUpdateFindings.SingleOrDefaultAsync(f => f.Id == findingId && f.SensorId == id, ct) ?? throw ApiException.NotFound("Finding");
        if (finding.AcknowledgedAt is null)
        {
            return await ViewAsync(id, details, ct);
        }
        var name = await AgentLog.NameAsync(db, id, ct);
        UdpViews.Audit(db, accessor, AuditEvents.OsUpdateFindingAcknowledgementEnded,
            $"{name}: {finding.Cve} in {finding.SourcePackage}: the acknowledgement by {finding.AcknowledgedByName ?? "someone"} removed",
            AuditTarget.Sensor(id, name, agent.Sensor.ParentId));
        finding.AcknowledgedAt = null;
        finding.AcknowledgedById = null;
        finding.AcknowledgedByName = null;
        finding.AcknowledgedReason = null;
        finding.AcknowledgedUntil = null;
        finding.AcknowledgedKinds = [];
        finding.AcknowledgementEndedAt = Clock.UtcNow;
        finding.AcknowledgementEndedReason = $"removed by {Cuts.Cut(UdpViews.User(accessor).Name ?? "someone", 200)}";
        await db.SaveChangesAsync(ct);
        await RefreshHighRiskAsync(id, ct);
        return await ViewAsync(id, details, ct);
    }

    /// <summary>466's message at once, so the next heartbeat already follows the acknowledgement.</summary>
    private async Task RefreshHighRiskAsync(Guid id, CancellationToken ct)
    {
        var agent = await db.Agents.SingleAsync(a => a.SensorId == id, ct);
        var open = await db.OsUpdateFindings.Where(f => f.SensorId == id && f.ResolvedAt == null).ToListAsync(ct);
        agent.HighRiskMessage = options.CurrentValue is { Enabled: true, Advisories.Enabled: true }
            ? OsAdvisoryEvaluator.Message(agent.Hostname, open, Clock.UtcNow, options.CurrentValue.Advisories.HighRiskEpssPercentile)
            : null;
        await db.SaveChangesAsync(ct);
    }

    // --- the dashboard (§10.3) --------------------------------------------------------------------------------------------------

    public async Task<OsUpdatesSummary> SummaryAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var now = Clock.UtcNow;
        var agents = await db.Agents.AsNoTracking().Where(a => a.Platform == AgentPlatform.Linux && a.Sensor.Enabled && a.RevokedAt == null && a.UninstalledAt == null)
            .Select(a => new { a.SensorId, a.Sensor.Name, Host = a.Sensor.Parent.Name, a.Hostname, a.PendingUpdates, a.PendingSecurity, a.ListsUpdatedAt,
                a.UpdateFailedMessage, a.OsUpdatesEnabled, a.Capabilities })
            .ToListAsync(ct);
        var ids = agents.Select(a => a.SensorId).ToList();
        var findings = await db.OsUpdateFindings.AsNoTracking().Where(f => ids.Contains(f.SensorId) && f.ResolvedAt == null).ToListAsync(ct);
        var running = await db.OsUpdateRuns.AsNoTracking().Where(r => ids.Contains(r.SensorId) && (r.State == OsUpdateState.Running || r.State == OsUpdateState.Requested))
            .Select(r => r.SensorId).Distinct().ToListAsync(ct);
        var reboot = await RebootsAsync(ids, ct);
        var stale = TimeSpan.FromDays(2);
        var rows = agents.Select(a =>
        {
            var mine = findings.Where(f => f.SensorId == a.SensorId && f.Rating == OsFindingRating.HighRisk).ToList();
            var acknowledged = mine.Where(f => f.IsAcknowledged(now)).ToList();
            return new OsUpdatesServerRow(a.SensorId, a.Name, a.Host, a.PendingUpdates, a.PendingSecurity, o.Advisories.Enabled ? mine.Count - acknowledged.Count : 0,
                o.Advisories.Enabled ? acknowledged.Count : 0, acknowledged.Count > 0 ? acknowledged.Max(f => f.AcknowledgedUntil) : null, reboot.Contains(a.SensorId),
                a.OsUpdatesEnabled && a.ListsUpdatedAt is { } lists && now - lists > stale, a.ListsUpdatedAt, a.UpdateFailedMessage is not null,
                running.Contains(a.SensorId), a.UpdateFailedMessage);
        }).ToList();
        var ordered = rows
            .OrderByDescending(r => r.HighRisk > 0).ThenByDescending(r => r.Failed).ThenByDescending(r => r.HighRiskAcknowledged > 0)
            .ThenByDescending(r => r.Security).ThenByDescending(r => r.Pending).ThenBy(r => r.HostName, StringComparer.OrdinalIgnoreCase)
            .Where(r => r.Pending > 0 || r.Failed || r.ListsStale || r.Running || r.HighRisk > 0).ToList();
        var packages = await db.OsPendingPackages.AsNoTracking().Where(p => ids.Contains(p.SensorId) && !p.Phased)
            .Select(p => new { p.SensorId, p.Source, p.SourceVersion, p.Security }).ToListAsync(ct);
        var names = agents.ToDictionary(a => a.SensorId, a => a.Host);
        var byPackage = packages.GroupBy(p => (p.Source, p.SourceVersion)).Select(g =>
        {
            var servers = g.Select(p => p.SensorId).Distinct().ToList();
            var mine = o.Advisories.Enabled ? findings.Where(f => f.SourcePackage == g.Key.Source && f.Candidate == g.Key.SourceVersion && servers.Contains(f.SensorId)).ToList() : [];
            return new OsUpdatesPackageRow(g.Key.Source, g.Key.SourceVersion, servers.Count, servers.Select(s => names.GetValueOrDefault(s, "?")).Order().Take(50).ToList(),
                g.Any(p => p.Security), mine.Count == 0 ? null : mine.Max(f => f.Rating), mine.Select(f => f.Cve).Distinct().Order().Take(20).ToList());
        })
            .OrderByDescending(p => p.Rating == OsFindingRating.HighRisk).ThenByDescending(p => p.Security).ThenByDescending(p => p.ServerCount).ThenBy(p => p.Source)
            .Take(200).ToList();
        return new OsUpdatesSummary(o.Enabled, o.Advisories.Enabled, agents.Count, rows.Count(r => r.Pending > 0), rows.Sum(r => r.Pending), rows.Sum(r => r.Security),
            rows.Sum(r => r.HighRisk), rows.Sum(r => r.HighRiskAcknowledged), rows.Count(r => r.Failed), rows.Count(r => r.ListsStale), running.Count,
            ordered.Take(200).ToList(), byPackage);
    }

    /// <summary>The servers whose os-updates section says a reboot is required.</summary>
    private async Task<HashSet<Guid>> RebootsAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var sections = await db.AgentSections.AsNoTracking().Where(s => ids.Contains(s.SensorId) && s.Section == SectionNames.OsUpdates)
            .Select(s => new { s.SensorId, s.Content }).ToListAsync(ct);
        var result = new HashSet<Guid>();
        foreach (var section in sections)
        {
            if (AgentSections.Parse<OsUpdatesSection>(section.Content, out _) is { RebootRequired: true })
            {
                result.Add(section.SensorId);
            }
        }
        return result;
    }

    // --- the advisory sources (§6.1, §10.4) ----------------------------------------------------------------------------------------

    public async Task<OsAdvisorySourcesView> SourcesAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var sources = await db.OsAdvisorySources.AsNoTracking().OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync(ct);
        var now = Clock.UtcNow;
        var stale = TimeSpan.FromHours(Math.Clamp(o.Advisories.StaleHours, 1, 24 * 30));
        bool Fresh(OsAdvisorySourceEntity s) => s.LastSuccessAt is { } at && now - at <= stale;
        var (state, ok) = OsAdvisoryFetcher.State(sources.Where(s => s.Enabled).ToList(), now, stale);
        var roles = new[] { ("fixes", "What updates fix"), ("risk", "How dangerous a CVE is") }
            .Select(r => new OsAdvisoryRole(r.Item1, r.Item2,
                sources.Where(s => s.Enabled && OsAdvisorySourceDefaults.Role(s.Kind) == r.Item1 && Fresh(s)).Select(s => s.Name).ToList(),
                sources.Any(s => s.Enabled && OsAdvisorySourceDefaults.Role(s.Kind) == r.Item1 && Fresh(s))))
            .Append(new OsAdvisoryRole("changelogs", "Changelogs from the servers",
                await db.OsPackageChangelogs.AnyAsync(ct) ? ["the servers' changelogs"] : [], await db.OsPackageChangelogs.AnyAsync(ct)))
            .ToList();
        return new OsAdvisorySourcesView(o.Enabled && o.Advisories.Enabled, services.GetService<OsAdvisoryRating>()?.Enabled ?? false,
            sources.Select(s => SourceView(s, now, stale)).ToList(), roles, o.Advisories.HighRiskEpssPercentile, o.Advisories.ImportantEpssPercentile,
            o.Advisories.StaleHours, o.Advisories.AllowPrivateAddresses, state, ok, o.Advisories.MaxAcknowledgeDays);
    }

    private static OsAdvisorySourceView SourceView(OsAdvisorySourceEntity s, DateTime now, TimeSpan stale) => new(s.Id, s.Kind, OsAdvisorySourceDefaults.Role(s.Kind),
        s.Name, s.Url, s.Enabled, s.IntervalMinutes, s.CreatedAt, s.LastFetchAt, s.LastSuccessAt, s.Entries, s.Error,
        s.Enabled && (s.LastSuccessAt is not { } at || now - at > stale));

    public async Task<OsAdvisorySourceView> CreateSourceAsync(OsAdvisorySourceRequest request, CancellationToken ct)
    {
        Validate(request);
        var source = new OsAdvisorySourceEntity
        {
            Id = Guid.NewGuid(), Kind = request.Kind, Name = request.Name.Trim(), Url = request.Url.Trim(), Enabled = request.Enabled,
            IntervalMinutes = request.IntervalMinutes, CreatedAt = Clock.UtcNow,
        };
        db.OsAdvisorySources.Add(source);
        UdpViews.Audit(db, accessor, AuditEvents.OsAdvisorySourceCreated, $"{source.Name} ({source.Kind}): {source.Url}",
            AuditTarget.Of(AuditTargets.OsAdvisorySource, source.Id, source.Name));
        await db.SaveChangesAsync(ct);
        services.GetService<OsAdvisoryFetcher>()?.FetchNow(source.Id);
        return SourceView(source, Clock.UtcNow, TimeSpan.FromHours(options.CurrentValue.Advisories.StaleHours));
    }

    public async Task<OsAdvisorySourceView> UpdateSourceAsync(Guid sourceId, OsAdvisorySourceRequest request, CancellationToken ct)
    {
        Validate(request);
        var source = await db.OsAdvisorySources.SingleOrDefaultAsync(s => s.Id == sourceId, ct) ?? throw ApiException.NotFound("Source");
        if (source.Kind != request.Kind)
        {
            throw ApiException.Invalid("kind", "A source's kind can't change: add a new source.");
        }
        var urlChanged = source.Url != request.Url.Trim();
        source.Name = request.Name.Trim();
        source.Url = request.Url.Trim();
        source.Enabled = request.Enabled;
        source.IntervalMinutes = request.IntervalMinutes;
        if (urlChanged)
        {
            // Another place: what it has is fetched in full.
            source.ETag = null;
            source.LastModified = null;
            source.Cursor = null;
        }
        UdpViews.Audit(db, accessor, AuditEvents.OsAdvisorySourceChanged,
            $"{source.Name} ({source.Kind}): {(source.Enabled ? "on" : "off")}, every {source.IntervalMinutes} min, {source.Url}",
            AuditTarget.Of(AuditTargets.OsAdvisorySource, source.Id, source.Name));
        await db.SaveChangesAsync(ct);
        if (urlChanged || source.Enabled)
        {
            services.GetService<OsAdvisoryFetcher>()?.FetchNow(source.Id);
        }
        nudge.All();
        return SourceView(source, Clock.UtcNow, TimeSpan.FromHours(options.CurrentValue.Advisories.StaleHours));
    }

    public async Task DeleteSourceAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.OsAdvisorySources.SingleOrDefaultAsync(s => s.Id == sourceId, ct) ?? throw ApiException.NotFound("Source");
        db.OsAdvisorySources.Remove(source);
        UdpViews.Audit(db, accessor, AuditEvents.OsAdvisorySourceDeleted, $"{source.Name} ({source.Kind}): {source.Url}, with what it held",
            AuditTarget.Of(AuditTargets.OsAdvisorySource, source.Id, source.Name));
        await db.SaveChangesAsync(ct);
        nudge.All();
    }

    public async Task FetchSourceAsync(Guid sourceId, CancellationToken ct)
    {
        if (!await db.OsAdvisorySources.AnyAsync(s => s.Id == sourceId, ct))
        {
            throw ApiException.NotFound("Source");
        }
        services.GetService<OsAdvisoryFetcher>()?.FetchNow(sourceId);
    }

    private void Validate(OsAdvisorySourceRequest request)
    {
        var allow = options.CurrentValue.Advisories.AllowPrivateAddresses;
        var problem = OsAdvisorySourceDefaults.UrlProblem(request.Url?.Trim() ?? "", allow);
        new ApiValidator()
            .Check(Enum.IsDefined(request.Kind), "kind", "One of the source kinds.")
            .Check(request.Name?.Trim().Length is > 0 and <= 200, "name", "A name, at most 200 characters.")
            .Check(request.Url?.Length is > 0 and <= 1000 && problem is null, "url", problem ?? "A URL, at most 1000 characters.")
            .Check(request.IntervalMinutes is >= 5 and <= 7 * 24 * 60, "intervalMinutes", "From 5 minutes to 7 days.")
            .ThrowIfInvalid();
    }

    public async Task<Paged<OsAdvisoryItemView>> ItemsAsync(PageQuery query, CancellationToken ct)
    {
        var items = db.OsAdvisoryItems.AsNoTracking().Join(db.OsAdvisorySources, i => i.SourceId, s => s.Id, (i, s) => new { Item = i, s.Name });
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            items = items.Where(x => EF.Functions.ILike(x.Item.Title, $"%{search}%") || x.Item.Cves.Contains(search.ToUpper()));
        }
        var size = Math.Clamp(query.PageSize, 1, PageQuery.MaxPageSize);
        var page = Math.Max(1, query.Page);
        var total = await items.CountAsync(ct);
        var rows = await items.OrderByDescending(x => x.Item.FirstSeenAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<OsAdvisoryItemView>(rows.Select(x => new OsAdvisoryItemView(x.Item.Id, x.Item.SourceId, x.Name, x.Item.Title, x.Item.Link, x.Item.PublishedAt,
            x.Item.FirstSeenAt, x.Item.Cves, x.Item.Keywords, x.Item.AssessedBy, x.Item.AssessedAt, OsAdvisoryAssessment.Read(x.Item.Assessment))).ToList(), page, size, total);
    }

    // --- helpers ------------------------------------------------------------------------------------------------------------------

    private async Task<AgentEntity> LinuxAgentAsync(Guid id, CancellationToken ct, bool tracked = false)
    {
        var query = tracked ? db.Agents : db.Agents.AsNoTracking();
        var agent = await query.Include(a => a.Sensor).SingleOrDefaultAsync(a => a.SensorId == id, ct);
        return agent is { Platform: AgentPlatform.Linux } ? agent : throw ApiException.NotFound("Linux server");
    }

    private async Task<OsUpdatesSection?> SectionAsync(Guid id, CancellationToken ct)
    {
        var row = await db.AgentSections.AsNoTracking().SingleOrDefaultAsync(s => s.SensorId == id && s.Section == SectionNames.OsUpdates, ct);
        return row is null ? null : AgentSections.Parse<OsUpdatesSection>(row.Content, out _);
    }

    private OsUpdateRunItem Item(OsUpdateRunEntity r, bool details)
    {
        var summary = OsUpdateRuns.ReadSummary(r.Summary) ?? OsUpdateSummary.Empty;
        var warn = TimeSpan.FromMinutes(Math.Clamp(options.CurrentValue.UpgradeWarnMinutes, 1, 24 * 60));
        var takingLong = r.State == OsUpdateState.Running && r.Kind == OsUpdateKind.Upgrade && r.StartedAt is { } started && Clock.UtcNow - started > warn;
        var who = r.Trigger is OsUpdateTrigger.Person or OsUpdateTrigger.External ? details ? r.RequestedByName : r.RequestedByName is null ? null : "a person" : r.RequestedByName;
        return new OsUpdateRunItem(r.Id, r.Kind, r.Mode, r.Trigger, who, r.RequestedAt, r.StartedAt, r.FinishedAt, r.State, r.ExitCode, r.Message, r.Command, r.LogBytes,
            r.LogTruncated, r.LogIncomplete, takingLong, r.DismissedAt is not null,
            new OsUpdateCounts(summary.Upgraded.Count, summary.Installed.Count, summary.Removed.Count, summary.Warnings.Count, summary.Errors.Count,
                summary.ServicesToRestart.Count, summary.RebootRequired, summary.RepositoriesReached, summary.RepositoriesFailed, summary.ConfigFilesKept.Count),
            r.State == OsUpdateState.Running && Clock.UtcNow - (r.LastHeardAt ?? r.StartedAt ?? r.RequestedAt) > TimeSpan.FromMinutes(15));
    }

    private static OsFindingView Finding(OsUpdateFindingEntity f, bool details, DateTime now) => new(f.Id, f.SourcePackage, f.Cve, f.Packages, f.Installed, f.Candidate,
        f.Rating, OsAdvisoryEvaluator.Reasons(f.Reasons), f.FirstSeenAt,
        f.IsAcknowledged(now) ? new OsFindingAcknowledgement(details ? f.AcknowledgedByName : "a person", f.AcknowledgedReason ?? "", f.AcknowledgedAt!.Value, f.AcknowledgedUntil!.Value) : null,
        f.AcknowledgementEndedAt, f.AcknowledgementEndedReason);
}
