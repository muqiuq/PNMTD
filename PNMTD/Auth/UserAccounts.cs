using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Security;
using PNMTD.Models.Db;

namespace PNMTD.Auth;

/// <summary>The auth_log event types (docs/webui-migration-plan.md §4.3).</summary>
public static class AuthEvents
{
    public const string LoginSucceeded = "LoginSucceeded";
    public const string LoginFailed = "LoginFailed";
    public const string LockedOut = "LockedOut";
    public const string TotpFailed = "TotpFailed";
    public const string Logout = "Logout";
    public const string TotpEnabled = "TotpEnabled";
    public const string TotpDisabled = "TotpDisabled";
    public const string TotpReset = "TotpReset";
    public const string PasswordChanged = "PasswordChanged";
    public const string PasswordReset = "PasswordReset";
    public const string SessionsRevoked = "SessionsRevoked";
    public const string UserCreated = "UserCreated";
    public const string UserUpdated = "UserUpdated";
    public const string UserUnlocked = "UserUnlocked";
    public const string RoleChanged = "RoleChanged";
    public const string UserDeleted = "UserDeleted";

    // UDP nodes (docs/udp-iot-plan.md §4.3, §4.11): the security-relevant changes, never the key itself.
    public const string UdpKeyChanged = "UdpKeyChanged";
    public const string UdpCounterReset = "UdpCounterReset";
    public const string UdpSessionReset = "UdpSessionReset";
    public const string UdpFirmwareUploaded = "UdpFirmwareUploaded";
    public const string UdpFirmwareDeleted = "UdpFirmwareDeleted";
    public const string UdpUpdateStarted = "UdpUpdateStarted";
    public const string UdpUpdateCancelled = "UdpUpdateCancelled";

    // MikroTik routers (docs/mikrotik-plan.md §9.6): who saw a join script, and the security-relevant changes.
    public const string MikrotikJoinScriptViewed = "MikrotikJoinScriptViewed";
    public const string MikrotikHostKeyAccepted = "MikrotikHostKeyAccepted";
    public const string MikrotikSecretsChanged = "MikrotikSecretsChanged";
    public const string MikrotikManagementAccessChanged = "MikrotikManagementAccessChanged";
    public const string MikrotikDiagnosticsChanged = "MikrotikDiagnosticsChanged";
    public const string MikrotikBackupDownloaded = "MikrotikBackupDownloaded";
    public const string MikrotikBackupDeleted = "MikrotikBackupDeleted";
    public const string MikrotikUpdateStarted = "MikrotikUpdateStarted";
    public const string MikrotikUpdateCancelled = "MikrotikUpdateCancelled";
    public const string MikrotikCommandStarted = "MikrotikCommandStarted";
    public const string MikrotikCommandCancelled = "MikrotikCommandCancelled";
    public const string MikrotikCommandRedacted = "MikrotikCommandRedacted";
    public const string MikrotikSshKeyRotated = "MikrotikSshKeyRotated";
    public const string MikrotikSshKeyRetired = "MikrotikSshKeyRetired";

    // The backups' one-way encryption (docs/mikrotik-backup-plan.md §3.3, §3.5).
    public const string MikrotikBackupKeySet = "MikrotikBackupKeySet";
    public const string MikrotikBackupKeyRetired = "MikrotikBackupKeyRetired";
    public const string MikrotikBackupEncryptionChanged = "MikrotikBackupEncryptionChanged";

    // The WireGuard network (docs/mikrotik-plan.md §9.6).
    public const string WireGuardNetworkChanged = "WireGuardNetworkChanged";
    public const string WireGuardKeysRegenerated = "WireGuardKeysRegenerated";
    public const string WireGuardTunnelCreated = "WireGuardTunnelCreated";
    public const string WireGuardTunnelChanged = "WireGuardTunnelChanged";
    public const string WireGuardTunnelDeleted = "WireGuardTunnelDeleted";
    public const string WireGuardTunnelDownloaded = "WireGuardTunnelDownloaded";

