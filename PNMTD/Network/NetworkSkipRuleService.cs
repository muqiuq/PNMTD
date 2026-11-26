using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Network;
using PNMTD.Locations;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;

namespace PNMTD.Network;

/// <summary>
/// The skip rules (docs/network-suggestion-rules-plan.md §4): what the analysis leaves out of the suggestions. Listed with
/// View, changed with Manage; each change is audited by its endpoint's event and followed by an analysis in a few seconds.
/// The preview runs the analysis in memory, with and without the draft, and writes nothing.
/// </summary>
public sealed class NetworkSkipRuleService(PnmtdDbContext db, LocationTree locations, NetworkAnalysis analysis, IHttpContextAccessor accessor)
{
    public const int MaxName = 100;
    public const int MaxTypes = 20;
    public const int MaxType = 40;
    private const int PreviewItems = 20;

    private Task<NetworkDirectory> DirectoryAsync(CancellationToken ct) => NetworkDirectory.LoadAsync(db, locations, ct);

    public async Task<NetworkSkipRuleList> ListAsync(CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var rules = await db.NetworkSkipRules.AsNoTracking().OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        var types = await db.NetworkRouterPorts.AsNoTracking().Where(p => p.Type != null && p.Type != "").Select(p => p.Type!).Distinct().ToListAsync(ct);
        return new NetworkSkipRuleList(rules.Select(r => Item(r, d)).ToList(), types.Order(StringComparer.Ordinal).ToList());
    }

    public async Task<NetworkSkipRuleItem> CreateAsync(NetworkSkipRuleRequest request, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        var who = AuditWho.From(accessor);
        var rule = new NetworkSkipRuleEntity { Id = Guid.NewGuid(), CreatedAt = Clock.UtcNow, CreatedBy = who.UserId, CreatedByName = Name(who) };
        Apply(rule, request, d);
        db.NetworkSkipRules.Add(rule);
        AuditLog.Target(Target(rule));
        AuditLog.Summary($"Skip rule {rule.Name} created: {Describe(rule, d)}");
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
        return Item(rule, d);
    }

    public async Task<NetworkSkipRuleItem> UpdateAsync(Guid id, NetworkSkipRuleRequest request, CancellationToken ct)
    {
        var rule = await db.NetworkSkipRules.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Skip rule");
        var d = await DirectoryAsync(ct);
        Apply(rule, request, d);
        var who = AuditWho.From(accessor);
        rule.UpdatedAt = Clock.UtcNow;
        rule.UpdatedBy = who.UserId;
        rule.UpdatedByName = Name(who);
        AuditLog.Target(Target(rule));
        AuditLog.Summary($"Skip rule {rule.Name} changed: {Describe(rule, d)}{(rule.Enabled ? "" : " (off)")}");
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
        return Item(rule, d);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.NetworkSkipRules.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Skip rule");
        AuditLog.Target(Target(rule));
        AuditLog.Summary($"Skip rule {rule.Name} deleted");
        db.NetworkSkipRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        analysis.RequestSoon();
    }

