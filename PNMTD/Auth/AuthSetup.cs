using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

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
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.CanRead, p => p.RequireAuthenticatedUser().RequireRole(Roles.All))
            .AddPolicy(Policies.CanWrite, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin, Roles.User))
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));

        return services;
    }
}
