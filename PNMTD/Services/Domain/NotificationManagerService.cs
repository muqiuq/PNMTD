using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

/// <summary>
/// The Notification Manager (docs/ai-operations-plan.md §11): which rules cover which sensors, through links, scopes
/// and exclusions; assignments in bulk; the sensors no rule covers; and a rule's scopes with their preview.
/// </summary>
public sealed class NotificationManagerService(PnmtdDbContext db, MonitoringReader reader)
{
    public const int MaxScopes = 20;

    /// <summary>"importance ≥ High", "host web-01", "everything".</summary>
    public static string Label(NotificationScopeKind kind, Importance? importance, string? hostName) => kind switch
    {
        NotificationScopeKind.Importance => $"importance ≥ {importance}",
        NotificationScopeKind.Host => $"host {hostName ?? "?"}",
        _ => "everything",
    };

    /// <summary>Every rule's links, scopes and exclusions, read once.</summary>
    private sealed class Coverage
    {
        public required List<NotificationRuleService.RuleRow> Rules { get; init; }
        public required HashSet<(Guid Rule, Guid Sensor)> Links { get; init; }
        public required HashSet<(Guid Rule, Guid Sensor)> Exclusions { get; init; }
        public required Dictionary<Guid, List<(NotificationRuleScopeEntity Scope, string? HostName)>> Scopes { get; init; }

        /// <summary>link, scope (with which) or excluded; null when the rule does not concern the sensor.</summary>
        public NotificationMatrixCell? Cell(Guid ruleId, SensorInfo sensor)
        {
            if (Links.Contains((ruleId, sensor.Id)))
            {
                return new NotificationMatrixCell(ruleId, "link", null);
            }
            var scope = Scopes.TryGetValue(ruleId, out var scopes)
                ? scopes.FirstOrDefault(s => s.Scope.Covers(sensor.HostId, sensor.EffectiveImportance))
                : default;
            if (scope.Scope is null)
            {
                return null;
            }
            var label = Label(scope.Scope.Kind, scope.Scope.Importance, scope.HostName);
            return Exclusions.Contains((ruleId, sensor.Id)) ? new NotificationMatrixCell(ruleId, "excluded", label) : new NotificationMatrixCell(ruleId, "scope", label);
        }

        /// <summary>Whether an enabled rule covers the sensor.</summary>
        public bool Covered(SensorInfo sensor) =>
            Rules.Where(r => r.Enabled).Any(r => Cell(r.Id, sensor) is { Via: "link" or "scope" });
    }

    private async Task<Coverage> CoverageAsync(CancellationToken ct)
    {
        var rules = await db.NotificationRules.AsNoTracking().OrderBy(r => r.Name)
            .Select(r => new NotificationRuleService.RuleRow(r.Id, r.Name, r.Recipient, r.Type, r.Enabled, r.SubscribedSensors.Count, r.Scopes.Count))
            .ToListAsync(ct);
        var links = await db.NotificationRuleSensor.AsNoTracking().Select(l => new { l.NotificationRuleId, l.SensorId }).ToListAsync(ct);
        var exclusions = await db.NotificationRuleExclusions.AsNoTracking().ToListAsync(ct);
        var scopes = await db.NotificationRuleScopes.AsNoTracking().Select(s => new { Scope = s, HostName = s.Host != null ? s.Host.Name : null }).ToListAsync(ct);
        return new Coverage
        {
            Rules = rules,
            Links = links.Select(l => (l.NotificationRuleId, l.SensorId)).ToHashSet(),
            Exclusions = exclusions.Select(x => (x.RuleId, x.SensorId)).ToHashSet(),
            Scopes = scopes.GroupBy(s => s.Scope.RuleId).ToDictionary(g => g.Key, g => g.Select(s => (s.Scope, s.HostName)).ToList()),
        };
    }

    private static NotificationMatrixRow Row(SensorStatus s, Coverage coverage, IReadOnlyList<NotificationRuleService.RuleRow> columns) =>
        new(s.Info.Id, s.Info.Name, s.Info.Type, new Ref(s.Info.HostId, s.Info.HostName), s.Info.Importance, s.Info.EffectiveImportance, s.State,
            columns.Select(r => coverage.Cell(r.Id, s.Info)).OfType<NotificationMatrixCell>().ToList());

