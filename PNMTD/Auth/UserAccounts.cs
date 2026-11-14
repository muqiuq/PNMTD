using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Security;
using PNMTD.Models.Db;

namespace PNMTD.Auth;

/// <summary>Who did something and from where, for the audit log. The deploy scripts are "cli".</summary>
public sealed record AuditContext(string? IpAddress, string? UserAgent, string? Origin, Guid? ActorId = null, string? ActorName = null)
{
    public static readonly AuditContext Cli = new(null, null, "cli");

    /// <summary>The request's address and browser, and its signed-in user (none during the sign-in's anonymous steps).</summary>
    public static AuditContext From(HttpContext http)
    {
        var agent = http.Request.Headers.UserAgent.ToString();
        var signedIn = http.User.Identity?.IsAuthenticated == true;
        return new AuditContext(http.Connection.RemoteIpAddress?.ToString(), agent.Length > 256 ? agent[..256] : agent, null,
            signedIn ? http.User.UserId() : null, signedIn ? http.User.Identity!.Name : null);
    }
}

/// <summary>
/// Users and their passwords, for the API and for `pnmtd users …` alike (§4.8–4.10). Every change
/// that should end other sessions rotates the security stamp; every change is in the audit log.
/// </summary>
public sealed class UserAccounts(PnmtdDbContext db, SessionCache? sessions = null)
{
    /// <summary>For the CLI, which looks groups up by name.</summary>
    internal PnmtdDbContext Db => db;

