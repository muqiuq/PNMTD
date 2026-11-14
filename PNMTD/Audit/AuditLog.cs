using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Audit;

/// <summary>
/// Writing to the audit log (docs/audit-redmine-zabbix-plan.md §3.3), the one way for every part of PNMT: services in a
/// write request name the target and a summary of the request's entry; anything else (a side effect, the CLI, an agent,
/// PNMT itself) records an entry of its own. Never a secret, in no field.
/// </summary>
public static class AuditLog
{
    public const int MaxDetail = 2000;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>In a write request: what it was on (its type, id, name then, and what it belongs to).</summary>
    public static void Target(AuditTarget target) => AuditScope.Current?.SetTarget(target);

    /// <summary>In a write request: the entry's detail, a sentence for people, instead of one made from the changes.</summary>
    public static void Summary(string summary) => AuditScope.Current?.SetSummary(summary);

    /// <summary>In a write request: a change the interceptor can't see (a bulk write, a link set).</summary>
    public static void Change(string field, string? old, string? @new, string? @object = null) =>
        AuditScope.Current?.AddChange(new AuditChange(@object, field, Cut(old, 500), Cut(@new, 500)));

    /// <summary>
    /// An event. In a write request whose endpoint names it, it becomes the request's entry; otherwise it is an entry of
    /// its own, added to <paramref name="db"/> and saved with the caller's <c>SaveChanges</c>, joined to the request's
    /// others by its correlation id.
    /// </summary>
    public static void Record(PnmtdDbContext db, string eventType, string? detail, AuditWho who, AuditTarget? target = null,
        AuditOutcome? outcome = null)
    {
        var scope = AuditScope.Current;
        if (scope is { Explicit: false } && scope.EventTypes.Contains(eventType) && outcome is null or AuditOutcome.Succeeded)
        {
            scope.Merge(eventType, detail, target);
            return;
        }
        db.AuditLog.Add(Entry(eventType, who, detail, target, outcome, scope?.CorrelationId, scope?.Request));
    }

    /// <summary>An entry, for <see cref="Record"/> and for writers that build their own (the seeds).</summary>
    public static AuditLogEntity Entry(string eventType, AuditWho who, string? detail, AuditTarget? target = null, AuditOutcome? outcome = null,
        string? correlationId = null, string? request = null, DateTime? at = null) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = at ?? Clock.UtcNow,
        UserId = who.UserId,
        Username = who.Username,
        EventType = eventType,
        Category = AuditCatalog.Get(eventType).Category,
        Actor = who.Actor,
        ActorRef = Cut(who.ActorRef, 200),
        Outcome = outcome ?? (AuditCatalog.Failures.Contains(eventType) ? AuditOutcome.Failed : AuditOutcome.Succeeded),
        IpAddress = who.IpAddress,
        UserAgent = string.IsNullOrEmpty(who.UserAgent) ? null : who.UserAgent,
        Detail = Cut(detail, MaxDetail),
        TargetType = target?.Type,
        TargetId = Cut(target?.Id, 64),
        TargetName = Cut(target?.Name, 500),
        Subjects = target?.Subjects.Distinct().ToList() ?? [],
        CorrelationId = correlationId,
        Request = request,
    };

    /// <summary>The detail made from the changes: "web01: Interval 60 → 30, Importance Normal → High".</summary>
    public static string? Describe(string? name, IReadOnlyList<AuditChange> changes, int leftOut)
    {
        if (changes.Count == 0)
        {
            return name;
        }
        // An object created or deleted, with its own fields: "web01 created" (the fields are in the changes).
        if (changes.FirstOrDefault(c => c.Kind is "created" or "deleted") is { } main && changes.All(c => c.Object == main.Object))
        {
            return Cut($"{name ?? main.Object} {main.Kind}", MaxDetail);
        }
        var parts = changes.Select(c => c.Kind switch
        {
            "secret" => $"{c.Field} changed",
            "text" => $"{c.Field} {c.Summary}",
            "created" => $"{c.Object ?? c.Field} created",
            "deleted" => $"{c.Object ?? c.Field} deleted",
            _ => $"{c.Field} {Show(c.Old)} → {Show(c.New)}",
        }).Distinct().ToList();
        var text = string.Join(", ", parts) + (leftOut > 0 ? $", and {leftOut} more" : "");
        return Cut(name is null ? text : $"{name}: {text}", MaxDetail);

        static string Show(string? value) => value is null or "" ? "–" : value.Length > 60 ? value[..59] + "…" : value;
    }

    public static string? Cut(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..(max - 1)] + "…";
}
