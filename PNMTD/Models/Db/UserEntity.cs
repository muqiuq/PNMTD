using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>A person who signs in to the admin UI (docs/webui-migration-plan.md §4.3).</summary>
[Table("users")]
public class UserEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>As entered; sign-in compares <see cref="NormalizedUsername"/>.</summary>
    public required string Username { get; set; }

    /// <summary>Lower-case invariant, unique.</summary>
    public required string NormalizedUsername { get; set; }

    public required string DisplayName { get; set; }

    /// <summary><c>pbkdf2-sha256$iterations$salt$key</c> (PasswordHasher).</summary>
    public required string PasswordHash { get; set; }

    /// <summary>
    /// Every right, always (docs/audit-redmine-zabbix-plan.md §5.1); only admins manage users and groups. Everyone else
    /// has what their groups give.
    /// </summary>
    public bool IsAdmin { get; set; }

    public List<UserGroupEntity> Groups { get; set; } = [];

    public bool IsActive { get; set; } = true;

    public bool MustChangePassword { get; set; }

    /// <summary>A temporary password (reset, new user) stops working at this time.</summary>
    public DateTime? TemporaryPasswordExpiresAt { get; set; }

    /// <summary>Rotated on a password, 2FA, admin flag or active change: every session carrying the old one ends.</summary>
    public required string SecurityStamp { get; set; }

    // TOTP (phase 4). The secrets are encrypted with Data Protection.
    public string? TotpSecretProtected { get; set; }

    public bool IsTotpEnabled { get; set; }

    public long? TotpLastUsedStep { get; set; }

    public string? PendingTotpSecretProtected { get; set; }

    public DateTime? PendingTotpCreatedAt { get; set; }

    /// <summary>Failures since the last success or lockout.</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>Lockouts since the last success: each one doubles the next (5, 10, 20, 40, 60 minutes).</summary>
    public int LockoutCount { get; set; }

    public DateTime? LockoutEnd { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime? PasswordChangedAt { get; set; }
}

