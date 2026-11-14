using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>A person who signs in to the admin UI (docs/webui-migration-plan.md §4.3).</summary>
[Table("users")]
[Audited]
public class UserEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>As entered; sign-in compares <see cref="NormalizedUsername"/>.</summary>
    public required string Username { get; set; }

    /// <summary>Lower-case invariant, unique.</summary>
    [AuditIgnore]
    public required string NormalizedUsername { get; set; }

    public required string DisplayName { get; set; }

    /// <summary><c>pbkdf2-sha256$iterations$salt$key</c> (PasswordHasher).</summary>
    [AuditSecret]
    public required string PasswordHash { get; set; }

    /// <summary>
    /// Every right, always (docs/audit-redmine-zabbix-plan.md §5.1); only admins manage users and groups. Everyone else
    /// has what their groups give.
    /// </summary>
    public bool IsAdmin { get; set; }

    public List<UserGroupEntity> Groups { get; set; } = [];

    public bool IsActive { get; set; } = true;

    [AuditPlain("a flag")]

    public bool MustChangePassword { get; set; }

    /// <summary>A temporary password (reset, new user) stops working at this time.</summary>
    [AuditPlain("when a temporary password expires")]
    public DateTime? TemporaryPasswordExpiresAt { get; set; }

    /// <summary>Rotated on a password, 2FA, admin flag or active change: every session carrying the old one ends.</summary>
    [AuditIgnore]
    public required string SecurityStamp { get; set; }

    // TOTP (phase 4). The secrets are encrypted with Data Protection.
    [AuditSecret]
    public string? TotpSecretProtected { get; set; }

    public bool IsTotpEnabled { get; set; }

    [AuditIgnore]

    public long? TotpLastUsedStep { get; set; }

    [AuditSecret]

    public string? PendingTotpSecretProtected { get; set; }

    [AuditIgnore]

    public DateTime? PendingTotpCreatedAt { get; set; }

    /// <summary>Failures since the last success or lockout.</summary>
    [AuditIgnore]
    public int FailedLoginCount { get; set; }

    /// <summary>Lockouts since the last success: each one doubles the next (5, 10, 20, 40, 60 minutes).</summary>
    [AuditIgnore]
    public int LockoutCount { get; set; }

    [AuditIgnore]

    public DateTime? LockoutEnd { get; set; }

    [AuditIgnore]

    public DateTime CreatedAt { get; set; }

    [AuditIgnore]

    public DateTime? LastLoginAt { get; set; }

    [AuditIgnore]

    public DateTime? PasswordChangedAt { get; set; }

    // --- The person (docs/cookie-plan.md §8.1): contact details, and their place in the availability plan ---------------

    /// <summary>Where Cookie and its duties write to this person by e-mail.</summary>
    [MaxLength(254)]
    public string? Email { get; set; }

    /// <summary>The Pushover user key, 30 letters and digits.</summary>
    [MaxLength(30)]
    [AuditSecret]
    public string? PushoverKey { get; set; }

    /// <summary>The name Redmine shows on tickets: how an assignee is found. Only admins set it.</summary>
    [MaxLength(200)]
    public string? RedmineName { get; set; }

    /// <summary>The person has a column in the availability plan (§8.2).</summary>
    public bool InPlan { get; set; }

    /// <summary>3 letters, unique, upper case: how the plan shows the person.</summary>
    [MaxLength(3)]
    public string? ShortCode { get; set; }

    /// <summary><c>#rrggbb</c>.</summary>
    [MaxLength(7)]
    public string? Color { get; set; }

    /// <summary>The default week as JSON: for each weekday, whether the person works and with which type (the fill uses it).</summary>
    public string? DefaultWeek { get; set; }

    /// <summary>The column's place in the plan.</summary>
    public int PlanOrder { get; set; }
}

