using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace PNMTD.Models.Db;

// 3rd Party Connectors (docs/connectors-plan.md §10): a narrow door into PNMT for one outside system and one purpose. The
// connectors, the outside systems that call them (callers, each with one key and its networks), the grants between the two, a
// script connector's target as text, its arguments, and the calls, which are the request log and the queue at once.

/// <summary>A connector's type (§3.2): what its calls run. Stored by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorType>))]
public enum ConnectorType { WindowsScript, LinuxScript }

/// <summary>How a caller gets the outcome (§4.5): it waits for it, or takes a ticket and asks later.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorMode>))]
public enum ConnectorMode { Wait, Ticket }

/// <summary>What a call does when the review can't run (§7.4, D4).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorReviewFallback>))]
public enum ConnectorReviewFallback { Refuse, RunUnreviewed }

/// <summary>A parameter's choice (§5.1): the caller passes it, PNMT sets it, or neither does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorArgumentMode>))]
public enum ConnectorArgumentMode { Exposed, Fixed, Omitted }

/// <summary>
/// A call's state (§4.4): queued and running are active, every other state is final. Once its run started, a call takes the run's
/// state; <see cref="Lost"/> is a run closed without a result (§9.4).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorCallState>))]
public enum ConnectorCallState { Queued, Running, Done, Failed, TimedOut, Offline, Lost, Refused, Expired, Cancelled, Checked }

/// <summary>A connector (§3.1, §10.1): what it is for, how it answers, its review and its limits. The target is per type.</summary>
[Table("connectors")]
[Audited]
public class ConnectorEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    /// <summary>In the URL: <c>/connector/v1/{slug}</c>.</summary>
    [MaxLength(64)]
    public string Slug { get; set; } = "";

    /// <summary>What the connector is for: for the people in PNMT, the caller's developers, and the review.</summary>
    [MaxLength(2000)]
    [AuditText]
    public string Purpose { get; set; } = "";

    public ConnectorType Type { get; set; }

    /// <summary>False at creation, until the arguments are set.</summary>
    public bool Enabled { get; set; }

    public ConnectorMode Mode { get; set; } = ConnectorMode.Wait;

    /// <summary>Wait mode: how long the answer waits for the outcome, 5 to Connectors__MaxWaitSeconds.</summary>
    public int WaitSeconds { get; set; } = 60;

    /// <summary>Whether the run's output goes back to the caller (D10): off until switched on.</summary>
    public bool ReturnOutput { get; set; }

    public int MaxCallsPerHour { get; set; } = 60;

    public int MaxQueued { get; set; } = 20;

    public int MaxQueueSeconds { get; set; } = 600;

    public bool ReviewEnabled { get; set; }

    /// <summary>The built-in purpose question (§7.2).</summary>
    public bool ReviewPurpose { get; set; } = true;

    /// <summary>Null: Ai__Guard__Threshold.</summary>
    public double? ReviewThreshold { get; set; }

    /// <summary>The admin's checks: <c>[{ "label": …, "question": … }]</c>, at most 5.</summary>
    [Column(TypeName = "jsonb")]
    [AuditText]
    public string ReviewChecks { get; set; } = "[]";

    public ConnectorReviewFallback ReviewFallback { get; set; } = ConnectorReviewFallback.Refuse;

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedBy { get; set; }

    [AuditIgnore]
    public DateTime UpdatedAt { get; set; }

    /// <summary>Updated at most once a minute.</summary>
    [AuditIgnore]
    public DateTime? LastCallAt { get; set; }

    public ConnectorScriptEntity? Script { get; set; }

    public List<ConnectorArgumentEntity> Arguments { get; set; } = [];

    public List<ConnectorGrantEntity> Grants { get; set; } = [];
}