    public async Task<NotificationMatrix> MatrixAsync(NotificationMatrixQuery query, bool withSecrets, CancellationToken ct)
    {
        var coverage = await CoverageAsync(ct);
        var columns = coverage.Rules.Where(r => r.Enabled).ToList();
        var snapshot = await reader.SnapshotAsync(query.HostId is { } h ? s => s.ParentId == h : null, ct);
        var rows = snapshot.Sensors.Values
            .Where(s => query.Importance is not { } i || s.Info.EffectiveImportance == i)
            .Where(s => query.Type is not { } t || s.Info.Type == t)
            .Where(s => query.State is not { } st || s.State == st)
            .Where(s => Text.Contains(s.Info.Name, query.Search) || Text.Contains(s.Info.HostName, query.Search))
            .Where(s => query.Uncovered != true || !coverage.Covered(s.Info))
            .OrderBy(s => s.Info.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => Row(s, coverage, columns))
            .ToList();
        var page = Paging.Page(rows, query);
        return new NotificationMatrix(columns.Select(r => NotificationRuleService.ToItem(r, withSecrets)).ToList(), page.Items, page.Page,
            page.PageSize, page.TotalCount);
    }

    public async Task<NotificationCoverage> UncoveredAsync(CancellationToken ct)
    {
        var coverage = await CoverageAsync(ct);
        var columns = coverage.Rules.Where(r => r.Enabled).ToList();
        var snapshot = await reader.SnapshotAsync(null, ct);
        var all = snapshot.Sensors.Values.ToList();
        var uncovered = all.Where(s => !coverage.Covered(s.Info)).ToList();
        return new NotificationCoverage(all.Count, all.Count - uncovered.Count,
            uncovered.Count(s => s.Info.EffectiveImportance == Importance.High),
            uncovered.Count(s => s.Info.EffectiveImportance == Importance.Normal),
            uncovered.Count(s => s.Info.EffectiveImportance == Importance.Low),
            uncovered.OrderByDescending(s => s.Info.EffectiveImportance)
                .ThenBy(s => s.Info.HostName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase)
                .Take(500).Select(s => Row(s, coverage, columns)).ToList());
    }

