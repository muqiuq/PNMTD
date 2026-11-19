using System.Text.Json;
using PNMTD.Connectors;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// 3rd Party Connectors' admin API (docs/connectors-plan.md §11): the calls, the connectors, the callers, the selectors.

// --- calls (§13.2) ------------------------------------------------------------------------------------------------------------------

/// <summary>
/// <c>GET /connector-calls</c>: every connector's calls, active ones first (queued and running, oldest first), then the processed ones,
/// newest first. Filters: connector, caller, active, state, refused by, from (an address or a network), reference, the time.
/// </summary>
public sealed class ConnectorCallQuery : PageQuery
{
    public Guid? ConnectorId { get; set; }

    public Guid? CallerId { get; set; }

    /// <summary>true: queued and running only; false: processed only.</summary>
    public bool? Active { get; set; }

    public ConnectorCallState? State { get; set; }

    public string? RefusedBy { get; set; }

    /// <summary>An address, or a network such as 10.0.0.0/8.</summary>
    public string? From { get; set; }

    public string? Reference { get; set; }

    public DateTime? Since { get; set; }

    public DateTime? Until { get; set; }
}

/// <summary>A call in <i>Calls</i>: what it is, where it came from, where it is now, what became of it.</summary>
public sealed record ConnectorCallItem(
    Guid Id,
    Ref? Connector,
    string ConnectorName,
    Ref? Caller,
    string CallerName,
    DateTime ReceivedAt,
    string? From,
    ConnectorCallState State,
    ConnectorMode Mode,
    bool DryRun,
    string? RefusedBy,
    string? WaitingFor,
    string? WaitingText,
    DateTime? ExpiresAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    Ref? Server,
    string? ServerName,
    string? ScriptName,
    int? ScriptRevision,
    string? Function,
    string? Reference,
    string? ReviewState,
    bool Collected,
    int? ExitCode,
    double? DurationMs,
    double? QueuedSeconds,
    string? Message,
    int? Place,
    Guid? RunId);

/// <summary>The top of <i>Calls</i> (§13.2): running now against the limit, and the last 24 hours in figures.</summary>
public sealed record ConnectorCallFigures(
    bool Enabled,
    int Running,
    int MaxRunning,
    int Queued,
    int Calls24h,
    int Refused24h,
    int Failed24h,
    int Expired24h,
    double? LongestWaitSeconds24h,
    int KeepCalls,
    Ref? Sensor,
    SensorState? SensorState,
    string? SensorMessage);

/// <summary>A step of a call, with its time: received, reviewed, waited, started, finished.</summary>
public sealed record ConnectorCallStep(string Step, DateTime? At, string? Detail);

/// <summary>A call's review as PNMT shows it to a person (§7.3): the probabilities too, which the caller never hears.</summary>
public sealed record ConnectorCallReview(string State, double Threshold, IReadOnlyList<ConnectorReviewAnswer> Answers, string? RefusedBy, string? Reason,
    long? AiRequestId);

public sealed record ConnectorReviewAnswer(string Key, string Label, double Probability, bool Refuses);

/// <summary><c>GET /connector-calls/{id}</c>: the arguments (secure ones by name), the note, the steps, the review, the run with its output.</summary>
public sealed record ConnectorCallDetail(
    ConnectorCallItem Call,
    string? Arguments,
    IReadOnlyList<string> SecureNames,
    string? Note,
    bool Redacted,
    string? IdempotencyKey,
    IReadOnlyList<ConnectorCallStep> Steps,
    ConnectorCallReview? Review,
    AgentScriptRunDetail? Run);

// --- connectors (§13.3, §13.5) ------------------------------------------------------------------------------------------------------

public sealed class ConnectorQuery : PageQuery
{
    public ConnectorType? Type { get; set; }

    public bool? Enabled { get; set; }
}

/// <summary>A caller granted a connector, as the connector's pages show it.</summary>
public sealed record ConnectorCallerRef(Guid Id, string Name, bool Enabled, bool AnyAddress, int Networks, DateTime? LastCallAt);

public sealed record ConnectorItem(
    Guid Id,
    string Name,
    string Slug,
    ConnectorType Type,
    bool Enabled,
    string Server,
    string ScriptName,
    string? Function,
    ConnectorMode Mode,
    IReadOnlyList<ConnectorCallerRef> Callers,
    bool Review,
    IReadOnlyList<ConnectorWarning> Warnings,
    DateTime? LastCallAt,
    int Calls24h,
    int Refused24h);

/// <summary>A connector's target as text (§3.3), as stored, with its parse.</summary>
public sealed record ConnectorTargetDetail(
    string Server,
    string ScriptName,
    string ScriptText,
    string Hash,
    int ScriptRevision,
    string? TakenFrom,
    ScriptParseState ParseState,
    ParsedScript? Parsed,
    string? ParseError,
    string? Function,
    RunAsKind RunAsKind,
    string? Account,
    string Shell,
    int TimeoutSeconds,
    bool NeedsReview,
    string Sentence,
    Ref? ServerNow,
    IReadOnlyList<string> ServersFound);

