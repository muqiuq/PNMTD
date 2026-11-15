using System.Text.Json;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Audit;

/// <summary>
/// The audit of one write request (docs/audit-redmine-zabbix-plan.md §3.3): its event, who, the endpoint, and what the
/// service and the change interceptor add (the target, a summary, the changes). It lives in an <see cref="AsyncLocal{T}"/>,
/// so every <see cref="PnmtdDbContext"/> of the request sees it, those from the factory too.
/// <para>
/// Its entry is added to the request's first <c>SaveChanges</c> (<see cref="AuditChangesInterceptor"/>), so a change is
/// never committed without it; later saves in the same context update it, and <see cref="AuditFilter"/> completes it at
/// the end: the outcome, and what other contexts or bulk writes added.
/// </para>
/// </summary>
public sealed class AuditScope
{
    public const int MaxChanges = 50;

    private static readonly AsyncLocal<AuditScope?> current = new();

    /// <summary>The running request's scope; none outside a write request, or once it ended (work it started later).</summary>
    public static AuditScope? Current => current.Value is { Completed: false } scope ? scope : null;

    private AuditScope(IReadOnlyList<string> eventTypes, AuditWho who, string? request, string? correlationId, string? routeId)
    {
        EventTypes = eventTypes;
        EventType = eventTypes[0];
        Who = who;
        Request = request;
        CorrelationId = correlationId;
        RouteId = routeId;
        Entry = new AuditLogEntity { Id = Guid.NewGuid(), CreatedAt = Clock.UtcNow, Username = who.Username, EventType = EventType };
    }

    public static AuditScope Begin(IReadOnlyList<string> eventTypes, AuditWho who, string? request, string? correlationId, string? routeId)
    {
        var scope = new AuditScope(eventTypes, who, request, correlationId, routeId);
        current.Value = scope;
        return scope;
    }

    public IReadOnlyList<string> EventTypes { get; }

    public string EventType { get; private set; }

    public AuditWho Who { get; }

    public string? Request { get; }

    public string? CorrelationId { get; }

    /// <summary>The route's <c>{id}</c>: the first guess at the target.</summary>
    public string? RouteId { get; }

    public AuditTarget? Target { get; private set; }

    public string? Summary { get; private set; }

    public List<AuditChange> Changes { get; } = [];

    public int ChangesLeftOut { get; private set; }

    public AuditOutcome Outcome { get; private set; } = AuditOutcome.Succeeded;

    public string? Error { get; private set; }

    public bool Completed { get; private set; }

    /// <summary>A service recorded the endpoint's event itself (<see cref="AuditLog.Record"/>): its detail is the entry's.</summary>
    internal bool Explicit { get; private set; }

    internal AuditLogEntity Entry { get; }

    /// <summary>The context the entry was added to, while its save is under way or after.</summary>
    internal PnmtdDbContext? Context { get; set; }

    internal bool Persisted { get; set; }

    /// <summary>Something changed after the entry was saved, which the filter writes at the end.</summary>
    internal bool Dirty { get; set; }

    public void SetTarget(AuditTarget target)
    {
        Target = target;
        Dirty = true;
    }

    /// <summary>The target the interceptor found, unless the service named one.</summary>
    internal void SuggestTarget(AuditTarget target)
    {
        if (Target is null || (Target.Type == target.Type && Target.Id == target.Id && Target.Name is null))
        {
            SetTarget(target);
        }
    }

    public void SetSummary(string? summary)
    {
        Summary = AuditLog.Cut(summary, AuditLog.MaxDetail);
        Dirty = true;
    }

    public void AddChange(AuditChange change)
    {
        if (Changes.Count < MaxChanges)
        {
            Changes.Add(change);
        }
        else
        {
            ChangesLeftOut++;
        }
        Dirty = true;
    }

    private (int Changes, int LeftOut, AuditTarget? Target)? checkpoint;

    /// <summary>Before a save: what to go back to if it fails, as its changes never happened.</summary>
    internal void Checkpoint() => checkpoint = (Changes.Count, ChangesLeftOut, Target);

    /// <summary>A save failed: the changes and the target it brought are dropped.</summary>
    internal void Restore()
    {
        if (checkpoint is not { } c)
        {
            return;
        }
        Changes.RemoveRange(c.Changes, Changes.Count - c.Changes);
        ChangesLeftOut = c.LeftOut;
        Target = c.Target;
        checkpoint = null;
    }

    /// <summary>
    /// A service's own record of one of the endpoint's events: it becomes the entry, with the service's detail as it is
    /// (none too), not one made from the changes, which stay in their own column.
    /// </summary>
    internal void Merge(string eventType, string? detail, AuditTarget? target)
    {
        EventType = eventType;
        Explicit = true;
        SetSummary(detail);
        if (target is not null)
        {
            SetTarget(target);
        }
    }

    /// <summary>
    /// Which of the declared events this request turned out to be, as an MCP tool that links or unlinks says (docs/mcp-plan.md
    /// §9.4). The detail stays what the changes or the service make of it.
    /// </summary>
    public void UseEvent(string eventType)
    {
        if (EventTypes.Contains(eventType) && !Explicit)
        {
            EventType = eventType;
            Dirty = true;
        }
    }

    internal void End(AuditOutcome outcome, string? error)
    {
        if (outcome != Outcome || error != Error)
        {
            Dirty = true;
        }
        Outcome = outcome;
        Error = AuditLog.Cut(error, 200);
        Completed = true;
        if (current.Value == this)
        {
            current.Value = null;
        }
    }

    /// <summary>Writes the scope's state into its entry.</summary>
    internal AuditLogEntity Apply()
    {
        var info = AuditCatalog.Get(EventType);
        var e = Entry;
        e.EventType = EventType;
        e.Category = info.Category;
        e.UserId = Who.UserId;
        e.Username = Who.Username;
        e.Actor = Who.Actor;
        e.ActorRef = Who.ActorRef;
        e.IpAddress = Who.IpAddress;
        e.UserAgent = string.IsNullOrEmpty(Who.UserAgent) ? null : Who.UserAgent;
        e.Request = Request;
        e.CorrelationId = CorrelationId;
        e.Outcome = Outcome;
        e.Error = Error;
        var target = Target ?? (RouteId is not null && info.TargetType is not null
            ? new AuditTarget(info.TargetType, RouteId, null, Guid.TryParse(RouteId, out var id) ? [id] : [])
            : null);
        e.TargetType = target?.Type;
        e.TargetId = AuditLog.Cut(target?.Id, 64);
        e.TargetName = AuditLog.Cut(target?.Name, 500);
        e.Subjects = target?.Subjects.Distinct().ToList() ?? [];
        e.Changes = Changes.Count == 0 ? null : JsonSerializer.Serialize(Changes, AuditLog.Json);
        e.Detail = Explicit ? Summary : Summary ?? AuditLog.Describe(target?.Name, Changes, ChangesLeftOut);
        return e;
    }
}
