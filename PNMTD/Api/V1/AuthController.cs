using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1;

/// <summary>Sign-in and sign-out (docs/webui-migration-plan.md §4.5, §4.9).</summary>
[Route("api/v1/auth")]
[Authorize(Policy = Policies.CanRead)]
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
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (User.UserId() is { } id && await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct) is { } user)
        {
            accounts.Log(AuthEvents.Logout, user, AuditContext.From(HttpContext), null);
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
        return new MeResponse(ToCurrentUser(user), Flags(user));
    }

    private async Task<LoginResponse> SignInAsync(UserEntity user)
    {
        var principal = SessionClaims.Create(user);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = Clock.UtcNow });
        // The request tokens are bound to the user: issued for the new session, not the anonymous one.
        HttpContext.User = principal;
        CsrfTokens.Issue(HttpContext);
        return new LoginResponse("done", ToCurrentUser(user), Flags(user));
    }

    private SessionFlags Flags(UserEntity user)
    {
        var (mustChange, totpRequired, totpSuggested) = AuthService.Flags(user, AuthService.Policy(configuration));
        return new SessionFlags(mustChange, totpRequired, totpSuggested);
    }

    internal static CurrentUser ToCurrentUser(UserEntity user) =>
        new(user.Id, user.Username, user.DisplayName, user.Role, user.IsTotpEnabled);
}

/// <summary>The signed-in user's own account (§4.9).</summary>
[Route("api/v1/account")]
[Authorize(Policy = Policies.CanRead)]
public sealed class AccountController(UserAccounts accounts, TwoFactorService twoFactor) : V1ControllerBase
{
    /// <summary>Ends the user's other sessions; this one continues with a fresh cookie.</summary>
    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.NotFound("User");
        await accounts.ChangePasswordAsync(id, request.CurrentPassword, request.NewPassword, AuditContext.From(HttpContext), ct);
        await RenewSessionAsync(id, ct);
        return NoContent();
    }

    /// <summary>"Sign out all sessions": every other session of this user ends; this one continues.</summary>
    [HttpPost("sessions/revoke")]
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
    public async Task<ActionResult<TotpSetup>> SetupTotp(CancellationToken ct) =>
        await twoFactor.SetupAsync(User.UserId() ?? throw ApiException.NotFound("User"), ct);

    /// <summary>
    /// Step 2: a code from the new secret turns 2FA on. Every other session of the user ends; this
    /// one continues. 400 for a wrong code, 409 totp_setup_expired: start again.
    /// </summary>
    [HttpPost("totp/confirm")]
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
[Authorize(Policy = Policies.Admin)]
public sealed class UsersController(UserAccounts accounts, PnmtdDbContext db) : V1ControllerBase
{
    private static readonly string[] SortFields = ["username", "role", "lastlogin", "created"];

