using PNMTD.Auth.Rights;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// One of the user's two MCP tokens (docs/mcp-plan.md §4.5). <see cref="State"/>: <c>on</c>, <c>off</c>, <c>no_value</c>,
/// <c>expired</c>, <c>blocked</c>, <c>stamp</c>, <c>inactive</c>, <c>totp</c>, <c>mcp_off</c>. <see cref="Until"/>: when it goes
/// off by itself. Never the value: that is shown once, when it is made.
/// </summary>
public sealed record McpTokenItem(
    McpTokenKind Kind,
    bool On,
    string State,
    DateTime? Until,
    bool HasValue,
    string? Hint,
    DateTime? IssuedAt,
    DateTime? LastUsedAt,
    string? LastUsedFrom,
    string? LastClient,
    DateTime? EndedAt,
    McpTokenEnd? EndedWhy,
    string? EndedNote,
    DateTime? BlockedAt,
    int Strikes,
    DateTime? DeleteUntil,
    int DeleteCount,
    IReadOnlyDictionary<Area, Level> Rights);

/// <summary><c>GET /account/mcp</c>: the settings that apply, the address to connect to, and both tokens.</summary>
public sealed record McpAccountItem(
    bool Enabled,
    string Url,
    int WriteHours,
    int ReadDaysAfterOnline,
    bool WriteRequiresTotp,
    bool TotpEnabled,
    int DeleteMinutes,
    int DeletesPerWindow,
    McpTokenItem Read,
    McpTokenItem Write);

/// <summary>After switching a token on or regenerating it: the token, and its value if one was made now. Shown once.</summary>
public sealed record McpTokenIssued(McpTokenItem Token, string? Value);

/// <summary>A tool as the documentation shows it (docs/mcp-plan.md §10.1), from the catalog itself.</summary>
public sealed record McpToolDoc(
    string Name,
    string Title,
    string Description,
    PNMTD.Tools.ToolKind Kind,
    string Right,
    bool ReadOnlyToken,
    bool Destructive,
    bool ForCookie,
    System.Text.Json.Nodes.JsonObject Parameters,
    bool Available);

/// <summary>A level per area for the ceiling's table.</summary>
public sealed record McpCeilingRow(Area Area, string Label, Level? ReadWrite, Level? ReadOnly);

/// <summary><c>GET /mcp/catalog</c>: everything Admin → MCP → Documentation shows, with this installation's values.</summary>
public sealed record McpCatalog(
    bool Enabled,
    string Url,
    string Instructions,
    int WriteHours,
    int ReadDaysAfterOnline,
    bool WriteRequiresTotp,
    int DeleteMinutes,
    int DeletesPerWindow,
    int CallsPerMinute,
    int WritesPerMinute,
    int CookieMessagesPerHour,
    int MaxResultChars,
    int StrikesToBlock,
    int LogEntries,
    IReadOnlyList<McpCeilingRow> Ceiling,
    IReadOnlyDictionary<string, string> GuardQuestions,
    IReadOnlyList<McpToolDoc> Tools);

/// <summary>An entry of the MCP log (§9.1), as the list shows it.</summary>
public sealed record McpLogItem(
    long Id,
    DateTime At,
    Guid? TokenId,
    McpTokenKind? Kind,
    Guid? UserId,
    string Username,
    string? Ip,
    string? Client,
    string Method,
    string? Tool,
    string? ToolKind,
    McpOutcome Outcome,
    string? ErrorCode,
    string Summary,
    bool Strike,
    int DurationMs);

/// <summary>One entry with everything: arguments, result, Jev's answers, and what changed (the audit's entries of the same call).</summary>
public sealed record McpLogDetail(
    McpLogItem Item,
    string CorrelationId,
    string? UserAgent,
    string? Arguments,
    string? Result,
    bool Truncated,
    string? Error,
    string? Targets,
    string? Guard,
    long? CookieMessageId,
    IReadOnlyList<AuditLogItem> Changes);

/// <summary><c>GET /mcp/log</c>: newest first. Filters: a user, a token, the kind, a tool, the outcome, a time range, text in the summary.</summary>
public sealed class McpLogQuery : PageQuery
{
    public Guid? UserId { get; set; }

    public Guid? TokenId { get; set; }

    public McpTokenKind? Kind { get; set; }

    public string? Tool { get; set; }

    public McpOutcome? Outcome { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

/// <summary>A user's token on Admin → MCP → Tokens (§10.3): never the value.</summary>
public sealed record McpAdminTokenItem(Guid Id, Guid UserId, string Username, string DisplayName, McpTokenItem Token);

/// <summary><c>POST /mcp/tokens/{id}/off</c>: why, shown to the token's user and in the audit.</summary>
public sealed record McpTurnOffRequest(string? Reason);