/// <summary>An outside system that calls connectors (§6.1, §10.1): one key, its networks, and the connectors granted to it.</summary>
[Table("connector_callers")]
[Audited]
public class ConnectorCallerEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(2000)]
    public string? Description { get; set; }

    /// <summary>False at creation; needs at least one network (D3).</summary>
    public bool Enabled { get; set; }

    /// <summary>Where its calls come from: <c>[{ "cidr": "10.20.0.0/24", "label": "office" }]</c>, normalised (§6.3).</summary>
    [Column(TypeName = "jsonb")]
    public string Networks { get; set; } = "[]";

    /// <summary>SHA-256 hex of the key; a call's key is looked up by it (§6.2).</summary>
    [MaxLength(64)]
    [AuditSecret]
    public string KeyHash { get; set; } = "";

    /// <summary>The key under Data Protection (purpose PNMTD.Connectors.Key.v1), only to show it again.</summary>
    [AuditSecret]
    public string KeyProtected { get; set; } = "";

    /// <summary>The key's last 4 characters.</summary>
    [MaxLength(8)]
    [AuditPlain("the last four characters, to tell keys apart")]
    public string KeyHint { get; set; } = "";

    [AuditIgnore]
    public DateTime KeyIssuedAt { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedBy { get; set; }

    [AuditIgnore]
    public DateTime UpdatedAt { get; set; }

    [AuditIgnore]
    public DateTime? LastCallAt { get; set; }

    [MaxLength(64)]
    [AuditIgnore]
    public string? LastCallFrom { get; set; }

    public List<ConnectorGrantEntity> Grants { get; set; } = [];
}

/// <summary>One caller may call one connector (§6.1). Listed in the audit under both.</summary>
[Table("connector_grants")]
[Audited]
public class ConnectorGrantEntity
{
    public Guid CallerId { get; set; }

    public Guid ConnectorId { get; set; }

    /// <summary>"caller → connector", for the audit.</summary>
    [MaxLength(420)]
    public string Name { get; set; } = "";

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedBy { get; set; }

    public ConnectorCallerEntity Caller { get; set; } = null!;

    public ConnectorEntity Connector { get; set; } = null!;
}

/// <summary>
/// A script connector's target (§3.3, §10.2), 1:1 with the connector: all of it text, with no foreign key outside the connector. The
/// server's name is resolved at each call, the script is the connector's own copy, and the account is passed to the agent as named.
/// </summary>
[Table("connector_scripts")]
[Audited]
public class ConnectorScriptEntity
{
    [Key]
    public Guid ConnectorId { get; set; }

    /// <summary>The server's name as PNMT shows it (<c>WEB01 / Windows</c>), or the host's name alone.</summary>
    [MaxLength(500)]
    public string Server { get; set; } = "";

    /// <summary>Named so that the audit names the row by it.</summary>
    [MaxLength(200)]
    public string Name { get; set; } = "";

    [AuditText]
    public string ScriptText { get; set; } = "";

    /// <summary><c>ScriptHashes.Of(ScriptText)</c>.</summary>
    [MaxLength(64)]
    [AuditPlain("a script's SHA-256, which the agents' allowlists name")]
    public string Hash { get; set; } = "";

    /// <summary>1, and one more with every change of the text.</summary>
    public int ScriptRevision { get; set; } = 1;

    /// <summary>Information only: "library: Shop-Tools.ps1 v4".</summary>
    [MaxLength(500)]
    public string? TakenFrom { get; set; }

    [AuditIgnore]
    public ScriptParseState ParseState { get; set; }

    /// <summary>The <c>ParsedScript</c>, as in <c>agent_script_versions</c>.</summary>
    [Column(TypeName = "jsonb")]
    [AuditIgnore]
    public string? Parsed { get; set; }

    [MaxLength(1000)]
    [AuditIgnore]
    public string? ParseError { get; set; }

    [AuditIgnore]
    public DateTime? ParsedAt { get; set; }

    /// <summary>The arguments wait for an admin after a change of the text (§5.5): calls are refused until they are saved again.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>Null: the whole script.</summary>
    [MaxLength(100)]
    public string? Function { get; set; }

    /// <summary>Windows: Service, Stored or Gmsa. Linux: Service (root) or User.</summary>
    public Lib.Agent.RunAsKind RunAsKind { get; set; }

    [MaxLength(256)]
    public string? Account { get; set; }

    [MaxLength(16)]
    public string Shell { get; set; } = "powershell";

    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// A parameter's choice on a connector (§5.1, §10.3): exposed with its pattern, fixed with its value, or omitted. One row per
/// parameter of the target. <see cref="Parameter"/> is the parameter as the parse found it at the last reconcile, so the endpoint
/// checks a call's types and keeps its secrets apart even while a new text waits for its parse.
/// </summary>
[Table("connector_arguments")]
[Audited]
public class ConnectorArgumentEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid ConnectorId { get; set; }

