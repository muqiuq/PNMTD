using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using PNMTD.Api;

namespace PNMTD.Auth;

/// <summary>The three roles (docs/webui-migration-plan.md §4.10), stored as the role claim.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, User, Viewer];
}

/// <summary>
/// Every v1 controller names one of these at class level, and write actions <see cref="CanWrite"/>
/// (the endpoint audit test checks it).
/// <list type="bullet">
/// <item><see cref="CanRead"/>: Viewer, User, Admin.</item>
/// <item><see cref="CanWrite"/>: User, Admin. Also what only they may see: tokens, sensor URLs, mail contents.</item>
/// <item><see cref="Admin"/>: users, the auth log, key values, system health.</item>
/// </list>
/// </summary>
public static class Policies
{
    public const string CanRead = "CanRead";
    public const string CanWrite = "CanWrite";
    public const string Admin = "Admin";
}

public static class AuthSetup
{
    public const string CookieName = "pnmt.auth";

    /// <summary>The antiforgery cookie token (HttpOnly) …</summary>
    public const string AntiforgeryCookieName = "pnmt.antiforgery";

    /// <summary>… and the request token, readable by the SPA, which sends it back as <see cref="CsrfHeaderName"/>.</summary>
    public const string CsrfCookieName = "XSRF-TOKEN";

    public const string CsrfHeaderName = "X-XSRF-TOKEN";

    /// <summary>The rate limiter policy of the login endpoints (§4.7).</summary>
    public const string LoginRateLimit = "login";

    /// <summary>Where the Data Protection key ring lives: the pnmtd-keys volume in the image.</summary>
    public const string DefaultKeysPath = "/var/lib/pnmtd/keys";

    /// <summary>
    /// The server protects itself, whatever a reverse proxy does (§3.6):
    /// <list type="bullet">
    /// <item>Cookie authentication only. It never redirects: 401 when signed out, 403 for the wrong role.</item>
    /// <item>Deny by default: an endpoint without [AllowAnonymous] requires a signed-in user.</item>
    /// <item>Data Protection keys on the keys volume, so sessions (and in phase 4 the TOTP secrets) survive restarts.</item>
    /// </list>
    /// Sign-in itself arrives with the users table (phase 3).
    /// </summary>
    public static IServiceCollection AddPnmtdAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDataProtection()
            .SetApplicationName("PNMTD")
            .PersistKeysToFileSystem(new DirectoryInfo(configuration["DataProtection:KeysPath"] ?? DefaultKeysPath));

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

        services.AddMemoryCache();
        services.AddSingleton<SessionCache>();
        services.AddScoped<UserAccounts>();
        services.AddScoped<AuthService>();

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
            options.OnRejected = async (context, ct) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromMinutes(1);
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, detail: "Too many sign-in attempts. Try again later.",
                    extensions: new Dictionary<string, object?> { ["code"] = "rate_limited" }).ExecuteAsync(context.HttpContext);
            };
        });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.CanRead, p => p.RequireAuthenticatedUser().RequireRole(Roles.All))
            .AddPolicy(Policies.CanWrite, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin, Roles.User))
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));

        return services;
    }
}

/// <summary>
/// A session that still has to change its password (a temporary one, or the seeded admin's) may only
/// do that, sign out and read who it is (§4.6): every other /api/v1 call is 403 must_change_password.
/// The claim goes when the password changes and the cookie is issued anew.
/// </summary>
public static class MustChangePasswordGate
{
    private static readonly string[] Allowed = ["/api/v1/auth", "/api/v1/account/password", "/api/v1/system/info"];

    public static IApplicationBuilder UseMustChangePasswordGate(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (context.User.HasClaim(SessionClaims.MustChangePassword, "true")
                && path.StartsWithSegments(ApiPaths.V1, StringComparison.OrdinalIgnoreCase)
                && !Allowed.Any(a => path.StartsWithSegments(a, StringComparison.OrdinalIgnoreCase)))
            {
                await Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Change your password first.",
                    extensions: new Dictionary<string, object?> { ["code"] = "must_change_password" }).ExecuteAsync(context);
                return;
            }
            await next(context);
        });
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

/// <summary>No antiforgery check: the login endpoints, which run before there is a session.</summary>
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