    public static readonly TimeSpan TemporaryPasswordLifetime = TimeSpan.FromDays(7);

    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9._@-]{2,64}$", RegexOptions.CultureInvariant);

    public static string Normalize(string username) => username.Trim().ToLowerInvariant();

    public static string NewStamp() => Guid.NewGuid().ToString("N");

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
    public async Task<(UserEntity User, string? TemporaryPassword)> CreateAsync(string? username, string? displayName, bool isAdmin,
        IReadOnlyCollection<Guid>? groupIds, string? password, AuditContext audit, CancellationToken ct = default)
    {
        var name = username?.Trim() ?? "";
        var v = new ApiValidator()
            .Check(UsernamePattern.IsMatch(name), "username", "2 to 64 letters, digits or . _ @ -.")
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
        var groups = await GroupsAsync(groupIds, ct);

        var now = Clock.UtcNow;
        var temporary = password is null ? SecurePasswordGenerator.Generate() : null;
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Username = name,
            NormalizedUsername = Normalize(name),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password ?? temporary!),
            IsAdmin = isAdmin,
            IsActive = true,
            MustChangePassword = temporary is not null,
            TemporaryPasswordExpiresAt = temporary is not null ? now + TemporaryPasswordLifetime : null,
            SecurityStamp = NewStamp(),
            CreatedAt = now,
            PasswordChangedAt = now,
        };
        user.Groups.AddRange(groups.Select(g => new UserGroupEntity { UserId = user.Id, GroupId = g.Id }));
        db.Users.Add(user);
        Log(AuditEvents.UserCreated, user, audit, Access(isAdmin, groups.Select(g => g.Name)));
        await SaveAsync(ct);
        return (user, temporary);
    }

    /// <summary>
    /// Display name, admin flag, active state and groups (docs/audit-redmine-zabbix-plan.md §5.1). A change of the flag or
    /// of the active state ends the user's sessions; groups apply from the next request, without a sign-out (§5.4).
    /// <paramref name="isAdmin"/> and <paramref name="groupIds"/> null: unchanged.
    /// </summary>
    public async Task<UserEntity> UpdateAsync(Guid id, string? displayName, bool? isAdmin, bool isActive, IReadOnlyCollection<Guid>? groupIds,
        AuditContext audit, CancellationToken ct = default)
    {
        var user = await db.Users.Include(u => u.Groups).ThenInclude(m => m.Group).SingleOrDefaultAsync(u => u.Id == id, ct)
            ?? throw ApiException.NotFound("User");
        new ApiValidator()
            .Check(!string.IsNullOrWhiteSpace(displayName) && displayName.Trim().Length <= 128, "displayName", "1 to 128 characters.")
            .ThrowIfInvalid();
        var groups = groupIds is null ? null : await GroupsAsync(groupIds, ct);
        if (isAdmin == false || !isActive)
        {
            await GuardLastAdminAsync(user, ct);
        }
        user.DisplayName = displayName!.Trim();
        if (isAdmin is { } admin && user.IsAdmin != admin)
        {
            Log(AuditEvents.UserAdminChanged, user, audit, admin ? "now an admin" : "no longer an admin");
            user.IsAdmin = admin;
            user.SecurityStamp = NewStamp();
        }
        if (user.IsActive != isActive)
        {
            Log(AuditEvents.UserUpdated, user, audit, isActive ? "activated" : "deactivated");
            user.IsActive = isActive;
            user.SecurityStamp = NewStamp();
        }
        if (groups is not null)
        {
            var before = user.Groups.Select(m => m.Group.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
            var after = groups.Select(g => g.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (!before.SequenceEqual(after))
            {
                Log(AuditEvents.UserGroupsChanged, user, audit, $"{Names(before)} → {Names(after)}");
                user.Groups.RemoveAll(m => !groups.Any(g => g.Id == m.GroupId));
                user.Groups.AddRange(groups.Where(g => user.Groups.All(m => m.GroupId != g.Id))
                    .Select(g => new UserGroupEntity { UserId = user.Id, GroupId = g.Id }));
            }
        }
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return user;
    }

    /// <summary>"admin", "groups Operators, Viewers", "no groups": what a user may do, for the audit.</summary>
    public static string Access(bool isAdmin, IEnumerable<string> groups)
    {
        var names = groups.ToList();
        return isAdmin ? "admin" + (names.Count > 0 ? $", groups {string.Join(", ", names)}" : "") : names.Count > 0 ? $"groups {string.Join(", ", names)}" : "no groups";
    }

    private static string Names(IReadOnlyCollection<string> names) => names.Count == 0 ? "none" : string.Join(", ", names);

    /// <summary>The groups of a request: each must exist (422 groupIds otherwise).</summary>
    private async Task<List<GroupEntity>> GroupsAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct)
    {
        if (ids is not { Count: > 0 })
        {
            return [];
        }
        var distinct = ids.Distinct().ToList();
        var groups = await db.Groups.Where(g => distinct.Contains(g.Id)).ToListAsync(ct);
        new ApiValidator().Check(groups.Count == distinct.Count, "groupIds", "Unknown group.").ThrowIfInvalid();
        return groups;
    }

    /// <summary>
    /// Deletes a user. Their SSH keys (docs/linux-sensor-plan.md §6.4) keep their names; the owner becomes the text "former user
    /// jdoe", and the keys stay wherever they are authorized.
    /// </summary>
    public async Task DeleteAsync(Guid id, AuditContext audit, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        await GuardLastAdminAsync(user, ct);
        var keys = await db.SshKeys.Where(k => k.UserId == user.Id).ToListAsync(ct);
        foreach (var key in keys)
        {
            key.UserId = null;
            key.Owner = SshKeyManager.SshKeyRegistry.FormerUser(user.Username);
            key.UpdatedAt = Clock.UtcNow;
        }
        db.Users.Remove(user);
        Log(AuditEvents.UserDeleted, user, audit,
            keys.Count == 0 ? null : $"{keys.Count} SSH {(keys.Count == 1 ? "key" : "keys")} now owned by \"{SshKeyManager.SshKeyRegistry.FormerUser(user.Username)}\"");
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
        Log(AuditEvents.PasswordReset, user, audit, temporary is not null ? "temporary password" : "password set");
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
        Log(AuditEvents.TotpReset, user, audit, wasEnabled ? null : "2FA was off");
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
        return wasEnabled;
    }

    public async Task UnlockAsync(Guid id, AuditContext audit, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        Unlock(user);
        Log(AuditEvents.UserUnlocked, user, audit, null);
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
        Log(AuditEvents.PasswordChanged, user, audit, null);
        await SaveAsync(ct);
        sessions?.Forget(user.Id);
    }

    /// <summary>"Sign out all sessions": every session but the one that asks gets a new stamp to compare with.</summary>
    public async Task<UserEntity> RevokeSessionsAsync(Guid id, AuditContext audit, CancellationToken ct = default)
    {
        var user = await GetAsync(id, ct);
        user.SecurityStamp = NewStamp();
        Log(AuditEvents.SessionsRevoked, user, audit, null);
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
        var (user, _) = await CreateAsync(string.IsNullOrWhiteSpace(username) ? "admin" : username, null, isAdmin: true, groupIds: null,
            password ?? generated, new AuditContext(null, null, "seed"), ct);
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
        if (!user.IsAdmin || !user.IsActive)
        {
            return;
        }
        if (!await db.Users.AnyAsync(u => u.Id != user.Id && u.IsAdmin && u.IsActive, ct))
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

    /// <summary>
    /// An account's event (docs/audit-redmine-zabbix-plan.md §3.2). The actor is the signed-in user who did it, or the CLI;
    /// during the sign-in, the person signing in. The account is the target, so its History and the user filter find it.
    /// </summary>
    internal void Log(string eventType, UserEntity? user, AuditContext audit, string? detail, string? typedUsername = null)
    {
        var who = audit.Origin == "cli" ? AuditWho.Cli
            : audit.ActorId is { } actor ? new AuditWho(AuditActor.User, actor, audit.ActorName ?? "", null, audit.IpAddress, audit.UserAgent)
            : new AuditWho(AuditActor.User, user?.Id, user?.Username ?? typedUsername ?? "", null, audit.IpAddress, audit.UserAgent);
        AuditLog.Record(db, eventType, detail, who, user is null ? null : AuditTarget.User(user.Id, user.Username));
    }

    internal Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
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

    /// <summary>
    /// The admin flag and the rights (docs/audit-redmine-zabbix-plan.md §5.4), like <see cref="TotpSetupPending"/> never in
    /// the cookie: added on every request from the user's groups, so a group's change applies to open sessions at once.
    /// </summary>
    public const string Admin = "pnmt:admin";

    /// <summary>One per area: "Monitoring:Edit".</summary>
    public const string Right = "pnmt:right";

    public static ClaimsPrincipal Create(UserEntity user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(DisplayName, user.DisplayName),
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