    /// <summary>The parameter's name; unique per connector.</summary>
    [MaxLength(100)]
    public string Name { get; set; } = "";

    public ConnectorArgumentMode Mode { get; set; } = ConnectorArgumentMode.Omitted;

    /// <summary>Exposed only; always true for a mandatory parameter.</summary>
    public bool Required { get; set; }

    [MaxLength(500)]
    [AuditText]
    public string? Pattern { get; set; }

    /// <summary>The value PNMT passes, as JSON; never for a secure parameter.</summary>
    [Column(TypeName = "jsonb")]
    public string? FixedValue { get; set; }

    /// <summary>For the caller; defaults to the parameter's help.</summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>Text arguments only: line breaks and tabs allowed (D8).</summary>
    public bool Multiline { get; set; }

    [AuditIgnore]
    public int Order { get; set; }

    /// <summary>The <c>ScriptParameter</c> as JSON.</summary>
    [Column(TypeName = "jsonb")]
    [AuditIgnore]
    public string Parameter { get; set; } = "{}";
}

/// <summary>
/// One call, from the endpoint to its end (§10.4, D7, D20): the request log, the queue and the history at once. The endpoint inserts
/// the row; afterwards only the worker changes it (and a ticket's first fetch sets <see cref="CollectedAt"/>).
/// </summary>
[Table("connector_calls")]
[NotAudited("calls from outside: the request log and the queue")]
public class ConnectorCallEntity
{
    /// <summary>The ticket.</summary>
    [Key]
    public Guid Id { get; set; }

    public Guid? ConnectorId { get; set; }

    /// <summary>At the time; the slug as called when it named no connector.</summary>
    [MaxLength(200)]
    public string ConnectorName { get; set; } = "";

    /// <summary>The slug as called, so a ticket is found under it after the connector is gone.</summary>
    [MaxLength(64)]
    public string Slug { get; set; } = "";

    public Guid? CallerId { get; set; }

    [MaxLength(200)]
    public string CallerName { get; set; } = "";

    public DateTime ReceivedAt { get; set; }

    /// <summary>The address the call came from.</summary>
    [MaxLength(64)]
    public string? From { get; set; }

    public ConnectorCallState State { get; set; }

    /// <summary>Passed the endpoint's checks and was stored as queued: what the calls per hour count.</summary>
    public bool Accepted { get; set; }

    public bool DryRun { get; set; }

    public ConnectorMode Mode { get; set; }

    [MaxLength(32)]
    public string? RefusedBy { get; set; }

    /// <summary>While queued: worker, review, turn, capacity, server_offline, parameters_pending.</summary>
    [MaxLength(32)]
    public string? WaitingFor { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>The server the name found, at the time (no foreign key).</summary>
    public Guid? ServerSensorId { get; set; }

    [MaxLength(500)]
    public string? ServerName { get; set; }

    [MaxLength(200)]
    public string? ScriptName { get; set; }

    public int? ScriptRevision { get; set; }

    [MaxLength(64)]
    public string? Hash { get; set; }

    [MaxLength(100)]
    public string? Function { get; set; }

    /// <summary>The refusal's message, with the fields; what a queued call waited for when it expired.</summary>
    [MaxLength(2000)]
    public string? Message { get; set; }

    /// <summary>The caller's plain arguments as received; null once redacted.</summary>
    [Column(TypeName = "jsonb")]
    public string? Arguments { get; set; }

    /// <summary>The secure arguments' names only.</summary>
    public List<string> SecureNames { get; set; } = [];

    [MaxLength(200)]
    public string? Reference { get; set; }

    /// <summary>Null once redacted.</summary>
    [MaxLength(1000)]
    public string? Note { get; set; }

    public bool Redacted { get; set; }

    [MaxLength(100)]
    public string? IdempotencyKey { get; set; }

    [MaxLength(64)]
    public string? ArgumentsHash { get; set; }

    /// <summary><c>{ state, threshold, answers, refusedBy, reason, aiRequestId }</c> (§7.3).</summary>
    [Column(TypeName = "jsonb")]
    public string? Review { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>The first fetch of the ticket after the call became final (§4.6).</summary>
    public DateTime? CollectedAt { get; set; }
}