    /// <summary>
    /// What a draft would change (§4.5): the analysis with the rules as stored, and with the draft in place of the rule it edits.
    /// Dismissed suggestions are left out of the difference: they aren't shown either way.
    /// </summary>
    public async Task<NetworkSkipRulePreview> PreviewAsync(NetworkSkipRulePreviewRequest request, CancellationToken ct)
    {
        var d = await DirectoryAsync(ct);
        if (request.Id is { } id && !await db.NetworkSkipRules.AnyAsync(r => r.Id == id, ct))
        {
            throw ApiException.NotFound("Skip rule");
        }
        // The draft comes last, so the rules before it keep their tags.
        var draft = new NetworkSkipRuleEntity { Id = Guid.NewGuid(), CreatedAt = DateTime.MaxValue };
        Apply(draft, request.Rule, d);
        var input = await NetworkAnalysis.LoadAsync(db, ct);
        var rules = input.Rules.Where(r => r.Id != request.Id).ToList();
        if (draft.Enabled)
        {
            rules.Add(NetworkSkipRules.ToTopology(draft, d.Routers.ToDictionary(r => r.Key, r => r.Value.LocationId), d.Tree));
        }
        var before = TopologyAnalyzer.Analyze(input);
        var after = TopologyAnalyzer.Analyze(new TopologyInput
        {
            Routers = input.Routers, Hosts = input.Hosts, Ports = input.Ports, Observations = input.Observations, Adapters = input.Adapters,
            Assignments = input.Assignments, Ignored = input.Ignored, Connections = input.Connections, Rules = rules,
        });
        var hidden = (await db.NetworkSuggestions.AsNoTracking().Where(s => s.State != NetworkSuggestionState.Open).Select(s => s.Key).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var then = after.Suggestions.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var now = before.Suggestions.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var removed = before.Suggestions.Where(s => !then.Contains(s.Key) && !hidden.Contains(s.Key)).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        var added = after.Suggestions.Where(s => !now.Contains(s.Key) && !hidden.Contains(s.Key)).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        var hit = after.Hits.FirstOrDefault(h => h.RuleId == draft.Id);
        return new NetworkSkipRulePreview(hit?.Rows ?? 0, hit?.Routers ?? 0,
            removed.Take(PreviewItems).Select(s => Preview(s, d)).ToList(), removed.Count,
            added.Take(PreviewItems).Select(s => Preview(s, d)).ToList(), added.Count,
            after.Interfaces.Count(i => i.SkippedBy == draft.Id));
    }

    private static NetworkSkipPreviewSuggestion Preview(SuggestionResult s, NetworkDirectory d) =>
        new(s.Key, d.End(s.A), d.End(s.B), s.Confidence, s.Evidence.FirstOrDefault()?.Text);

    /// <summary>Checks a request and writes it into the rule: 400 with every problem at once.</summary>
    private static void Apply(NetworkSkipRuleEntity rule, NetworkSkipRuleRequest request, NetworkDirectory d)
    {
        var v = new ApiValidator();
        var name = Text.Trimmed(request.Name);
        v.Check(name is { Length: <= MaxName }, "name", $"A name of at most {MaxName} characters.");
        switch (request.Scope)
        {
            case NetworkSkipRuleScope.Location:
                v.Check(request.LocationId is { } l && d.Tree.Contains(l), "locationId", "A location.");
                break;
            case NetworkSkipRuleScope.Router:
                v.Check(request.SensorId is { } s && d.Routers.ContainsKey(s), "sensorId", "A MikroTik router.");
                break;
            case NetworkSkipRuleScope.All:
                break;
            default:
                v.Add("scope", "All, Location or Router.");
                break;
        }
        var pattern = Text.Trimmed(request.InterfacePattern);
        v.Check(pattern is not { Length: > SkipRuleSyntax.MaxPattern }, "interfacePattern", $"At most {SkipRuleSyntax.MaxPattern} characters.");
        var types = (request.InterfaceTypes ?? []).Select(Text.Trimmed).OfType<string>().Select(t => t.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        v.Check(types.Count <= MaxTypes && types.All(t => t.Length <= MaxType), "interfaceTypes", $"At most {MaxTypes} types of {MaxType} characters.");
        var vlanText = Text.Trimmed(request.Vlans);
        var vlans = vlanText is { Length: > SkipRuleSyntax.MaxVlans } ? null : SkipRuleSyntax.ParseVlans(vlanText);
        v.Check(vlans is not null, "vlans", "VLAN IDs from 1 to 4094 and ranges, as 10,20-29.");
        var macText = Text.Trimmed(request.MacPrefix);
        var mac = SkipRuleSyntax.MacPrefix(macText);
        v.Check(macText is null || mac is not null, "macPrefix", "1 to 6 octets of a MAC address, as 00:50:56.");
        var evidence = NetworkSkipRules.Evidence(request.Evidence);
        var conditions = pattern is not null || types.Count > 0 || vlans is { Count: > 0 } || evidence != SkipEvidence.None || mac is not null;
        v.Check(conditions || request.Scope == NetworkSkipRuleScope.Router, "scope",
            "At least one condition: a rule without one would skip everything. Only on one router may it do so.");
        v.ThrowIfInvalid();
        rule.Name = name!;
        rule.Enabled = request.Enabled;
        rule.Scope = request.Scope;
        rule.LocationId = request.Scope == NetworkSkipRuleScope.Location ? request.LocationId : null;
        rule.SensorId = request.Scope == NetworkSkipRuleScope.Router ? request.SensorId : null;
        rule.InterfacePattern = pattern;
        rule.InterfaceTypes = types.Count > 0 ? types : null;
        rule.Vlans = vlans is { Count: > 0 } ? SkipRuleSyntax.FormatVlans(vlans) : null;
        rule.Evidence = evidence;
        rule.MacPrefix = mac;
    }

    public static NetworkSkipRuleItem Item(NetworkSkipRuleEntity r, NetworkDirectory d) => new(
        r.Id, r.Name, r.Enabled, r.Scope, r.LocationId, r.LocationId is { } l && d.Tree.Contains(l) ? d.Tree.Path(l) : null,
        r.SensorId, r.SensorId is { } s && d.Routers.TryGetValue(s, out var router) ? router.Name : null,
        r.InterfacePattern, r.InterfaceTypes ?? [], r.Vlans, NetworkSkipRules.Evidence(r.Evidence), r.MacPrefix,
        r.Hits, r.HitRouters, r.HitsAt, r.CreatedAt, r.CreatedByName, r.UpdatedAt, r.UpdatedByName);

    /// <summary>"on office-gw: interface vlan*, VLAN 99".</summary>
    public static string Describe(NetworkSkipRuleEntity r, NetworkDirectory d)
    {
        var on = r.Scope switch
        {
            NetworkSkipRuleScope.Router => r.SensorId is { } s && d.Routers.TryGetValue(s, out var router) ? router.Name : "a router",
            NetworkSkipRuleScope.Location => r.LocationId is { } l && d.Tree.Contains(l) ? d.Tree.Path(l) : "a location",
            _ => "all routers",
        };
        var parts = new List<string>();
        if (r.InterfacePattern is { } p)
        {
            parts.Add($"interface {p}");
        }
        if (r.InterfaceTypes is { Count: > 0 } types)
        {
            parts.Add($"type {string.Join(", ", types)}");
        }
        if (r.Vlans is { } vlans)
        {
            parts.Add($"VLAN {vlans}");
        }
        if (r.Evidence != SkipEvidence.None)
        {
            parts.Add(string.Join(", ", NetworkSkipRules.Evidence(r.Evidence)));
        }
        if (r.MacPrefix is { } mac)
        {
            parts.Add($"MAC {mac}…");
        }
        return parts.Count == 0 ? $"on {on}: everything" : $"on {on}: {string.Join(", ", parts)}";
    }

    private static AuditTarget Target(NetworkSkipRuleEntity r) =>
        AuditTarget.Of(AuditTargets.NetworkSkipRule, r.Id, r.Name, r.SensorId, r.LocationId);

    private static string? Name(AuditWho who) => string.IsNullOrEmpty(who.Username) ? null : who.Username;
}
