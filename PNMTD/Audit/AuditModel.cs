using System.Security.Claims;
using PNMTD.Api;
using PNMTD.Auth;

namespace PNMTD.Audit;

/// <summary>Who did it (docs/audit-redmine-zabbix-plan.md §3.2). Packages eight to ten add Mcp, Cookie and Seraph.</summary>
public enum AuditActor { User, Cli, Agent, System }

/// <summary>How it ended (§3.5).</summary>
public enum AuditOutcome { Succeeded, Failed, Denied }

/// <summary>
/// Who did something, and from where: the person (or the CLI, an agent, PNMT itself), the address and the browser.
/// <see cref="ActorRef"/> says which agent or token when the actor isn't a person.
/// </summary>
public sealed record AuditWho(AuditActor Actor, Guid? UserId, string Username, string? ActorRef, string? IpAddress, string? UserAgent)
{
    public static readonly AuditWho Cli = new(AuditActor.Cli, null, "", null, null, null);

    public static readonly AuditWho System = new(AuditActor.System, null, "", null, null, null);

    public static AuditWho Agent(string? address, string? reference = null) => new(AuditActor.Agent, null, "agent", reference, address, null);

    /// <summary>The signed-in user of a request, or nobody (an anonymous step of the sign-in).</summary>
    public static AuditWho From(HttpContext http)
    {
        var context = AuditContext.From(http);
        return new AuditWho(AuditActor.User, http.User.UserId(), http.User.Identity?.IsAuthenticated == true ? http.User.Identity.Name ?? "" : "",
            null, context.IpAddress, context.UserAgent);
    }

    /// <summary>The request's user, or the CLI when there is no request.</summary>
    public static AuditWho From(IHttpContextAccessor accessor) => accessor.HttpContext is { } http ? From(http) : Cli;

    /// <summary>From an <see cref="AuditContext"/>: the CLI for the deploy scripts, else the signed-in actor it carries.</summary>
    public static AuditWho From(AuditContext context) => context.Origin == "cli"
        ? Cli
        : new AuditWho(AuditActor.User, context.ActorId, context.ActorName ?? "", null, context.IpAddress, context.UserAgent);
}

/// <summary>
/// What an operation was on: a type of <see cref="AuditTargets"/>, the object's id and its name as it was then, and the
/// ids it belongs to (a sensor's host and location), so that the object's History finds it (§3.2, §4.3).
/// </summary>
public sealed record AuditTarget(string Type, string? Id, string? Name, IReadOnlyList<Guid> Subjects)
{
    public static AuditTarget Of(string type, Guid id, string? name, params Guid?[] belongsTo) =>
        new(type, id.ToString(), name, [id, .. belongsTo.Where(g => g is not null).Select(g => g!.Value)]);

    public static AuditTarget Of(string type, string? id, string? name) => new(type, id, name, []);

    public static AuditTarget User(Guid id, string username) => Of(AuditTargets.User, id, username);

    public static AuditTarget Host(Guid id, string name, Guid? locationId = null) => Of(AuditTargets.Host, id, name, locationId);

    public static AuditTarget Sensor(Guid id, string name, Guid? hostId = null, Guid? locationId = null) =>
        Of(AuditTargets.Sensor, id, name, hostId, locationId);
}

/// <summary>One change of a field (§3.4). A secret has no values; a long text a summary of its lines.</summary>
public sealed record AuditChange(string? Object, string Field, string? Old, string? New, string? Kind = null, string? Summary = null);

/// <summary>
/// The event a write action makes (§3.3): one entry per request, written by <see cref="AuditFilter"/>. The first type is
/// the entry's; a service recording one of the others (<see cref="AuditLog.Record"/>) makes the entry that type instead,
/// as <c>PUT /users/{id}</c> is a UserUpdated or a RoleChanged.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuditAttribute(params string[] eventTypes) : Attribute
{
    public IReadOnlyList<string> EventTypes { get; } = eventTypes.Length > 0 ? eventTypes : throw new ArgumentException("An event type is needed.");
}

/// <summary>
/// On a write action: it changes nothing a person did (a preview, a parse, a view counter), with the reason. On an entity:
/// its rows are operational data, not configuration, and their changes aren't recorded (§3.4).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NotAuditedAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}

/// <summary>An entity whose changes the audit records, field by field (§3.4).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AuditedAttribute : Attribute;

/// <summary>A secret: the audit records that it changed, never a value (§3.4).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditSecretAttribute : Attribute;

/// <summary>Noise: timestamps of its own, counters, computed columns. Not recorded.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditIgnoreAttribute : Attribute;

/// <summary>A long text (Markdown, notes, scripts): the lines added and removed, not the text.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditTextAttribute : Attribute;

/// <summary>A name that looks like a secret's but isn't (a public key, a key's name), with the reason (§3.7).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditPlainAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
