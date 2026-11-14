using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1;

/// <summary>Sign-in and sign-out (docs/webui-migration-plan.md §4.5, §4.9).</summary>
[Route("api/v1/auth")]
[Authorize]
public sealed class AuthController(AuthService auth, UserAccounts accounts, TotpSecrets secrets, PnmtdDbContext db,
    IConfiguration configuration) : V1ControllerBase
{
    /// <summary>
    /// Step 1. 200 <c>{ next: "done" }</c> with the session cookie, or <c>{ next: "totp" }</c> with the
    /// pnmt.2fa cookie when the account has 2FA; 401 invalid_credentials (also for an unknown user, an
    /// inactive one or an expired temporary password); 429 locked_out with Retry-After.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [SkipCsrf]
    [EnableRateLimiting(AuthSetup.LoginRateLimit)]
    [NotAudited("logged by AuthService: LoginSucceeded, LoginFailed, LockedOut")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var outcome = await auth.LoginAsync(request.Username, request.Password, AuditContext.From(HttpContext), ct);
        switch (outcome.Status)
        {
            case LoginStatus.Success:
                return await SignInAsync(outcome.User!);
            case LoginStatus.TotpRequired:
                Response.Cookies.Append(AuthSetup.TwoFactorCookieName, secrets.IssueTicket(outcome.User!), TwoFactorCookie(TwoFactorService.LoginStepLifetime));
                return new LoginResponse("totp", null, null);
            default:
                throw Refused(outcome);
        }
    }

    /// <summary>
    /// Step 2, within 5 minutes of step 1 (the pnmt.2fa cookie). 200 <c>{ next: "done" }</c> with the
    /// session cookie; 401 invalid_code (wrong, or used already), which counts towards the lockout;
    /// 401 login_expired (no step 1, too late, or the account changed): sign in again from the password.
    /// </summary>
    [HttpPost("login/totp")]
    [AllowAnonymous]
    [SkipCsrf]
    [EnableRateLimiting(AuthSetup.LoginRateLimit)]
    [NotAudited("logged by AuthService: LoginSucceeded, TotpFailed, LockedOut")]
    public async Task<ActionResult<LoginResponse>> LoginTotp(TotpCodeRequest request, CancellationToken ct)
    {
        var outcome = secrets.ReadTicket(Request.Cookies[AuthSetup.TwoFactorCookieName]) is { } ticket
            ? await auth.LoginTotpAsync(ticket.UserId, ticket.Stamp, request.Code, AuditContext.From(HttpContext), ct)
            : new LoginOutcome(LoginStatus.LoginExpired);
        if (outcome.Status is LoginStatus.InvalidCode)
        {
            throw Refused(outcome);
        }
        Response.Cookies.Delete(AuthSetup.TwoFactorCookieName, TwoFactorCookie(maxAge: null));
        return outcome.Status == LoginStatus.Success ? await SignInAsync(outcome.User!) : throw Refused(outcome);
    }

    private ApiException Refused(LoginOutcome outcome)
    {
        switch (outcome.Status)
        {
            case LoginStatus.LockedOut:
                Response.Headers.RetryAfter = ((int)Math.Ceiling(outcome.RetryAfter!.Value.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new ApiException(StatusCodes.Status429TooManyRequests, "locked_out", "Too many failed sign-ins. Try again later.");
            case LoginStatus.InvalidCode:
                return new ApiException(StatusCodes.Status401Unauthorized, "invalid_code", "Wrong code, or one that was used already.");
            case LoginStatus.LoginExpired:
                return new ApiException(StatusCodes.Status401Unauthorized, "login_expired", "The sign-in has expired. Enter your password again.");
            default:
                return new ApiException(StatusCodes.Status401Unauthorized, "invalid_credentials", "Wrong username or password.");
        }
    }

    /// <summary>The ticket itself expires after 5 minutes too; Max-Age only spares sending a dead one.</summary>
    private CookieOptions TwoFactorCookie(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = Request.IsHttps,
        Path = AuthSetup.TwoFactorCookiePath,
        MaxAge = maxAge,
    };

    [HttpPost("logout")]
    [Audit(AuditEvents.Logout)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (User.UserId() is { } id && await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct) is { } user)
        {
            accounts.Log(AuditEvents.Logout, user, AuditContext.From(HttpContext), null);
            await accounts.SaveAsync(ct);
        }
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Cookies.Delete(AuthSetup.CsrfCookieName, new CookieOptions { Path = "/" });
        return NoContent();
    }

    /// <summary>Anonymous: 401 when signed out. Signed in: the user and flags, and a fresh XSRF-TOKEN cookie.</summary>
    [HttpGet("me")]
    [AllowAnonymous]
    public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    {
        var user = User.UserId() is { } id ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct) : null;
        if (user is null)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthorized, "Not signed in.");
        }
        CsrfTokens.Issue(HttpContext);
        return new MeResponse(ToCurrentUser(user, User.Rights()), Flags(user));
    }

    private async Task<LoginResponse> SignInAsync(UserEntity user)
    {
        var principal = SessionClaims.Create(user);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = Clock.UtcNow });
        // The request tokens are bound to the user: issued for the new session, not the anonymous one.
        HttpContext.User = principal;
        CsrfTokens.Issue(HttpContext);
        var rights = (await SessionCache.LoadAsync(db, user.Id))?.Rights ?? UserRights.None;
        return new LoginResponse("done", ToCurrentUser(user, rights), Flags(user));
    }

    private SessionFlags Flags(UserEntity user)
    {
        var (mustChange, totpRequired, totpSuggested) = AuthService.Flags(user, AuthService.Policy(configuration));
        return new SessionFlags(mustChange, totpRequired, totpSuggested);
    }

    internal static CurrentUser ToCurrentUser(UserEntity user, UserRights rights) =>
        new(user.Id, user.Username, user.DisplayName, user.IsAdmin, rights.Levels, user.IsTotpEnabled);
}