    /// <summary>Sort: username (default), role, lastLogin, created. Search: username and display name.</summary>
    [HttpGet]
    public async Task<ActionResult<Paged<UserItem>>> List([FromQuery] PageQuery query, CancellationToken ct)
    {
        var (field, descending) = Services.Domain.Paging.Sort(query.Sort, "username", SortFields);
        var users = db.Users.AsNoTracking();
        if (Services.Domain.Text.Pattern(query.Search) is { } pattern)
        {
            users = users.Where(u => EF.Functions.ILike(u.Username, pattern, @"\") || EF.Functions.ILike(u.DisplayName, pattern, @"\"));
        }
        users = (field, descending) switch
        {
            ("role", false) => users.OrderBy(u => u.Role).ThenBy(u => u.NormalizedUsername),
            ("role", true) => users.OrderByDescending(u => u.Role).ThenBy(u => u.NormalizedUsername),
            ("lastlogin", false) => users.OrderBy(u => u.LastLoginAt).ThenBy(u => u.NormalizedUsername),
            ("lastlogin", true) => users.OrderByDescending(u => u.LastLoginAt).ThenBy(u => u.NormalizedUsername),
            ("created", false) => users.OrderBy(u => u.CreatedAt),
            ("created", true) => users.OrderByDescending(u => u.CreatedAt),
            (_, true) => users.OrderByDescending(u => u.NormalizedUsername),
            _ => users.OrderBy(u => u.NormalizedUsername),
        };
        var page = await Services.Domain.Paging.PageAsync(users, query, ct);
        return new Paged<UserItem>(page.Items.Select(ToItem).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserItem>> Get(Guid id, CancellationToken ct) => ToItem(await accounts.GetAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<CreatedUser>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var (user, temporary) = await accounts.CreateAsync(request.Username, request.DisplayName, request.Role, null, AuditContext.From(HttpContext), ct);
        return CreatedAtAction(nameof(Get), new { id = user.Id },
            new CreatedUser(ToItem(user), new TemporaryPassword(temporary!, user.TemporaryPasswordExpiresAt!.Value)));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<UserItem>> Update(Guid id, UpdateUserRequest request, CancellationToken ct) =>
        ToItem(await accounts.UpdateAsync(id, request.DisplayName, request.Role, request.IsActive, AuditContext.From(HttpContext), ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await accounts.DeleteAsync(id, AuditContext.From(HttpContext), ct);
        return NoContent();
    }

    /// <summary>A temporary password (shown once); the user's sessions end and a lockout is lifted.</summary>
    [HttpPost("{id:guid}/reset-password")]
    public async Task<ActionResult<TemporaryPassword>> ResetPassword(Guid id, CancellationToken ct)
    {
        var password = await accounts.ResetPasswordAsync(id, null, AuditContext.From(HttpContext), ct);
        var user = await accounts.GetAsync(id, ct);
        return new TemporaryPassword(password!, user.TemporaryPasswordExpiresAt!.Value);
    }

    /// <summary>"Reset 2FA" for a user who lost the authenticator: 2FA off, sessions ended, a lockout lifted.</summary>
    [HttpPost("{id:guid}/reset-totp")]
    public async Task<IActionResult> ResetTotp(Guid id, CancellationToken ct)
    {
        await accounts.ResetTotpAsync(id, AuditContext.From(HttpContext), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/unlock")]
    public async Task<IActionResult> Unlock(Guid id, CancellationToken ct)
    {
        await accounts.UnlockAsync(id, AuditContext.From(HttpContext), ct);
        return NoContent();
    }

    internal static UserItem ToItem(UserEntity u) => new(u.Id, u.Username, u.DisplayName, u.Role, u.IsActive, u.IsTotpEnabled,
        u.MustChangePassword, u.TemporaryPasswordExpiresAt, u.LockoutEnd is { } end && end > Clock.UtcNow ? end : null,
        u.FailedLoginCount, u.CreatedAt, u.LastLoginAt, u.PasswordChangedAt);
}

/// <summary>The audit log (§4.3): admins only.</summary>
[Route("api/v1/auth-log")]
[Authorize(Policy = Policies.Admin)]
public sealed class AuthLogController(PnmtdDbContext db) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<AuthLogItem>>> List([FromQuery] AuthLogQuery query, CancellationToken ct)
    {
        new ApiValidator()
            .Check(query.Type is null || AuthEvents.All.Contains(query.Type), "type", "One of: " + string.Join(", ", AuthEvents.All) + ".")
            .ThrowIfInvalid();
        var log = db.AuthLog.AsNoTracking();
        if (query.UserId is { } userId)
        {
            log = log.Where(l => l.UserId == userId);
        }
        if (query.Type is { } type)
        {
            log = log.Where(l => l.EventType == type);
        }
        if (Services.Domain.Text.Pattern(query.Search) is { } pattern)
        {
            log = log.Where(l => EF.Functions.ILike(l.Username, pattern, @"\") || (l.Detail != null && EF.Functions.ILike(l.Detail, pattern, @"\")));
        }
        return await Services.Domain.Paging.PageAsync(log.OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select(l => new AuthLogItem(l.Id, l.CreatedAt, l.UserId, l.Username, l.EventType, l.IpAddress, l.UserAgent, l.Detail)), query, ct);
    }
}
