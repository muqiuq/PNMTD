using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PNMTD.Api;

namespace PNMTD.Auth;

public static class AuthSetup
{
    public const string CookieName = "pnmt.auth";

    /// <summary>A sign-in between the password and the code (§4.5): 5 minutes, on the auth endpoints only.</summary>
    public const string TwoFactorCookieName = "pnmt.2fa";

    public const string TwoFactorCookiePath = "/api/v1/auth";

    /// <summary>The antiforgery cookie token (HttpOnly) …</summary>
    public const string AntiforgeryCookieName = "pnmt.antiforgery";

    /// <summary>… and the request token, readable by the SPA, which sends it back as <see cref="CsrfHeaderName"/>.</summary>
    public const string CsrfCookieName = "XSRF-TOKEN";

    public const string CsrfHeaderName = "X-XSRF-TOKEN";

    /// <summary>The rate limiter policy of the login endpoints (§4.7).</summary>
    public const string LoginRateLimit = "login";

    /// <summary>The debug page's test sends (docs/ai-operations-plan.md §8.1): 10 a minute per user.</summary>
    public const string DebugRateLimit = "debug";

    /// <summary>Where the Data Protection key ring lives: the pnmtd-keys volume in the image.</summary>
    public const string DefaultKeysPath = "/var/lib/pnmtd/keys";

    /// <summary>
    /// The key ring on the keys volume, under the application name PNMTD: the server, and the CLI where
    /// it decrypts what the server encrypted (PNMTD's SSH key for <c>pnmtd mikrotik poll</c>).
    /// </summary>
    public static IDataProtectionBuilder AddPnmtdDataProtection(this IServiceCollection services, IConfiguration configuration) =>
        services.AddDataProtection()
            .SetApplicationName("PNMTD")
            .PersistKeysToFileSystem(new DirectoryInfo(configuration["DataProtection:KeysPath"] ?? DefaultKeysPath));

    /// <summary>
    /// The server protects itself, whatever a reverse proxy does (§3.6):
    /// <list type="bullet">
    /// <item>Cookie authentication only. It never redirects: 401 when signed out, 403 for the wrong role.</item>
    /// <item>Deny by default: an endpoint without [AllowAnonymous] requires a signed-in user.</item>
    /// <item>Data Protection keys on the keys volume, so sessions and the TOTP secrets survive restarts.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddPnmtdAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPnmtdDataProtection(configuration);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                // Secure when the request is HTTPS, which behind a proxy needs the Proxy setting.
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(configuration.GetValue("Auth:SessionDays", 7));
                options.SlidingExpiration = true;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToLogout = _ => Task.CompletedTask;
                options.Events.OnRedirectToReturnUrl = _ => Task.CompletedTask;
                options.Events.OnValidatePrincipal = SessionCache.ValidateAsync;
            });

        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.AddMemoryCache();
        services.AddSingleton<SessionCache>();
        services.AddScoped<UserAccounts>();
        services.AddScoped<AuthService>();
        services.AddSingleton<TotpSecrets>();
        services.AddScoped<TwoFactorService>();

        // The double-submit pattern for the SPA: a cookie token, and a request token the SPA echoes
        // in a header on every unsafe call (CsrfProtectionAttribute). Issued by /auth/me and login.
        services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeaderName;
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.Path = "/";
            // The security headers set X-Frame-Options for the API already.
            options.SuppressXFrameOptionsHeader = true;
        });

        // 10 attempts a minute per client address (Auth:LoginRateLimit:PermitLimit, read when a
        // client first comes), on the login endpoints only. Behind a reverse proxy the address needs
        // the Proxy setting.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Auth:LoginRateLimit:PermitLimit", 10),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            options.AddPolicy(DebugRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = async (context, ct) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromMinutes(1);
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, detail: context.HttpContext.Request.Path.StartsWithSegments("/api/v1/debug")
                        ? "Too many test sends: at most 10 a minute. Try again later." : "Too many sign-in attempts. Try again later.",
                    extensions: new Dictionary<string, object?> { ["code"] = "rate_limited" }).ExecuteAsync(context.HttpContext);
            };
        });

        // Rights, not roles (docs/audit-redmine-zabbix-plan.md §5.4): [RequireRight(area, level)] and [RequireAdmin] become
        // policies; the request's rights come from its session (SessionCache.ValidateAsync).
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddSingleton<IAuthorizationPolicyProvider, Rights.RightPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, Rights.RightHandler>();
        services.AddScoped<GroupService>();

        return services;
    }
}