/// <summary>A parameter's choice (§5.1), with the parameter as the parse found it.</summary>
public sealed record ConnectorArgumentDetail(
    string Name,
    ConnectorArgumentMode Mode,
    bool Required,
    string? Pattern,
    JsonElement? FixedValue,
    string? Description,
    bool Multiline,
    int Order,
    ScriptParameter Parameter,
    ConnectorValueKind Kind,
    string? Misfit);

/// <summary>The library's script of the same name, when its current text differs from the connector's (§3.3): <i>Compare…</i> and <i>Take vN…</i>.</summary>
public sealed record ConnectorLibraryHint(Guid ScriptId, string Name, int Version, bool Differs);

public sealed record ConnectorReviewSettings(bool Enabled, bool Purpose, double? Threshold, double DefaultThreshold, IReadOnlyList<ConnectorReviewCheck> Checks,
    ConnectorReviewFallback Fallback, string? Unusable);

public sealed record ConnectorFigures(int Calls24h, IReadOnlyDictionary<string, int> ByState24h, int Queued, int Running);

public sealed record ConnectorDetail(
    Guid Id,
    string Name,
    string Slug,
    string Purpose,
    ConnectorType Type,
    bool Enabled,
    ConnectorMode Mode,
    int WaitSeconds,
    int MaxWaitSeconds,
    bool ReturnOutput,
    int MaxCallsPerHour,
    int MaxQueued,
    int MaxQueueSeconds,
    ConnectorReviewSettings Review,
    ConnectorTargetDetail Target,
    IReadOnlyList<ConnectorArgumentDetail> Arguments,
    IReadOnlyList<ConnectorCallerRef> Callers,
    IReadOnlyList<ConnectorWarning> Warnings,
    ConnectorDescription Description,
    ConnectorLibraryHint? Library,
    ConnectorFigures Figures,
    string Url,
    bool PlainHttp,
    DateTime CreatedAt,
    string? CreatedBy,
    DateTime UpdatedAt,
    DateTime? LastCallAt);

/// <summary>A script connector's target (§3.3): every field text, filled by the selectors or typed.</summary>
public sealed class ConnectorTargetRequest
{
    public required string Server { get; init; }

    public required string ScriptName { get; init; }

    public required string ScriptText { get; init; }

    public string? TakenFrom { get; init; }

    /// <summary>Null or empty: the whole script.</summary>
    public string? Function { get; init; }

    public required RunAsKind RunAsKind { get; init; }

    public string? Account { get; init; }

    public string? Shell { get; init; }

    public int? TimeoutSeconds { get; init; }
}

public sealed class ConnectorArgumentRequest
{
    public required string Name { get; init; }

    public required ConnectorArgumentMode Mode { get; init; }

    public bool Required { get; init; }

    public string? Pattern { get; init; }

    public JsonElement? FixedValue { get; init; }

    public string? Description { get; init; }

    public bool Multiline { get; init; }
}

/// <summary>
/// <c>POST /connectors</c> and <c>PUT /connectors/{id}</c>: one request for both. At the creation the arguments come from the parse (every
/// parameter omitted, mandatory ones exposed); at a change, <see cref="Arguments"/> null keeps them, and a changed text reconciles them
/// (§5.5). <see cref="CallerIds"/> null keeps the grants.
/// </summary>
public sealed class ConnectorRequest
{
    public ConnectorType? Type { get; set; }

    public required string Name { get; set; }

    public required string Slug { get; set; }

    public required string Purpose { get; set; }

    public bool? Enabled { get; set; }

    public required ConnectorTargetRequest Target { get; set; }

    public List<ConnectorArgumentRequest>? Arguments { get; set; }

    public ConnectorMode? Mode { get; set; }

    public int? WaitSeconds { get; set; }

    public bool? ReturnOutput { get; set; }

    public int? MaxCallsPerHour { get; set; }

    public int? MaxQueued { get; set; }

    public int? MaxQueueSeconds { get; set; }

    public bool? ReviewEnabled { get; set; }

    public bool? ReviewPurpose { get; set; }

    /// <summary>Null: Ai__Guard__Threshold.</summary>
    public double? ReviewThreshold { get; set; }

    public List<ConnectorReviewCheck>? ReviewChecks { get; set; }

    public ConnectorReviewFallback? ReviewFallback { get; set; }

    public List<Guid>? CallerIds { get; init; }
}

/// <summary><c>GET /connectors/{id}/compare</c>: the connector's text and the library's current version of the script of the same name.</summary>
public sealed record ConnectorCompare(Guid ScriptId, string Name, int Version, string LibraryText, string LibraryHash, string ConnectorText, string ConnectorHash);

