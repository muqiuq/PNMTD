using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Security;
using PNMTD.Models.Db;

namespace PNMTD.Auth;

public enum LoginStatus { Success, InvalidCredentials, LockedOut, TotpRequired, InvalidCode, LoginExpired }

public sealed record LoginOutcome(LoginStatus Status, UserEntity? User = null, TimeSpan? RetryAfter = null);

/// <summary>
/// Auth:TotpPolicy (§4.6). Optional: the SPA suggests 2FA after a sign-in without it. The Required
/// policies answer every call but setting it up with 403 totp_setup_required until it is on.
/// </summary>
public enum TotpPolicy { Optional, RequiredForAdmins, Required }

/// <summary>
/// Sign-in (docs/webui-migration-plan.md §4.5, §4.7). The reasons for a failure go to the audit log
/// only; the client always hears invalid_credentials.
/// <list type="bullet">
/// <item>An unknown name is verified against a dummy hash, so it takes as long as a wrong password.</item>
/// <item>With 2FA the password is step 1 and the code step 2 (<see cref="LoginTotpAsync"/>).</item>
/// <item>Lockout: 5 failures in a row, wrong passwords and wrong codes alike, lock the account for 5
///   minutes, then 10, 20, 40 and at most 60. Only a complete sign-in clears the count, so the right
///   password does not buy more guesses at the code. An admin's unlock, a password or 2FA reset clear it too.</item>
/// <item>A hash with fewer iterations than today's (the WebApp's 10,000) is replaced at the next success.</item>
/// </list>
/// </summary>
public sealed class AuthService(PnmtdDbContext db, UserAccounts accounts, TotpSecrets secrets)
{
    public const int FailuresBeforeLockout = 5;

    public static TimeSpan LockoutFor(int previousLockouts) =>
        TimeSpan.FromMinutes(Math.Min(60, 5 << Math.Min(previousLockouts, 4)));

    public async Task<LoginOutcome> LoginAsync(string? username, string? password, AuditContext audit, CancellationToken ct = default)
    {
        var typed = (username ?? "").Trim();
        var normalized = UserAccounts.Normalize(typed);
        var user = normalized.Length == 0 ? null : await db.Users.SingleOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
        var now = Clock.UtcNow;

        if (user?.LockoutEnd is { } lockedUntil && lockedUntil > now)
        {
            accounts.Log(AuditEvents.LoginFailed, user, audit, "locked out");
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.LockedOut, RetryAfter: lockedUntil - now);
        }

        var passwordValid = PasswordHasher.Verify(password, user?.PasswordHash ?? PasswordHasher.DummyHash) && user is not null;
        var failure = user is null ? "unknown user"
            : !passwordValid ? "wrong password"
            : !user.IsActive ? "inactive"
            : user.TemporaryPasswordExpiresAt is { } expires && expires < now ? "temporary password expired"
            : null;
        if (failure is not null)
        {
            accounts.Log(AuditEvents.LoginFailed, user, audit, failure, typed.Length > 128 ? typed[..128] : typed);
            if (user is not null && !passwordValid)
            {
                CountFailure(user, audit, now);
            }
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.InvalidCredentials);
        }

        var detail = (string?)null;
        if (PasswordHasher.NeedsRehash(user!.PasswordHash))
        {
            user.PasswordHash = PasswordHasher.Hash(password!);
            detail = "password hash upgraded";
        }
        if (user.IsTotpEnabled)
        {
            // The audit log gets the outcome of step 2.
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.TotpRequired, user);
        }
        UserAccounts.Unlock(user);
        user.LastLoginAt = now;
        accounts.Log(AuditEvents.LoginSucceeded, user, audit, detail);
        await accounts.SaveAsync(ct);
        return new LoginOutcome(LoginStatus.Success, user);
    }

    /// <summary>
    /// Step 2: the code, for the user and stamp of the pnmt.2fa ticket. A wrong or reused code counts
    /// towards the lockout like a wrong password. LoginExpired: the user changed in between (a new
    /// stamp, deactivated, 2FA reset), so the sign-in starts again with the password.
    /// </summary>
    public async Task<LoginOutcome> LoginTotpAsync(Guid userId, string stamp, string? code, AuditContext audit, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive || !user.IsTotpEnabled || user.SecurityStamp != stamp)
        {
            return new LoginOutcome(LoginStatus.LoginExpired);
        }
        var now = Clock.UtcNow;
        if (user.LockoutEnd is { } lockedUntil && lockedUntil > now)
        {
            accounts.Log(AuditEvents.TotpFailed, user, audit, "locked out");
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.LockedOut, RetryAfter: lockedUntil - now);
        }
        var secret = secrets.Unprotect(user.TotpSecretProtected);
        long step = 0;
        var check = secret is null ? TotpCheck.Invalid : TotpService.Check(secret, code, now, user.TotpLastUsedStep, out step);
        if (check != TotpCheck.Valid)
        {
            accounts.Log(AuditEvents.TotpFailed, user, audit,
                secret is null ? "secret unreadable (Data Protection keys lost?)" : check == TotpCheck.Reused ? "code already used" : "wrong code");
            CountFailure(user, audit, now);
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.InvalidCode);
        }
        user.TotpLastUsedStep = step;
        UserAccounts.Unlock(user);
        user.LastLoginAt = now;
        accounts.Log(AuditEvents.LoginSucceeded, user, audit, "with 2FA");
        await accounts.SaveAsync(ct);
        return new LoginOutcome(LoginStatus.Success, user);
    }

    private void CountFailure(UserEntity user, AuditContext audit, DateTime now)
    {
        user.FailedLoginCount++;
        if (user.FailedLoginCount >= FailuresBeforeLockout)
        {
            var duration = LockoutFor(user.LockoutCount);
            user.LockoutEnd = now + duration;
            user.LockoutCount++;
            user.FailedLoginCount = 0;
            accounts.Log(AuditEvents.LockedOut, user, audit, $"{duration.TotalMinutes:0} minutes");
        }
    }

    /// <summary>The flags the SPA acts on after sign-in and on every reload (§4.5).</summary>
    public static (bool MustChangePassword, bool TotpSetupRequired, bool TotpSetupSuggested) Flags(UserEntity user, TotpPolicy policy)
    {
        var required = !user.IsTotpEnabled && TotpRequired(policy, user.IsAdmin);
        return (user.MustChangePassword, required, !user.IsTotpEnabled && !required);
    }

    /// <summary>RequiredForAdmins means the admin flag; groups don't change it (docs/audit-redmine-zabbix-plan.md §5.2).</summary>
    public static bool TotpRequired(TotpPolicy policy, bool isAdmin) =>
        policy == TotpPolicy.Required || (policy == TotpPolicy.RequiredForAdmins && isAdmin);

    public static TotpPolicy Policy(IConfiguration configuration) =>
        Enum.TryParse<TotpPolicy>(configuration["Auth:TotpPolicy"], ignoreCase: true, out var policy) ? policy : TotpPolicy.Optional;
}