    // The debug page's test sends (docs/ai-operations-plan.md §8).
    public const string DebugNotificationSent = "DebugNotificationSent";
    public const string DebugAiTested = "DebugAiTested";

    // The AI's work (docs/ai-operations-plan.md §14).
    public const string AiWorkRetried = "AiWorkRetried";
    public const string AiWorkCancelled = "AiWorkCancelled";
    public const string AiBackfillStarted = "AiBackfillStarted";
    public const string AiAnalysisStarted = "AiAnalysisStarted";

    // Servers with an agent and diagnostics (docs/windows-sensor-plan.md §9.2): Agent* for every platform since L1 of
    // docs/linux-sensor-plan.md (§3.3; they were Windows* before), Windows* for what is Windows' own (WMI).
    public const string AgentJoinTokenCreated = "AgentJoinTokenCreated";
    public const string AgentJoinTokenRevoked = "AgentJoinTokenRevoked";
    public const string AgentJoined = "AgentJoined";
    public const string AgentJoinFailed = "AgentJoinFailed";
    public const string AgentRevoked = "AgentRevoked";
    public const string AgentSettingsChanged = "AgentSettingsChanged";
    public const string WindowsSettingsChanged = "WindowsSettingsChanged";
    public const string DiagnosticsSetChanged = "DiagnosticsSetChanged";
    public const string DiagnosticsAssignmentChanged = "DiagnosticsAssignmentChanged";
    public const string AgentScriptChanged = "AgentScriptChanged";
    public const string AgentScriptRun = "AgentScriptRun";

    public static readonly string[] All =
    [
        LoginSucceeded, LoginFailed, LockedOut, TotpFailed, Logout, TotpEnabled, TotpDisabled, TotpReset, PasswordChanged,
        PasswordReset, SessionsRevoked, UserCreated, UserUpdated, UserUnlocked, RoleChanged, UserDeleted,
        UdpKeyChanged, UdpCounterReset, UdpSessionReset, UdpFirmwareUploaded, UdpFirmwareDeleted, UdpUpdateStarted, UdpUpdateCancelled,
        MikrotikJoinScriptViewed, MikrotikHostKeyAccepted, MikrotikSecretsChanged, MikrotikManagementAccessChanged, MikrotikDiagnosticsChanged,
        MikrotikBackupDownloaded, MikrotikBackupDeleted, MikrotikUpdateStarted, MikrotikUpdateCancelled,
        MikrotikCommandStarted, MikrotikCommandCancelled, MikrotikCommandRedacted, MikrotikSshKeyRotated, MikrotikSshKeyRetired,
        MikrotikBackupKeySet, MikrotikBackupKeyRetired, MikrotikBackupEncryptionChanged,
        WireGuardNetworkChanged, WireGuardKeysRegenerated,
        WireGuardTunnelCreated, WireGuardTunnelChanged, WireGuardTunnelDeleted, WireGuardTunnelDownloaded,
        DebugNotificationSent, DebugAiTested, AiWorkRetried, AiWorkCancelled, AiBackfillStarted, AiAnalysisStarted,
        AgentJoinTokenCreated, AgentJoinTokenRevoked, AgentJoined, AgentJoinFailed, AgentRevoked, AgentSettingsChanged, WindowsSettingsChanged,
        DiagnosticsSetChanged, DiagnosticsAssignmentChanged, AgentScriptChanged, AgentScriptRun,
    ];
}

/// <summary>Who did something and from where, for the audit log. The deploy scripts are "cli".</summary>
public sealed record AuditContext(string? IpAddress, string? UserAgent, string? Origin)
{
    public static readonly AuditContext Cli = new(null, null, "cli");

    public static AuditContext From(HttpContext http)
    {
        var agent = http.Request.Headers.UserAgent.ToString();
        return new AuditContext(http.Connection.RemoteIpAddress?.ToString(), agent.Length > 256 ? agent[..256] : agent, null);
    }
}

