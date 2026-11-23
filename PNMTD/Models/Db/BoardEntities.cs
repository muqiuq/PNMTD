using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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
