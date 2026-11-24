using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Api.V1.Contracts;

namespace PNMTD.Models.Db;

// Boards (docs/boards-plan.md §11): monitoring dashboards for a wall, configured under Boards and shown by the board app at
// /pnmt-board/<token>. The layout is one JSON document (§6.2); the token is found by its hash and kept under Data Protection
// to show it again (§5.1).

/// <summary>A board (§6.1): its name, its URL's token, the networks it may be opened from, and its layout.</summary>
[Table("boards")]
[Audited]
public class BoardEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>1–100 characters, as entered.</summary>
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>The name compared, regardless of case: unique.</summary>
    [MaxLength(100)]
    [AuditIgnore]
    public string NormalizedName { get; set; } = "";

    /// <summary>For the list only; never shown on the board.</summary>
    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Off: the URL answers 403 board_disabled, and the token stays.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The token's SHA-256, lower-case hex: what a request is looked up by.</summary>
    [MaxLength(64)]
    [AuditIgnore]
    public string TokenHash { get; set; } = "";

    /// <summary>The token under Data Protection (PNMTD.Boards.Token.v1), to show the URL again.</summary>
    [AuditIgnore]
    public string TokenProtected { get; set; } = "";

    /// <summary>The token's last four characters.</summary>
    [MaxLength(8)]
    [AuditPlain("the token's last four characters, which the list and the audit name it by")]
    public string TokenHint { get; set; } = "";

    /// <summary>The networks it opens from without sign-in, as connector callers' (JSON); none means anywhere (§5.2).</summary>
    public string Networks { get; set; } = "[]";

    /// <summary>The layout (§6.2), validated on save. The audit entry carries a summary of its changes instead (§13.2).</summary>
    [Column(TypeName = "jsonb")]
    [AuditIgnore]
    public string Layout { get; set; } = "{}";

    /// <summary>Raised by each save: a save from an older one is an edit conflict.</summary>
    [AuditIgnore]
    public int Revision { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [AuditIgnore]
    public Guid? CreatedBy { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedByName { get; set; }

    [AuditIgnore]
    public DateTime UpdatedAt { get; set; }

    [AuditIgnore]
    public Guid? UpdatedBy { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? UpdatedByName { get; set; }

    /// <summary>When a screen last opened it (§5.4), written at most once a minute.</summary>
    [AuditIgnore]
    public DateTime? LastSeenAt { get; set; }

    [MaxLength(64)]
    [AuditIgnore]
    public string? LastSeenAddress { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? LastSeenAgent { get; set; }
}

/// <summary>
/// A message sent to boards (docs/board-messages-plan.md §4, §10): a kind, a title and a text, shown for so many seconds on each board
/// it went to, by a person (Admin → Debug) or by Cookie. Open while not withdrawn and before <see cref="EndsAt"/>; kept
/// Boards:MessageHistoryDays after that. Migration BoardMessages.
/// </summary>
[Table("board_messages")]
[NotAudited("a board message: sending and withdrawing it are in the audit log as BoardMessageSent and BoardMessageWithdrawn")]
public class BoardMessageEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>The boards it went to, as sent: "all boards" is resolved when sending.</summary>
    public Guid[] BoardIds { get; set; } = [];

    public BoardMessageKind Kind { get; set; }

    [MaxLength(100)]
    public string Title { get; set; } = "";

    /// <summary>Markdown as in a note element, up to 1000 characters.</summary>
    [MaxLength(1000)]
    public string? Text { get; set; }

    /// <summary>How long it shows: 10 s to Boards:MessageMaxMinutes.</summary>
    public int Seconds { get; set; }

    public DateTime SentAt { get; set; }

    public DateTime EndsAt { get; set; }

    public BoardMessageSource Source { get; set; }

    /// <summary>The user who sent it; null for Cookie.</summary>
    public Guid? SentById { get; set; }

    /// <summary>The name as it was: the user's, or Cookie's.</summary>
    [MaxLength(200)]
    public string SentByName { get; set; } = "";

    /// <summary>For a proposal of Cookie's that a person approved.</summary>
    public Guid? ApprovedById { get; set; }

    [MaxLength(200)]
    public string? ApprovedByName { get; set; }

    /// <summary>Cookie's decision log entry it came from.</summary>
    public long? CookieDecisionId { get; set; }

    public Guid? CookieCaseId { get; set; }

    /// <summary>Ended early, on every board: by a person, or by Cookie's own all-clear.</summary>
    public DateTime? WithdrawnAt { get; set; }

    public Guid? WithdrawnById { get; set; }

    [MaxLength(200)]
    public string? WithdrawnByName { get; set; }
}
