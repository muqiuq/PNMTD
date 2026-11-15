using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>Which of a user's two MCP tokens (docs/mcp-plan.md §4.1).</summary>
public enum McpTokenKind { Read, Write }

/// <summary>Why a token went off last (§4.4).</summary>
public enum McpTokenEnd { Off, Expired, Regenerated, Stamp, Strikes, Admin, UserInactive, TotpOff }

/// <summary>How an MCP request ended (§9.1).</summary>
public enum McpOutcome { Ok, ToolError, Denied, Refused, RateLimited, Unauthenticated }

/// <summary>
/// One of a user's two MCP tokens (docs/mcp-plan.md §4.2): read-only or read-write. Every user has both rows from the
/// start; the value is made the first time the token is switched on or regenerated, shown once, and kept only as its
/// SHA-256. Whether it is on is decided per request from these columns, the user and the settings (§4.3).
/// </summary>
[Table("mcp_tokens")]
[NotAudited("a token's changes are the McpToken… events, never its value")]
public class McpTokenEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public McpTokenKind Kind { get; set; }

    /// <summary>SHA-256 of the value, lower-case hex; null until a value was made.</summary>
    [MaxLength(64)]
    [AuditSecret]
    public string? Hash { get; set; }

    /// <summary>The value's last 4 characters, so a person can tell values apart.</summary>
    [MaxLength(8)]
    public string? Hint { get; set; }

    public DateTime? IssuedAt { get; set; }

    /// <summary>When it was switched on (or extended); null while it is off.</summary>
    public DateTime? OnSince { get; set; }

    /// <summary>The read-write token's end: <see cref="OnSince"/> plus Mcp:WriteHours. Null for the read-only one.</summary>
    public DateTime? OnUntil { get; set; }

    /// <summary>The user's security stamp when it was switched on: a new stamp ends it.</summary>
    [MaxLength(64)]
    public string? OnStamp { get; set; }

    /// <summary>The window for deleting (§7.5), read-write only.</summary>
    public DateTime? DeleteUntil { get; set; }

    /// <summary>How many objects were deleted in the current or last window.</summary>
    public int DeleteCount { get; set; }

    public DateTime? LastUsedAt { get; set; }

    [MaxLength(64)]
    public string? LastUsedFrom { get; set; }

    [MaxLength(200)]
    public string? LastClient { get; set; }

    /// <summary>The refused requests of the last 24 hours (§8.6).</summary>
    public List<DateTime> Strikes { get; set; } = [];

    /// <summary>Blocked by strikes until the user switches it on again.</summary>
    public DateTime? BlockedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    public McpTokenEnd? EndedWhy { get; set; }

    /// <summary>An admin's reason for turning it off, shown to the user.</summary>
    [MaxLength(300)]
    public string? EndedNote { get; set; }
}

/// <summary>
/// The MCP log (docs/mcp-plan.md §9): every request to <c>/mcp</c>, what it asked and what came back. What changed is in
/// the audit log under the same <see cref="CorrelationId"/>. The newest Mcp:LogEntries are kept.
/// </summary>
[Table("mcp_log")]
[NotAudited("the MCP log itself")]
public class McpLogEntity
{
    [Key]
    public long Id { get; set; }

    public DateTime At { get; set; }

    /// <summary>The request's id; the audit entries of the same call carry it.</summary>
    [MaxLength(64)]
    public string CorrelationId { get; set; } = "";

    public Guid? TokenId { get; set; }

    public McpTokenKind? Kind { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>The token's user as they were named then.</summary>
    [MaxLength(200)]
    public string Username { get; set; } = "";

    [MaxLength(64)]
    public string? Ip { get; set; }

    [MaxLength(300)]
    public string? UserAgent { get; set; }

    /// <summary>The client's name and version, where it sends them.</summary>
    [MaxLength(200)]
    public string? Client { get; set; }

    /// <summary><c>tools/list</c>, <c>tools/call</c>, <c>auth</c>.</summary>
    [MaxLength(32)]
    public string Method { get; set; } = "";

    [MaxLength(64)]
    public string? Tool { get; set; }

    [MaxLength(16)]
    public string? ToolKind { get; set; }

    /// <summary>The arguments, redacted (§9.5), at most 16 KiB.</summary>
    [Column(TypeName = "jsonb")]
    public string? Arguments { get; set; }

    public McpOutcome Outcome { get; set; }

    [MaxLength(64)]
    public string? ErrorCode { get; set; }

    [MaxLength(1000)]
    public string? Error { get; set; }

    [MaxLength(500)]
    public string Summary { get; set; } = "";

    /// <summary>The objects concerned: <c>[{ type, id, name }]</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string? Targets { get; set; }

    /// <summary>The result as the client got it, gzip, at most 256 KiB.</summary>
    public byte[]? Result { get; set; }

    public bool Truncated { get; set; }

    /// <summary>Jev's answers for <c>cookie_ask</c>: <c>{ question: probability }</c>.</summary>
    [Column(TypeName = "jsonb")]
    public string? Guard { get; set; }

    public long? CookieMessageId { get; set; }

    /// <summary>A refused request that counts as a strike (§8.6): the admins' alert lists these.</summary>
    public bool Strike { get; set; }

    public int DurationMs { get; set; }
}