/// <summary>
/// What a session is checked against on every request (§4.7): the user's stamp and active state, cached for up to a
/// minute, and what it may do: the admin flag and the levels of its groups (docs/audit-redmine-zabbix-plan.md §5.4). The 2FA state decides whether a required setup is still pending. This server forgets an entry the moment it changes the user,
/// so its own changes end sessions at once; a `pnmtd users …` command (another process) takes up to
/// the minute.
/// </summary>
public sealed class SessionCache(IMemoryCache cache)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    public sealed record Entry(string Stamp, bool IsActive, bool IsTotpEnabled, Rights.UserRights Rights);

    /// <summary>The user's entry from the database: the flag, and the highest level their groups give per area.</summary>
    public static async Task<Entry?> LoadAsync(PnmtdDbContext db, Guid id)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new { u.SecurityStamp, u.IsActive, u.IsTotpEnabled, u.IsAdmin }).SingleOrDefaultAsync();
        if (user is null)
        {
            return null;
        }
        var levels = await db.UserGroups.AsNoTracking().Where(m => m.UserId == id)
            .SelectMany(m => m.Group.Rights).Select(r => new { r.Area, r.Level }).ToListAsync();
        return new Entry(user.SecurityStamp, user.IsActive, user.IsTotpEnabled,
            new Rights.UserRights(user.IsAdmin, Rights.UserRights.Combine(levels.Select(l => (l.Area, l.Level)))));
    }

    public async Task<Entry?> GetAsync(Guid userId, Func<Task<Entry?>> load) =>
        await cache.GetOrCreateAsync(Key(userId), e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheFor;
            return load();
        });

    public void Forget(Guid userId) => cache.Remove(Key(userId));

    private static string Key(Guid userId) => $"session:{userId}";

    /// <summary>
    /// The cookie handler's OnValidatePrincipal: a session whose user changed is signed out. A session
    /// that Auth:TotpPolicy requires 2FA of, while the user has none, gets the
    /// <see cref="SessionClaims.TotpSetupPending"/> claim for this request.
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var userId = principal?.UserId();
        var stamp = principal?.FindFirstValue(SessionClaims.SecurityStamp);
        Entry? entry = null;
        if (userId is { } id && stamp is not null)
        {
            var services = context.HttpContext.RequestServices;
            entry = await services.GetRequiredService<SessionCache>().GetAsync(id, () => LoadAsync(services.GetRequiredService<PnmtdDbContext>(), id));
        }
        if (entry is null || !entry.IsActive || entry.Stamp != stamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }
        var identity = (ClaimsIdentity)principal!.Identity!;
        foreach (var claim in identity.Claims.Where(c => c.Type is SessionClaims.TotpSetupPending or SessionClaims.Admin or SessionClaims.Right
            or ClaimTypes.Role).ToList())
        {
            identity.RemoveClaim(claim);
        }
        identity.AddClaims(entry.Rights.Claims());
        var policy = AuthService.Policy(context.HttpContext.RequestServices.GetRequiredService<IConfiguration>());
        if (!entry.IsTotpEnabled && AuthService.TotpRequired(policy, entry.Rights.IsAdmin))
        {
            identity.AddClaim(new Claim(SessionClaims.TotpSetupPending, "true"));
        }
    }
}