/// <summary>The signed-in user's own account (§4.9).</summary>
[Route("api/v1/account")]
[Authorize]
public sealed class AccountController(UserAccounts accounts, TwoFactorService twoFactor) : V1ControllerBase
{
    /// <summary>One's own groups and effective rights (docs/audit-redmine-zabbix-plan.md §5.5).</summary>
    [HttpGet("access")]
    public async Task<AccessItem> Access([FromServices] GroupService groups, CancellationToken ct) => await groups.AccessAsync(User.UserId()!.Value, ct);

    /// <summary>Ends the user's other sessions; this one continues with a fresh cookie.</summary>
    [HttpPost("password")]
    [Audit(AuditEvents.PasswordChanged)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.NotFound("User");
        await accounts.ChangePasswordAsync(id, request.CurrentPassword, request.NewPassword, AuditContext.From(HttpContext), ct);
        await RenewSessionAsync(id, ct);
        return NoContent();
    }

    /// <summary>"Sign out all sessions": every other session of this user ends; this one continues.</summary>
    [HttpPost("sessions/revoke")]
    [Audit(AuditEvents.SessionsRevoked)]
    public async Task<IActionResult> RevokeSessions(CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.NotFound("User");
        await accounts.RevokeSessionsAsync(id, AuditContext.From(HttpContext), ct);
        await RenewSessionAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Step 1 of setting up 2FA: a new secret, pending for 15 minutes (it replaces an earlier pending
    /// one). 409 totp_already_enabled.
    /// </summary>
    [HttpPost("totp/setup")]
    [Audit(AuditEvents.TotpSetupStarted)]
    public async Task<ActionResult<TotpSetup>> SetupTotp(CancellationToken ct) =>
        await twoFactor.SetupAsync(User.UserId() ?? throw ApiException.NotFound("User"), ct);

    /// <summary>
    /// Step 2: a code from the new secret turns 2FA on. Every other session of the user ends; this
    /// one continues. 400 for a wrong code, 409 totp_setup_expired: start again.
    /// </summary>
    [HttpPost("totp/confirm")]
    [Audit(AuditEvents.TotpEnabled)]
    public async Task<IActionResult> ConfirmTotp(TotpCodeRequest request, CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.NotFound("User");
        await twoFactor.ConfirmAsync(id, request.Code, AuditContext.From(HttpContext), ct);
        await RenewSessionAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Turns 2FA off: needs the password and a current code (400 names the wrong one). Every other
    /// session ends. 409 totp_required while Auth:TotpPolicy requires it, totp_not_enabled.
    /// </summary>
    [HttpPost("totp/disable")]
    [Audit(AuditEvents.TotpDisabled, AuditEvents.TotpFailed)]
    public async Task<IActionResult> DisableTotp(TotpDisableRequest request, CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.NotFound("User");
        await twoFactor.DisableAsync(id, request.Password, request.Code, AuditContext.From(HttpContext), ct);
        await RenewSessionAsync(id, ct);
        return NoContent();
    }

    private async Task RenewSessionAsync(Guid id, CancellationToken ct)
    {
        // Cookie sessions only: the tests' sign-in has no cookie to renew.
        if (User.Identity?.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme)
        {
            var user = await accounts.GetAsync(id, ct);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, SessionClaims.Create(user),
                new AuthenticationProperties { IsPersistent = true, IssuedUtc = Clock.UtcNow });
        }
    }
}

/// <summary>User administration (§4.9, §4.10). The last active admin cannot be deleted, demoted or deactivated.</summary>
[Route("api/v1/users")]
[RequireAdmin]
public sealed class UsersController(UserAccounts accounts, PnmtdDbContext db) : V1ControllerBase
{
    private static readonly string[] SortFields = ["username", "admin", "lastlogin", "created"];

    /// <summary>Sort: username (default), admin, lastLogin, created. Search: username and display name. A group: its members.</summary>
    [HttpGet]
    public async Task<ActionResult<Paged<UserItem>>> List([FromQuery] UserQuery query, CancellationToken ct)
    {
        var (field, descending) = Services.Domain.Paging.Sort(query.Sort, "username", SortFields);
        var users = db.Users.AsNoTracking().Include(u => u.Groups).ThenInclude(m => m.Group).AsQueryable();
        if (query.GroupId is { } groupId)
        {
            users = users.Where(u => u.Groups.Any(m => m.GroupId == groupId));
        }
        if (Services.Domain.Text.Pattern(query.Search) is { } pattern)
        {
            users = users.Where(u => EF.Functions.ILike(u.Username, pattern, @"\") || EF.Functions.ILike(u.DisplayName, pattern, @"\"));
        }
        users = (field, descending) switch
        {
            ("admin", false) => users.OrderBy(u => u.IsAdmin).ThenBy(u => u.NormalizedUsername),
            ("admin", true) => users.OrderByDescending(u => u.IsAdmin).ThenBy(u => u.NormalizedUsername),
            ("lastlogin", false) => users.OrderBy(u => u.LastLoginAt).ThenBy(u => u.NormalizedUsername),
            ("lastlogin", true) => users.OrderByDescending(u => u.LastLoginAt).ThenBy(u => u.NormalizedUsername),
            ("created", false) => users.OrderBy(u => u.CreatedAt),
            ("created", true) => users.OrderByDescending(u => u.CreatedAt),
            (_, true) => users.OrderByDescending(u => u.NormalizedUsername),
            _ => users.OrderBy(u => u.NormalizedUsername),
        };
        var page = await Services.Domain.Paging.PageAsync(users, query, ct);
        var ids = page.Items.Select(u => u.Id).ToList();
        var keys = await db.SshKeys.AsNoTracking().Where(k => k.UserId != null && ids.Contains(k.UserId.Value)).GroupBy(k => k.UserId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return new Paged<UserItem>(page.Items.Select(u => ToItem(u) with { SshKeys = keys.GetValueOrDefault(u.Id) }).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserItem>> Get(Guid id, CancellationToken ct) => await WithKeysAsync(id, ct);

    /// <summary>A user's effective rights, with the group each level comes from (§5.5).</summary>
    [HttpGet("{id:guid}/access")]
    public async Task<AccessItem> Access(Guid id, [FromServices] GroupService groups, CancellationToken ct) => await groups.AccessAsync(id, ct);

    /// <summary>The user with their groups and number of SSH keys (docs/linux-sensor-plan.md §6.4).</summary>
    private async Task<UserItem> WithKeysAsync(Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Groups).ThenInclude(m => m.Group).SingleOrDefaultAsync(u => u.Id == id, ct)
            ?? throw ApiException.NotFound("User");
        return ToItem(user) with { SshKeys = await db.SshKeys.CountAsync(k => k.UserId == user.Id, ct) };
    }

    [HttpPost]
    [Audit(AuditEvents.UserCreated)]
    public async Task<ActionResult<CreatedUser>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var (user, temporary) = await accounts.CreateAsync(request.Username, request.DisplayName, request.IsAdmin, request.GroupIds, null,
            AuditContext.From(HttpContext), ct);
        return CreatedAtAction(nameof(Get), new { id = user.Id },
            new CreatedUser(await WithKeysAsync(user.Id, ct), new TemporaryPassword(temporary!, user.TemporaryPasswordExpiresAt!.Value)));
    }

    [HttpPut("{id:guid}")]
    [Audit(AuditEvents.UserUpdated, AuditEvents.UserAdminChanged, AuditEvents.UserGroupsChanged)]
    public async Task<ActionResult<UserItem>> Update(Guid id, UpdateUserRequest request, CancellationToken ct) =>
        await WithKeysAsync((await accounts.UpdateAsync(id, request.DisplayName, request.IsAdmin, request.IsActive, request.GroupIds,
            AuditContext.From(HttpContext), ct)).Id, ct);

    [HttpDelete("{id:guid}")]
    [Audit(AuditEvents.UserDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await accounts.DeleteAsync(id, AuditContext.From(HttpContext), ct);
        return NoContent();
    }

    /// <summary>A temporary password (shown once); the user's sessions end and a lockout is lifted.</summary>
    [HttpPost("{id:guid}/reset-password")]
    [Audit(AuditEvents.PasswordReset)]
    public async Task<ActionResult<TemporaryPassword>> ResetPassword(Guid id, CancellationToken ct)
    {
        var password = await accounts.ResetPasswordAsync(id, null, AuditContext.From(HttpContext), ct);
        var user = await accounts.GetAsync(id, ct);
        return new TemporaryPassword(password!, user.TemporaryPasswordExpiresAt!.Value);
    }

    /// <summary>"Reset 2FA" for a user who lost the authenticator: 2FA off, sessions ended, a lockout lifted.</summary>
    [HttpPost("{id:guid}/reset-totp")]
    [Audit(AuditEvents.TotpReset)]
    public async Task<IActionResult> ResetTotp(Guid id, CancellationToken ct)
    {
        await accounts.ResetTotpAsync(id, AuditContext.From(HttpContext), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/unlock")]
    [Audit(AuditEvents.UserUnlocked)]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct)
    {
        await accounts.UnlockAsync(id, AuditContext.From(HttpContext), ct);
        return NoContent();
    }

    internal static UserItem ToItem(UserEntity u) => new(u.Id, u.Username, u.DisplayName, u.IsAdmin,
        u.Groups.Where(m => m.Group is not null).Select(m => new Ref(m.GroupId, m.Group.Name)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        u.IsActive, u.IsTotpEnabled,
        u.MustChangePassword, u.TemporaryPasswordExpiresAt, u.LockoutEnd is { } end && end > Clock.UtcNow ? end : null,
        u.FailedLoginCount, u.CreatedAt, u.LastLoginAt, u.PasswordChangedAt);
}
