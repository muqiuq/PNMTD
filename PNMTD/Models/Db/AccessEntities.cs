using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>What a login was to (docs/cookie-access-plan.md §4.1): a server with PNMT's agent, a MikroTik router, or PNMT itself.</summary>
public enum AccessTargetKind { Server, Router, Pnmt }

/// <summary>Where a reviewed login stands (§5.4, §6.5): waiting for a verdict, judged, or closed without one.</summary>
public enum AccessReviewState { Waiting, Normal, Unusual, Suspicious, Alarming, NotJudged }

/// <summary>
/// A candidate login (docs/cookie-access-plan.md §4.5, §10.1): one with a signal and no known access that covers it. Its
/// signals, the verdict on it (Cookie's or code's), the notices it led to, and <i>Was this you?</i>. The login itself stays
/// in <c>agent_logins</c> (servers, routers) or the audit log (PNMT); what the notice needs is copied here, because those
/// expire. Kept a year, like the decision log.
/// </summary>
[Table("access_reviews")]
[NotAudited("the access watch's book, kept by code; answers, the router switches and known access are audited")]
public class AccessReviewEntity
{
    [Key]
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public AccessTargetKind Target { get; set; }

    /// <summary>The server's or router's sensor; null for PNMT.</summary>
    public Guid? SensorId { get; set; }

    /// <summary>The server's or router's name, or "PNMT", as it was.</summary>
    [MaxLength(300)]
    public string TargetName { get; set; } = "";

    public Guid? AgentLoginId { get; set; }

    /// <summary>A PNMT sign-in's audit entry. No foreign key: the audit log's retention deletes in bulk.</summary>
    public Guid? AuditLogId { get; set; }

    /// <summary>An MCP token's first call from a network that day (docs/cookie-access-plan.md §4.3), in the MCP log.</summary>
    public long? McpLogId { get; set; }

    /// <summary>When the login happened.</summary>
    public DateTime At { get; set; }

    /// <summary>The account as the target names it: <c>CONTOSO\jdoe</c>, <c>root</c>, a PNMT username.</summary>
    [MaxLength(520)]
    public string Account { get; set; } = "";

    public bool IsAdmin { get; set; }

    /// <summary>How: rdp, ssh, winbox, web (PNMT's web UI), mcp …</summary>
    [MaxLength(32)]
    public string Way { get; set; } = "";

    /// <summary>The kind group of §4.4: remote, console, network, elevation, file-transfer, management, web, mcp.</summary>
    [MaxLength(32)]
    public string Group { get; set; } = "";

    [MaxLength(100)]
    public string? Source { get; set; }

    /// <summary>A Windows workstation's name, or PNMT's device family ("Firefox on Windows").</summary>
    [MaxLength(256)]
    public string? Device { get; set; }

    [MaxLength(100)]
    public string? KeyFingerprint { get; set; }

    [MaxLength(256)]
    public string? TargetUser { get; set; }

    /// <summary>The signals of §4.5 as JSON: key, strength, text.</summary>
    public string Signals { get; set; } = "[]";

    /// <summary>At least one strong signal.</summary>
    public bool Strong { get; set; }

    /// <summary>An agent's backlog, older than 24 hours when it came: never alarming, never wakes anyone (§4.5).</summary>
    public bool Late { get; set; }

    /// <summary>The text Jev or the scanner flagged as instructions to an AI: at least <i>suspicious</i> (§9).</summary>
    public bool Flagged { get; set; }

    public Guid? CaseId { get; set; }

    public AccessReviewState State { get; set; } = AccessReviewState.Waiting;

    public double? Confidence { get; set; }

    [MaxLength(300)]
    public string? Reason { get; set; }

    /// <summary>cookie or code.</summary>
    [MaxLength(16)]
    public string? JudgedBy { get; set; }

    public DateTime? JudgedAt { get; set; }

    public long? DecisionId { get; set; }

    /// <summary>The PNMT user the account belongs to (§5.3), or the user who signed in to PNMT.</summary>
    public Guid? PersonId { get; set; }

    // Was this you? (§6.6)
    public DateTime? AskedAt { get; set; }

    public List<string> AskedAddresses { get; set; } = [];

    /// <summary>The token's SHA-256, never the token; null once it is used.</summary>
    [MaxLength(64)]
    public string? TokenHash { get; set; }

    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>yes or no.</summary>
    [MaxLength(8)]
    public string? PersonAnswer { get; set; }

    public DateTime? PersonAnsweredAt { get; set; }

    [MaxLength(100)]
    public string? PersonAnsweredFrom { get; set; }
}

/// <summary>
/// Known access (docs/cookie-access-plan.md §4.4, §7): an account, a source network and kinds of login that count as history on
/// a target, a location or every target, until an end or for good. Set by people, from an answer or on Admin → Cookie → Access.
/// </summary>
[Table("access_known")]
[NotAudited("known access; adding and ending an entry are in the audit log")]
public class AccessKnownEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>target, location or all.</summary>
    [MaxLength(16)]
    public string Scope { get; set; } = "target";

    /// <summary>For a target: server, router or PNMT; null for every kind.</summary>
    public AccessTargetKind? Target { get; set; }

    public Guid? SensorId { get; set; }

    public Guid? LocationId { get; set; }

    /// <summary>The account, as the target names it; null for any.</summary>
    [MaxLength(520)]
    public string? Account { get; set; }

    /// <summary>"10.1.2.0/24"; null for any.</summary>
    [MaxLength(64)]
    public string? Source { get; set; }

    /// <summary>The kind groups it covers; empty for every one.</summary>
    public List<string> Groups { get; set; } = [];

    public DateTime? Until { get; set; }

    [MaxLength(500)]
    public string Comment { get; set; } = "";

    /// <summary>What it is about, for people: "srv-01", "PNMT".</summary>
    [MaxLength(300)]
    public string Label { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public Guid? CreatedById { get; set; }

    [MaxLength(200)]
    public string CreatedByName { get; set; } = "";

    /// <summary>The case whose <i>Expected</i> added it.</summary>
    public Guid? FromCaseId { get; set; }

    public DateTime? EndedAt { get; set; }

    [MaxLength(200)]
    public string? EndedByName { get; set; }
}