/// <summary><c>POST /connectors/{id}/check</c> (§11): sample arguments and a note, optionally through the review.</summary>
public sealed class ConnectorCheckRequest
{
    public Dictionary<string, JsonElement>? Arguments { get; init; }

    public string? Note { get; init; }

    public bool Review { get; init; }
}

public sealed record ConnectorCheckResult(bool Ok, string? Message, IReadOnlyDictionary<string, string> Fields, Ref? Server, IReadOnlyList<ConnectorWarning> Warnings,
    ConnectorCallReview? Review);

/// <summary><c>POST /connectors/parse</c>: a text's parse for the form, before a save.</summary>
public sealed class ConnectorParseRequest
{
    public required ConnectorType Type { get; init; }

    public required string Text { get; init; }

    /// <summary>The function chosen, for its parameters.</summary>
    public string? Function { get; init; }
}

public sealed record ConnectorParseResult(ScriptParseState State, ParsedScript? Parsed, string Hash, bool ParserAvailable);

/// <summary><c>POST /connectors/patterns/test</c>: compiles a pattern and matches values typed by the admin.</summary>
public sealed class ConnectorPatternRequest
{
    public required string Pattern { get; init; }

    public required List<string> Values { get; init; }
}

public sealed record ConnectorPatternMatch(string Value, bool? Matches);

public sealed record ConnectorPatternResult(bool Valid, string? Error, bool Backtracking, IReadOnlyList<ConnectorPatternMatch> Results);

// --- the selectors (§3.3) -----------------------------------------------------------------------------------------------------------

public sealed record ConnectorServerOption(Guid SensorId, string Name, bool Online, bool Scripts, bool Functions, string? Note);

public sealed record ConnectorLibraryScript(Guid Id, string Name, string? Description, int CurrentVersion, IReadOnlyList<int> Versions);

public sealed record ConnectorLibraryVersion(Guid ScriptId, string Name, int Number, string Text, string Hash, ScriptParseState ParseState, ParsedScript? Parsed,
    string TakenFrom);

public sealed record ConnectorRegisteredScript(string Name, string Hash, bool Matches, RunAsKind RunAs, string? Account, string RunAsName, string? Text,
    ParsedScript? Parsed, string TakenFrom);

public sealed record ConnectorAccountOption(RunAsKind Kind, string? Account, string Label, bool? Allowed, string? Note);

// --- callers (§13.4) ----------------------------------------------------------------------------------------------------------------

public sealed record ConnectorNetworkItem(string Cidr, string? Label, string? Inside);

public sealed record ConnectorCallerItem(
    Guid Id,
    string Name,
    string? Description,
    bool Enabled,
    string KeyHint,
    int Networks,
    bool AnyAddress,
    IReadOnlyList<Ref> Connectors,
    DateTime? LastCallAt,
    string? LastCallFrom,
    int Calls24h,
    int Refused24h);

public sealed record ConnectorCallerDetail(
    Guid Id,
    string Name,
    string? Description,
    bool Enabled,
    string KeyHint,
    DateTime KeyIssuedAt,
    IReadOnlyList<ConnectorNetworkItem> Networks,
    bool AnyAddress,
    IReadOnlyList<ConnectorCallerConnector> Connectors,
    DateTime? LastCallAt,
    string? LastCallFrom,
    int Calls24h,
    int Refused24h,
    string? YourAddress,
    string? AddressHint,
    DateTime CreatedAt,
    string? CreatedBy);

public sealed record ConnectorCallerConnector(Guid Id, string Name, string Slug, bool Enabled, ConnectorType Type);

public sealed class ConnectorNetworkRequest
{
    public required string Cidr { get; init; }

    public string? Label { get; init; }
}

/// <summary><c>POST /connector-callers</c> and <c>PUT /connector-callers/{id}</c>: everything but the key. Null connector ids keep the grants.</summary>
public sealed class ConnectorCallerRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool? Enabled { get; init; }

    public List<ConnectorNetworkRequest> Networks { get; init; } = [];

    public List<Guid>? ConnectorIds { get; init; }
}

/// <summary>A new caller, with its key: it is shown again later too, and each display is audited.</summary>
public sealed record ConnectorCallerCreated(ConnectorCallerDetail Caller, string Key);

public sealed record ConnectorCallerKey(string Key, string Hint, DateTime IssuedAt);

/// <summary>docs/connectors-api.md as embedded at the build.</summary>
public sealed record ConnectorDocs(string Markdown)
{
    public static ConnectorDocs Load()
    {
        using var stream = typeof(ConnectorDocs).Assembly.GetManifestResourceStream("docs/connectors-api.md");
        return new ConnectorDocs(stream is null ? "The connectors' reference isn't in this build: see docs/connectors-api.md." : new StreamReader(stream).ReadToEnd());
    }
}
