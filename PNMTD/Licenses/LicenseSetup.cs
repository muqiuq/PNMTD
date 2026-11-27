using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace PNMTD.Licenses;

/// <summary>
/// The <c>Licenses</c> section of .env (docs/licences-plan.md §13): <c>Licenses__Enabled</c>, off by default, and the defaults of new
/// items. Read through <c>IOptionsMonitor</c>; the switch itself through <see cref="LicenseSetup.Enabled"/>.
/// </summary>
public sealed class LicenseOptions
{
    public const string Section = "Licenses";

    /// <summary>The switch (§13.1): off, nothing of the feature is usable or visible, and its data stays.</summary>
    public bool Enabled { get; set; }

    public int WarnDays { get; set; } = 30;

    public int EolWarnDays { get; set; } = 90;

    /// <summary>The owner's reminder this many days before a warning's date (§5.5); 0 is none.</summary>
    public int RemindDays { get; set; } = 7;

    /// <summary>The default rhythm of reviews for new items, in months; 0 is none.</summary>
    public int ReviewMonths { get; set; } = 12;

    /// <summary>The default currency of new prices, and the overview's main currency.</summary>
    public string Currency { get; set; } = "EUR";

    public bool RecordCharges { get; set; } = true;

    /// <summary>After how many days an estimated charge without an invoice is worth a look (§6.7).</summary>
    public int EstimateDays { get; set; } = 45;

    public int MaxAssignments { get; set; } = 1000;
}

public static class LicenseSetup
{
    /// <summary>
    /// The one place the switch is read (§13.1): <c>Licenses__Enabled</c>, false unless set. Everything of §13.2 asks it, on the server
    /// and, through <c>GET /system/info</c>, in the browser.
    /// </summary>
    public static bool Enabled(IConfiguration configuration) => configuration.GetValue($"{LicenseOptions.Section}:Enabled", false);

    public static IServiceCollection AddLicenses(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LicenseOptions>(configuration.GetSection(LicenseOptions.Section));
        services.AddSingleton<LicenseKeyProtector>();
        services.AddSingleton<LicenseEvaluator>();
        services.AddSingleton<LicenseEvaluatorTask>();
        services.AddScoped<LicenseQueries>();
        services.AddScoped<LicenseService>();
        services.AddSingleton<Notifications.IUserNotificationRenderer, LicenseWarningNotice>();
        services.AddSingleton<Notifications.IUserNotificationRenderer, LicenseReviewNotice>();
        services.AddSingleton<Notifications.IUserNotificationRenderer, LicenseUnownedNotice>();
        return services;
    }

    /// <summary>
    /// Every endpoint marked <see cref="RequiresLicensesAttribute"/> answers 404 <c>licenses_disabled</c> while the feature is off (§13.2):
    /// after routing, before authentication and authorization, so for everyone, admins too, and without a denied entry.
    /// </summary>
    public static IApplicationBuilder UseLicensesSwitch(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresLicensesAttribute>() is not null
                && !Enabled(context.RequestServices.GetRequiredService<IConfiguration>()))
            {
                await Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: Disabled, extensions: new Dictionary<string, object?>
                {
                    ["code"] = DisabledCode,
                }).ExecuteAsync(context);
                return;
            }
            await next(context);
        });

    public const string DisabledCode = "licenses_disabled";

    public const string Disabled = "Licences & services is turned off on this installation (Licenses__Enabled).";
}

/// <summary>On a controller: its endpoints exist only while <c>Licenses__Enabled</c> is on (docs/licences-plan.md §13.2).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresLicensesAttribute : Attribute;

/// <summary>The keys at rest (§3.4): a Data Protection protector of their own, as the connectors' keys have.</summary>
public sealed class LicenseKeyProtector(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("PNMTD.Licenses.Key.v1");

    public string Protect(string key) => protector.Protect(key);

    /// <summary>Null when the key ring that protected it is gone: the key has to be entered again (409 <c>key_lost</c>).</summary>
    public string? Unprotect(string? protectedKey)
    {
        if (string.IsNullOrEmpty(protectedKey))
        {
            return null;
        }
        try
        {
            return protector.Unprotect(protectedKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The last five letters and digits, upper case, separators ignored: what Windows reports as <c>PartialKey</c> (§3.4).</summary>
    public static string? Hint(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        var chars = key.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray();
        return chars.Length == 0 ? null : new string(chars[Math.Max(0, chars.Length - 5)..]);
    }
}
