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

    /// <summary>Admin, User or Viewer (PNMTD.Auth.Roles).</summary>
    public required string Role { get; set; }

    public bool IsActive { get; set; } = true;

    public bool MustChangePassword { get; set; }

    /// <summary>A temporary password (reset, new user) stops working at this time.</summary>
    public DateTime? TemporaryPasswordExpiresAt { get; set; }

    /// <summary>Rotated on a password, 2FA, role or active change: every session carrying the old one ends.</summary>
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

/// <summary>The audit log of sign-ins and account changes; the newest 10,000 entries are kept.</summary>
[Table("auth_log")]
public class AuthLogEntity
{
    [Key]
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>No foreign key: the entries outlive a deleted user.</summary>
    public Guid? UserId { get; set; }

    /// <summary>As the user typed it, for failed logins with unknown names too.</summary>
    public required string Username { get; set; }

    public required string EventType { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>Why a login failed, what changed, or "cli" for the deploy scripts.</summary>
    public string? Detail { get; set; }
}