    public async Task<NotificationAssignResult> AssignAsync(NotificationAssignRequest request, CancellationToken ct)
    {
        new ApiValidator()
            .Check(request.RuleIds.Count is >= 1 and <= 50, "ruleIds", "Between 1 and 50 rules.")
            .Check(request.SensorIds.Count is >= 1 and <= 5000, "sensorIds", "Between 1 and 5000 sensors.")
            .Check(Enum.IsDefined(request.Mode), "mode", "Add or Remove.")
            .ThrowIfInvalid();
        var ruleIds = request.RuleIds.Distinct().ToList();
        var sensorIds = request.SensorIds.Distinct().ToList();
        if (await db.NotificationRules.CountAsync(r => ruleIds.Contains(r.Id), ct) != ruleIds.Count)
        {
            throw ApiException.NotFound("Notification rule");
        }
        var snapshot = await reader.SnapshotAsync(s => sensorIds.Contains(s.Id), ct);
        if (snapshot.Sensors.Count != sensorIds.Count)
        {
            throw ApiException.NotFound("Sensor");
        }
        var coverage = await CoverageAsync(ct);
        int linked = 0, unlinked = 0, excluded = 0, included = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var ruleId in ruleIds)
        {
            foreach (var sensor in snapshot.Sensors.Values.Select(s => s.Info))
            {
                var pair = (ruleId, sensor.Id);
                if (request.Mode == AssignMode.Add)
                {
                    if (coverage.Exclusions.Contains(pair))
                    {
                        db.NotificationRuleExclusions.Remove(new NotificationRuleExclusionEntity { RuleId = ruleId, SensorId = sensor.Id });
                        included++;
                    }
                    if (!coverage.Links.Contains(pair))
                    {
                        db.NotificationRuleSensor.Add(new NotificationRuleSensorEntity { Id = Guid.NewGuid(), NotificationRuleId = ruleId, SensorId = sensor.Id });
                        linked++;
                    }
                }
                else
                {
                    if (coverage.Links.Contains(pair))
                    {
                        unlinked += await db.NotificationRuleSensor.Where(l => l.NotificationRuleId == ruleId && l.SensorId == sensor.Id).ExecuteDeleteAsync(ct);
                    }
                    var scoped = coverage.Scopes.TryGetValue(ruleId, out var scopes) && scopes.Any(s => s.Scope.Covers(sensor.HostId, sensor.EffectiveImportance));
                    if (scoped && !coverage.Exclusions.Contains(pair))
                    {
                        db.NotificationRuleExclusions.Add(new NotificationRuleExclusionEntity { RuleId = ruleId, SensorId = sensor.Id });
                        excluded++;
                    }
                }
            }
        }
        // Many rules and sensors: the audit gets the counts (docs/audit-redmine-zabbix-plan.md §3.3); the rows are in the changes.
        var rules = await db.NotificationRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).OrderBy(r => r.Name)
            .Select(r => new { r.Id, r.Name }).ToListAsync(ct);
        AuditLog.Target(AuditTarget.Many(AuditTargets.NotificationRule, rules.Select(r => (r.Id, r.Name)).ToList()));
        AuditLog.Summary($"{request.Mode} {sensorIds.Count} sensor{(sensorIds.Count == 1 ? "" : "s")}: {linked} linked, {unlinked} unlinked, "
            + $"{excluded} excluded, {included} included again");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new NotificationAssignResult(linked, unlinked, excluded, included);
    }

    /// <summary>Replaces the rule's scopes (§11.2).</summary>
    public async Task SetScopesAsync(Guid ruleId, IReadOnlyList<NotificationScopeRequest> scopes, CancellationToken ct)
    {
        var ruleName = await db.NotificationRules.AsNoTracking().Where(r => r.Id == ruleId).Select(r => r.Name).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Notification rule");
        AuditLog.Target(AuditTarget.Of(AuditTargets.NotificationRule, ruleId, ruleName));
        await ValidateAsync(scopes, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.NotificationRuleScopes.Where(s => s.RuleId == ruleId).ExecuteDeleteAsync(ct);
        db.NotificationRuleScopes.AddRange(Distinct(scopes).Select(s => new NotificationRuleScopeEntity
        {
            Id = Guid.NewGuid(), RuleId = ruleId, Kind = s.Kind,
            Importance = s.Kind == NotificationScopeKind.Importance ? s.Importance : null,
            HostId = s.Kind == NotificationScopeKind.Host ? s.HostId : null,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>What the scopes would cover now (minus the rule's exclusions), and those sensors' events a day over 7 days.</summary>
    public async Task<NotificationScopePreview> PreviewAsync(Guid ruleId, IReadOnlyList<NotificationScopeRequest> scopes, CancellationToken ct)
    {
        await ValidateAsync(scopes, ct);
        var excluded = await db.NotificationRuleExclusions.Where(x => x.RuleId == ruleId).Select(x => x.SensorId).ToListAsync(ct);
        var entities = Distinct(scopes).Select(s => new NotificationRuleScopeEntity { Kind = s.Kind, Importance = s.Importance, HostId = s.HostId }).ToList();
        var snapshot = await reader.SnapshotAsync(null, ct);
        var ids = snapshot.Sensors.Values.Select(s => s.Info)
            .Where(s => !excluded.Contains(s.Id) && entities.Any(e => e.Covers(s.HostId, s.EffectiveImportance)))
            .Select(s => s.Id).ToList();
        var since = Clock.UtcNow.AddDays(-7);
        var events = ids.Count == 0 ? 0 : await db.Events.CountAsync(e => ids.Contains(e.SensorId) && e.Created >= since, ct);
        return new NotificationScopePreview(ids.Count, Math.Round(events / 7.0, 1));
    }

    public async Task ExcludeAsync(Guid ruleId, Guid sensorId, bool exclude, CancellationToken ct)
    {
        var ruleName = await db.NotificationRules.AsNoTracking().Where(r => r.Id == ruleId).Select(r => r.Name).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Notification rule");
        AuditLog.Target(AuditTarget.Of(AuditTargets.NotificationRule, ruleId, ruleName));
        if (!await db.Sensors.AnyAsync(s => s.Id == sensorId, ct))
        {
            throw ApiException.NotFound("Sensor");
        }
        var exists = await db.NotificationRuleExclusions.AnyAsync(x => x.RuleId == ruleId && x.SensorId == sensorId, ct);
        if (exclude && !exists)
        {
            db.NotificationRuleExclusions.Add(new NotificationRuleExclusionEntity { RuleId = ruleId, SensorId = sensorId });
            await db.SaveChangesAsync(ct);
        }
        else if (!exclude && exists)
        {
            await db.NotificationRuleExclusions.Where(x => x.RuleId == ruleId && x.SensorId == sensorId).ExecuteDeleteAsync(ct);
        }
    }

    private async Task ValidateAsync(IReadOnlyList<NotificationScopeRequest> scopes, CancellationToken ct)
    {
        var v = new ApiValidator();
        v.Check(scopes.Count <= MaxScopes, "scopes", $"At most {MaxScopes} scopes.");
        for (var i = 0; i < scopes.Count; i++)
        {
            var s = scopes[i];
            v.Check(Enum.IsDefined(s.Kind), $"scopes[{i}].kind", "Importance, Host or Everything.");
            v.Check(s.Kind != NotificationScopeKind.Importance || s.Importance is { } imp && Enum.IsDefined(imp), $"scopes[{i}].importance",
                "Low, Normal or High.");
            v.Check(s.Kind != NotificationScopeKind.Host || s.HostId is { } hostId && await db.Hosts.AnyAsync(h => h.Id == hostId, ct),
                $"scopes[{i}].hostId", "An existing host.");
        }
        v.ThrowIfInvalid();
    }

    private static IEnumerable<NotificationScopeRequest> Distinct(IEnumerable<NotificationScopeRequest> scopes) =>
        scopes.DistinctBy(s => (s.Kind, s.Kind == NotificationScopeKind.Importance ? s.Importance : null, s.Kind == NotificationScopeKind.Host ? s.HostId : null));
}
