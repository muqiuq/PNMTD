using System.Globalization;
using System.Security.Claims;
using PNMTD.Lib.Helper;

namespace PNMTD.Auth;

/// <summary>
/// The sign-in's settings (docs/mcp-plan.md §3.5), from <c>Auth__…</c>. The older ones (<c>TotpPolicy</c>, <c>TotpIssuer</c>,
/// <c>InitialAdmin</c>, <c>LoginRateLimit</c>) are still read where they are used.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>A session ends after this many days without a request (the cookie's sliding expiry).</summary>
    public int SessionDays { get; set; } = 7;

    /// <summary>The password again at least every this many days; 0: only <see cref="SessionDays"/> apply.</summary>
    public int LoginDays { get; set; } = 30;

    /// <summary>The 2FA code again every this many days, for users who have 2FA; 0: only at sign-in.</summary>
    public int TotpDays { get; set; } = 1;

    /// <summary>The hour (0–23, in <c>TZ</c>'s local time) at which a day ends for both rules.</summary>
    public int ResetHour { get; set; } = 4;
}

/// <summary>
/// When a session has to sign in again, and when the day's 2FA code is due (docs/mcp-plan.md §3.2). Both end at
/// <see cref="AuthOptions.ResetHour"/> in PNMT's time zone, so a day of 23 or 25 hours still ends at 04:00.
/// </summary>
public static class SessionRules
{
    /// <summary>Set on the request whose session the 30 days ended: the 401 says <c>login_expired</c>, not just <c>unauthorized</c>.</summary>
    public const string LoginExpiredItem = "pnmt:login_expired";

    public const string LoginExpiredCode = "login_expired";

    public const string TotpDueCode = "totp_due";

    /// <summary>
    /// The first <paramref name="hour"/>:00 local time strictly after <paramref name="instant"/>, then
    /// <paramref name="days"/> − 1 days later, as a UTC instant.
    /// </summary>
    public static DateTime NextReset(DateTime instant, int hour, int days = 1)
    {
        hour = Math.Clamp(hour, 0, 23);
        var local = Clock.ToLocal(instant);
        var reset = local.Date.AddHours(hour);
        if (reset <= local)
        {
            reset = reset.AddDays(1);
        }
        return Clock.ToUtc(reset.AddDays(Math.Max(days, 1) - 1));
    }

    /// <summary>When a session signed in with the password at <paramref name="signedIn"/> ends; null when the rule is off.</summary>
    public static DateTime? LoginEnds(DateTime signedIn, AuthOptions options) =>
        options.LoginDays <= 0 ? null : NextReset(Clock.ToUtc(Clock.ToLocal(signedIn).AddDays(options.LoginDays)), options.ResetHour);

    /// <summary>When the code entered at <paramref name="totpAt"/> is due again; null when the rule is off.</summary>
    public static DateTime? TotpDue(DateTime totpAt, AuthOptions options) =>
        options.TotpDays <= 0 ? null : NextReset(totpAt, options.ResetHour, options.TotpDays);

    /// <summary>A time claim (<see cref="SessionClaims.SignedIn"/>, <see cref="SessionClaims.TotpAt"/>): Unix seconds.</summary>
    public static DateTime? Time(ClaimsPrincipal? principal, string type) =>
        long.TryParse(principal?.FindFirstValue(type), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    public static Claim TimeClaim(string type, DateTime instant) =>
        new(type, new DateTimeOffset(Clock.AsUtc(instant)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
}