/// <summary>
/// Users and their passwords, for the API and for `pnmtd users …` alike (§4.8–4.10). Every change
/// that should end other sessions rotates the security stamp; every change is in the audit log.
/// </summary>
public sealed class UserAccounts(PnmtdDbContext db, SessionCache? sessions = null)
{
    public static readonly TimeSpan TemporaryPasswordLifetime = TimeSpan.FromDays(7);

    /// <summary>The audit log keeps this many entries, like the other MAXIMUM_NUM_OF_* limits.</summary>
    public const int MaximumAuthLogEntries = 10_000;

    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9._@-]{2,64}$", RegexOptions.CultureInvariant);

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    public static string NewStamp() => Guid.NewGuid().ToString("N");

    /// <summary>"admin", "User", … to the role's canonical name; null if there is no such role.</summary>
    public static string? ParseRole(string? role) =>
        Roles.All.FirstOrDefault(r => string.Equals(r, role?.Trim(), StringComparison.OrdinalIgnoreCase));

    public Task<UserEntity?> FindAsync(string username, CancellationToken ct = default)
    {
        var normalized = Normalize(username);
        return db.Users.SingleOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
    }

    public async Task<UserEntity> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.NotFound("User");

    /// <summary>
    /// A new user. Without <paramref name="password"/> one is generated: a temporary password that
    /// is shown once, expires after 7 days and must be changed at the first sign-in.
    /// </summary>
    public async Task<(UserEntity User, string? TemporaryPassword)> CreateAsync(string? username, string? displayName, string? role,
        string? password, AuditContext audit, CancellationToken ct = default)
    {
        var name = username?.Trim() ?? "";
        var canonicalRole = ParseRole(role);
        var v = new ApiValidator()
            .Check(UsernamePattern.IsMatch(name), "username", "2 to 64 letters, digits or . _ @ -.")
            .Check(canonicalRole is not null, "role", "Admin, User or Viewer.")
            .Check(displayName is null || displayName.Trim().Length <= 128, "displayName", "At most 128 characters.");
        if (password is not null && PasswordHasher.PolicyViolation(password) is { } violation)
        {
            v.Add("password", violation);
        }
        v.ThrowIfInvalid();
        if (await FindAsync(name, ct) is not null)
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"The username {name} is taken.");
        }

        var now = Clock.UtcNow;
        var temporary = password is null ? SecurePasswordGenerator.Generate() : null;
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = name,
            NormalizedUsername = Normalize(name),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password ?? temporary!),
            Role = canonicalRole!,
            IsActive = true,
            MustChangePassword = temporary is not null,
            TemporaryPasswordExpiresAt = temporary is not null ? now + TemporaryPasswordLifetime : null,
            SecurityStamp = NewStamp(),
            CreatedAt = now,
            PasswordChangedAt = now,
        };
        db.Users.Add(user);
        Log(AuthEvents.UserCreated, user, audit, $"role {user.Role}");
        await SaveAsync(ct);
        return (user, temporary);
    }

    /// <summary>Display name, role and active state. A role or active change ends the user's sessions.</summary>
    public async Task<UserEntity> UpdateAsync(Guid id, string? displayName, string? role, bool isActive, AuditContext audit,
        CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        var canonicalRole = ParseRole(role);
        new ApiValidator()
            .Check(canonicalRole is not null, "role", "Admin, User or Viewer.")
            .Check(!string.IsNullOrWhiteSpace(displayName) && displayName.Trim().Length <= 128, "displayName", "1 to 128 characters.")
            .ThrowIfInvalid();
        if (canonicalRole != Roles.Admin || !isActive)
        {
            await GuardLastAdminAsync(user, ct);
        }
        user.DisplayName = displayName!.Trim();
        if (user.Role != canonicalRole)
        {
            Log(AuthEvents.RoleChanged, user, audit, $"{user.Role} → {canonicalRole}");
            user.Role = canonicalRole!;
            user.SecurityStamp = NewStamp();
        }
        if (user.IsActive != isActive)
        {
            Log(AuthEvents.UserUpdated, user, audit, isActive ? "activated" : "deactivated");
            user.IsActive = isActive;
            user.SecurityStamp = NewStamp();
        }
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return user;
    }

    public async Task DeleteAsync(Guid id, AuditContext audit, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        await GuardLastAdminAsync(user, ct);
        db.Users.Remove(user);
        Log(AuthEvents.UserDeleted, user, audit, null);
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
    }

    /// <summary>
    /// A new password, by default a temporary one (shown once, 7 days, must be changed). Ends every
    /// session and lifts a lockout.
    /// </summary>
    public async Task<string?> ResetPasswordAsync(Guid id, string? password, AuditContext audit, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        if (password is not null && PasswordHasher.PolicyViolation(password) is { } violation)
        {
            throw ApiException.Invalid("password", violation);
        }
        var temporary = password is null ? SecurePasswordGenerator.Generate() : null;
        var now = Clock.UtcNow;
        user.PasswordHash = PasswordHasher.Hash(password ?? temporary!);
        user.MustChangePassword = temporary is not null;
        user.TemporaryPasswordExpiresAt = temporary is not null ? now + TemporaryPasswordLifetime : null;
        user.PasswordChangedAt = now;
        user.SecurityStamp = NewStamp();
        Unlock(user);
        Log(AuthEvents.PasswordReset, user, audit, temporary is not null ? "temporary password" : "password set");
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return temporary;
    }

    /// <summary>
    /// "Reset 2FA" (§4.6), for a user who lost the authenticator: 2FA off, every session ended, a
    /// lockout lifted (the failed codes caused it). The next sign-in offers the setup again.
    /// Returns whether 2FA was on.
    /// </summary>
    public async Task<bool> ResetTotpAsync(Guid id, AuditContext audit, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        var wasEnabled = user.IsTotpEnabled;
        ClearTotp(user);
        user.SecurityStamp = NewStamp();
        Unlock(user);
        Log(AuthEvents.TotpReset, user, audit, wasEnabled ? null : "2FA was off");
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return wasEnabled;
    }

    public async Task UnlockAsync(Guid id, AuditContext audit, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        Unlock(user);
        Log(AuthEvents.UserUnlocked, user, audit, null);
        await SaveAsync(ct);
    }

    /// <summary>The user's own change: needs the current password; ends the user's other sessions.</summary>
    public async Task ChangePasswordAsync(Guid id, string? currentPassword, string? newPassword, AuditContext audit,
        CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        var v = new ApiValidator();
        if (!PasswordHasher.Verify(currentPassword, user.PasswordHash))
        {
            v.Add("currentPassword", "Not your current password.");
        }
        if (PasswordHasher.PolicyViolation(newPassword) is { } violation)
        {
            v.Add("newPassword", violation);
        }
        else if (newPassword == currentPassword)
        {
            v.Add("newPassword", "Choose a password other than the current one.");
        }
        v.ThrowIfInvalid();
        user.PasswordHash = PasswordHasher.Hash(newPassword!);
        user.MustChangePassword = false;
        user.TemporaryPasswordExpiresAt = null;
        user.PasswordChangedAt = Clock.UtcNow;
        user.SecurityStamp = NewStamp();
        Log(AuthEvents.PasswordChanged, user, audit, null);
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
    }

    /// <summary>"Sign out all sessions": every session but the one that asks gets a new stamp to compare with.</summary>
    public async Task<UserEntity> RevokeSessionsAsync(Guid id, AuditContext audit, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        user.SecurityStamp = NewStamp();
        Log(AuthEvents.SessionsRevoked, user, audit, null);
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return user;
    }

    /// <summary>
    /// A new installation: when the users table is empty after the migrations, the first admin
    /// (Auth:InitialAdmin:Username, default admin) with Auth:InitialAdmin:Password, which
    /// deploy/init.sh generates. Without it a random password is logged once. Either way it must be
    /// changed at the first sign-in. Returns the generated password, if one was generated.
    /// </summary>
    public async Task<(UserEntity? User, string? GeneratedPassword)> SeedAsync(IConfiguration configuration, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct))
        {
            return (null, null);
        }
        var username = configuration["Auth:InitialAdmin:Username"];
        var password = configuration["Auth:InitialAdmin:Password"];
        var generated = string.IsNullOrEmpty(password) ? SecurePasswordGenerator.Generate() : null;
        var (user, _) = await CreateAsync(string.IsNullOrWhiteSpace(username) ? "admin" : username, null, Roles.Admin, password ?? generated,
            new AuditContext(null, null, "seed"), ct);
        // Known to whoever reads .env or the log: changed at the first sign-in, but it does not expire.
        user.MustChangePassword = true;
        await SaveAsync(ct);
        return (user, generated);
    }

    /// <summary>
    /// A change that would leave no active admin (deleting, demoting or deactivating the last one)
    /// is refused with 409 last_admin: nobody could manage users any more.
    /// </summary>
    private async Task GuardLastAdminAsync(UserEntity user, CancellationToken ct)
    {
        if (user.Role != Roles.Admin || !user.IsActive)
        {
            return;
        }
        if (!await db.Users.AnyAsync(u => u.Id != user.Id && u.Role == Roles.Admin && u.IsActive, ct))
        {
            throw ApiException.Conflict("last_admin", "This is the last active admin.");
        }
    }

    internal static void ClearTotp(UserEntity user)
    {
        user.TotpSecretProtected = null;
        user.IsTotpEnabled = false;
        user.TotpLastUsedStep = null;
        user.PendingTotpSecretProtected = null;
        user.PendingTotpCreatedAt = null;
    }

    internal static void Unlock(UserEntity user)
    {
        user.FailedLoginCount = 0;
        user.LockoutCount = 0;
        user.LockoutEnd = null;
    }

    internal void Log(string eventType, UserEntity? user, AuditContext audit, string? detail, string? typedUsername = null)
    {
        var origin = audit.Origin is null ? detail : detail is null ? audit.Origin : $"{audit.Origin}: {detail}";
        db.AuthLog.Add(new AuthLogEntity
        {
            Id = Guid.NewGuid(),
            CreatedAt = Clock.UtcNow,
            UserId = user?.Id,
            Username = user?.Username ?? typedUsername ?? "",
            EventType = eventType,
            IpAddress = audit.IpAddress,
            UserAgent = string.IsNullOrEmpty(audit.UserAgent) ? null : audit.UserAgent,
            Detail = origin,
        });
    }

    /// <summary>Saves, then keeps the newest <see cref="MaximumAuthLogEntries"/> audit entries.</summary>
    internal async Task SaveAsync(CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM auth_log WHERE "Id" IN (SELECT "Id" FROM auth_log ORDER BY "CreatedAt" DESC OFFSET {MaximumAuthLogEntries})
            """, ct);
    }
}

/// <summary>The claims of a session (§4.7): no secret, and the stamp the session is checked against.</summary>
public static class SessionClaims
{
    public const string DisplayName = "pnmt:display_name";
    public const string SecurityStamp = "pnmt:stamp";
    public const string MustChangePassword = "pnmt:must_change_password";

    /// <summary>
    /// Auth:TotpPolicy requires 2FA and the user has none yet. Not stored in the cookie: added on every
    /// request (SessionCache.ValidateAsync), so a policy change applies to running sessions too.
    /// </summary>
    public const string TotpSetupPending = "pnmt:totp_setup_pending";

    public static ClaimsPrincipal Create(UserEntity user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(DisplayName, user.DisplayName),
            new(ClaimTypes.Role, user.Role),
            new(SecurityStamp, user.SecurityStamp),
        };
        if (user.MustChangePassword)
        {
            claims.Add(new Claim(MustChangePassword, "true"));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name, ClaimTypes.Role));
    }

    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