/// <summary>
/// What a session must do before anything else (§4.6). Every other /api/v1 call is 403 with the code:
/// <list type="bullet">
/// <item>must_change_password (a temporary password, or the seeded admin's): it may only change it,
///   sign out and read who it is. The claim goes when the password changes and the cookie is issued anew.</item>
/// <item>totp_setup_required (Auth:TotpPolicy, and no 2FA yet): it may also set 2FA up. The password
///   comes first, so this one allows changing it.</item>
/// <item>totp_due (docs/mcp-plan.md §3.3): the day's code. Only who it is, signing out, and the code itself.</item>
/// </list>
/// A request that passes them is the person being online (§3.4): <see cref="LastSeen"/>.
/// </summary>
public static class SessionGates
{
    private static readonly (string Claim, string Code, string Detail, string[] Allowed)[] Gates =
    [
        (SessionClaims.MustChangePassword, "must_change_password", "Change your password first.",
            ["/api/v1/auth", "/api/v1/account/password", "/api/v1/system/info"]),
        (SessionClaims.TotpSetupPending, "totp_setup_required", "Set up two-factor authentication first.",
            ["/api/v1/auth", "/api/v1/account/password", "/api/v1/account/totp", "/api/v1/system/info"]),
        (SessionClaims.TotpDue, SessionRules.TotpDueCode, "Enter your 2FA code for today.",
            ["/api/v1/auth", "/api/v1/system/info"]),
    ];

    public static IApplicationBuilder UseSessionGates(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments(ApiPaths.V1, StringComparison.OrdinalIgnoreCase))
            {
                var gated = false;
                foreach (var (claim, code, detail, allowed) in Gates)
                {
                    if (!context.User.HasClaim(claim, "true"))
                    {
                        continue;
                    }
                    gated = true;
                    if (!allowed.Any(a => path.StartsWithSegments(a, StringComparison.OrdinalIgnoreCase)))
                    {
                        await Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: detail,
                            extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(context);
                        return;
                    }
                }
                if (!gated)
                {
                    await LastSeen.TouchAsync(context);
                }
            }
            await next(context);
        });
}

/// <summary>
/// <c>users.LastSeenAt</c> (docs/mcp-plan.md §3.4): a request of the web UI that no gate holds up. Written at most every
/// <see cref="Every"/> per user, so a page's polling costs one small update in five minutes, not one per request.
/// Only people's sessions count: not MCP tokens, not the CLI.
/// </summary>
public static class LastSeen
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    /// <summary>The authentication types that are a person in the web UI (and the tests' sign-in, which stands for it).</summary>
    public static bool IsPerson(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true, AuthenticationType: { } type } && !string.Equals(type, Mcp.McpTokenDefaults.Scheme, StringComparison.Ordinal);

    public static async Task TouchAsync(HttpContext context)
    {
        if (!IsPerson(context.User) || context.User.UserId() is not { } id)
        {
            return;
        }
        var cache = context.RequestServices.GetRequiredService<IMemoryCache>();
        var key = $"last-seen:{id}";
        var now = PNMTD.Lib.Helper.Clock.UtcNow;
        // Compared on PNMT's clock, not the cache's own, so the rule follows Clock like every other one.
        if (cache.TryGetValue(key, out DateTime written) && now - written < Every)
        {
            return;
        }
        cache.Set(key, now, TimeSpan.FromHours(1));
        // A bulk update: no tracked change, so no audit entry for being online.
        await context.RequestServices.GetRequiredService<Data.PnmtdDbContext>().Users.Where(u => u.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, now), context.RequestAborted);
    }
}

/// <summary>
/// Unsafe v1 calls with a session cookie must carry the request token from the XSRF-TOKEN cookie in
/// X-XSRF-TOKEN (§4.7). Requests without the cookie session (signed out: 401 before this runs) are
/// not affected, nor are actions marked <see cref="SkipCsrfAttribute"/> (the login).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CsrfProtectionAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var unsafeMethod = !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method) || HttpMethods.IsTrace(http.Request.Method));
        if (unsafeMethod
            && http.User.Identity is { IsAuthenticated: true, AuthenticationType: CookieAuthenticationDefaults.AuthenticationScheme }
            && context.ActionDescriptor.EndpointMetadata.OfType<SkipCsrfAttribute>().FirstOrDefault() is null)
        {
            try
            {
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Forbidden",
                    Detail = $"The {CsrfHeaderName} header is missing or does not match the XSRF-TOKEN cookie.",
                    Extensions = { ["code"] = "csrf_token_invalid" },
                })
                { StatusCode = StatusCodes.Status403Forbidden, ContentTypes = { "application/problem+json" } };
                return;
            }
        }
        await next();
    }

    private const string CsrfHeaderName = AuthSetup.CsrfHeaderName;
}

/// <summary>No antiforgery check: the login steps, which run before there is a session.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SkipCsrfAttribute : Attribute;

/// <summary>Issues the antiforgery tokens for the session in <paramref name="http"/>'s User.</summary>
public static class CsrfTokens
{
    public static void Issue(HttpContext http)
    {
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        http.Response.Cookies.Append(AuthSetup.CsrfCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/",
        });
    }
}
