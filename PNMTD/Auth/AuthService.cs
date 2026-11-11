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

public enum LoginStatus { Success, InvalidCredentials, LockedOut, TotpRequired }

public sealed record LoginOutcome(LoginStatus Status, UserEntity? User = null, TimeSpan? RetryAfter = null);

/// <summary>Auth:TotpPolicy (§4.6). Enforcement of the Required policies arrives with TOTP (phase 4).</summary>
public enum TotpPolicy { Optional, RequiredForAdmins, Required }

/// <summary>
/// Sign-in (docs/webui-migration-plan.md §4.5, §4.7). The reasons for a failure go to the audit log
/// only; the client always hears invalid_credentials.
/// <list type="bullet">
/// <item>An unknown name is verified against a dummy hash, so it takes as long as a wrong password.</item>
/// <item>Lockout: 5 wrong passwords in a row lock the account for 5 minutes, then 10, 20, 40 and at
///   most 60. A success, an admin's unlock or a password reset clears it.</item>
/// <item>A hash with fewer iterations than today's (the WebApp's 10,000) is replaced at the next success.</item>
/// </list>
/// </summary>
public sealed class AuthService(PnmtdDbContext db, UserAccounts accounts)
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
            accounts.Log(AuthEvents.LoginFailed, user, audit, "locked out");
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
            accounts.Log(AuthEvents.LoginFailed, user, audit, failure, typed.Length > 128 ? typed[..128] : typed);
            if (user is not null && !passwordValid)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= FailuresBeforeLockout)
                {
                    var duration = LockoutFor(user.LockoutCount);
                    user.LockoutEnd = now + duration;
                    user.LockoutCount++;
                    user.FailedLoginCount = 0;
                    accounts.Log(AuthEvents.LockedOut, user, audit, $"{duration.TotalMinutes:0} minutes");
                }
            }
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.InvalidCredentials);
        }

        UserAccounts.Unlock(user!);
        var detail = (string?)null;
        if (PasswordHasher.NeedsRehash(user!.PasswordHash))
        {
            user.PasswordHash = PasswordHasher.Hash(password!);
            detail = "password hash upgraded";
        }
        if (user.IsTotpEnabled)
        {
            // Step 2 (the code) arrives with TOTP in phase 4; until then no account can have it enabled.
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.TotpRequired, user);
        }
        user.LastLoginAt = now;
        accounts.Log(AuthEvents.LoginSucceeded, user, audit, detail);
        await accounts.SaveAsync(ct);
        return new LoginOutcome(LoginStatus.Success, user);
    }

    /// <summary>The flags the SPA acts on after sign-in and on every reload (§4.5).</summary>
    public static (bool MustChangePassword, bool TotpSetupRequired, bool TotpSetupSuggested) Flags(UserEntity user, TotpPolicy policy)
    {
        var required = !user.IsTotpEnabled && (policy == TotpPolicy.Required || (policy == TotpPolicy.RequiredForAdmins && user.Role == Roles.Admin));
        return (user.MustChangePassword, required, !user.IsTotpEnabled && !required);
    }

    public static TotpPolicy Policy(IConfiguration configuration) =>
        Enum.TryParse<TotpPolicy>(configuration["Auth:TotpPolicy"], ignoreCase: true, out var policy) ? policy : TotpPolicy.Optional;
}

/// <summary>
/// What a session is checked against on every request (§4.7): the user's stamp, role and active
/// state, cached for up to a minute. This server forgets an entry the moment it changes the user,
/// so its own changes end sessions at once; a `pnmtd users …` command (another process) takes up to
/// the minute.
/// </summary>
public sealed class SessionCache(IMemoryCache cache)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    public sealed record Entry(string Stamp, string Role, bool IsActive);

    public async Task<Entry?> GetAsync(Guid userId, Func<Task<Entry?>> load) =>
        await cache.GetOrCreateAsync(Key(userId), e =>
        {
            e.AbsoluteExpirationRelativeToNow = CacheFor;
            return load();
        });

    public void Forget(Guid userId) => cache.Remove(Key(userId));

    private static string Key(Guid userId) => $"session:{userId}";

    /// <summary>The cookie handler's OnValidatePrincipal: a session whose user changed is signed out.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var userId = principal?.UserId();
        var stamp = principal?.FindFirstValue(SessionClaims.SecurityStamp);
        Entry? entry = null;
        if (userId is { } id && stamp is not null)
        {
            var services = context.HttpContext.RequestServices;
            entry = await services.GetRequiredService<SessionCache>().GetAsync(id, () =>
                services.GetRequiredService<PnmtdDbContext>().Users.AsNoTracking()
                    .Where(u => u.Id == id)
                    .Select(u => new Entry(u.SecurityStamp, u.Role, u.IsActive))
                    .SingleOrDefaultAsync());
        }
        if (entry is null || !entry.IsActive || entry.Stamp != stamp || entry.Role != principal!.FindFirstValue(ClaimTypes.Role))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
