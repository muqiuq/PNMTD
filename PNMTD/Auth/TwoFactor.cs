using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Security;
using PNMTD.Models.Db;

namespace PNMTD.Auth;

/// <summary>
/// What Data Protection encrypts for 2FA (docs/webui-migration-plan.md §4.6, §4.7), with the key ring
/// on the keys volume: the TOTP secrets in the users table (purpose PNMTD.Totp.v1), and the ticket of a
/// sign-in that waits for its code (the pnmt.2fa cookie). Losing the keys makes both unreadable:
/// reset-totp.sh then lets the users set 2FA up again.
/// </summary>
public sealed class TotpSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector secrets = provider.CreateProtector("PNMTD.Totp.v1");
    private readonly IDataProtector tickets = provider.CreateProtector("PNMTD.TwoFactorLogin.v1");

    public string Protect(string secret) => secrets.Protect(secret);

    /// <summary>Null when the keys that encrypted it are gone.</summary>
    public string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }
        try
        {
            return secrets.Unprotect(protectedSecret);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pnmt.2fa cookie: who passed the password, with which security stamp, and when. A password
    /// reset or a role change in between (a new stamp) makes it worthless, as does the time limit.
    /// </summary>
    public string IssueTicket(UserEntity user) =>
        tickets.Protect($"{user.Id:N}|{user.SecurityStamp}|{Clock.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)}");

    public (Guid UserId, string Stamp)? ReadTicket(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket))
        {
            return null;
        }
        string payload;
        try
        {
            payload = tickets.Unprotect(ticket);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // A forged or truncated cookie: not Base64url, or not ours.
            return null;
        }
        var parts = payload.Split('|');
        if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "N", out var id)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
        {
            return null;
        }
        var age = Clock.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
        return age < TimeSpan.Zero || age > TwoFactorService.LoginStepLifetime ? null : (id, parts[1]);
    }
}

/// <summary>
/// 2FA for the signed-in user (§4.6): setting it up, and turning it off again.
/// <list type="bullet">
/// <item>Setup stores a new secret as pending, encrypted, for 15 minutes. Only a code from it (confirm)
///   turns 2FA on, so an app that was set up wrongly locks nobody out.</item>
/// <item>Turning it on or off rotates the security stamp: the user's other sessions end.</item>
/// <item>Turning it off needs the password and a code. Without the authenticator, an admin resets it.</item>
/// </list>
/// </summary>
public sealed class TwoFactorService(PnmtdDbContext db, UserAccounts accounts, TotpSecrets secrets, SessionCache sessions,
    IConfiguration configuration)
{
    public static readonly TimeSpan PendingSetupLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long the code step of a sign-in waits after the password (the pnmt.2fa cookie).</summary>
    public static readonly TimeSpan LoginStepLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Auth:TotpIssuer, by default "PNMT (&lt;Identity&gt;)": the name the authenticator app shows.</summary>
    public static string Issuer(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Auth:TotpIssuer"]))
        {
            return configuration["Auth:TotpIssuer"]!.Trim();
        }
        var identity = configuration["Identity"];
        return string.IsNullOrWhiteSpace(identity) ? "PNMT" : $"PNMT ({identity.Trim()})";
    }

    /// <summary>A new pending secret (replacing an earlier one): the SPA shows it as a QR code and in groups of four.</summary>
    public async Task<TotpSetup> SetupAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await accounts.GetAsync(userId, ct);
        if (user.IsTotpEnabled)
        {
            throw ApiException.Conflict("totp_already_enabled", "Two-factor authentication is on already. Turn it off first to use another app.");
        }
        var secret = TotpService.GenerateSecret();
        user.PendingTotpSecretProtected = secrets.Protect(secret);
        user.PendingTotpCreatedAt = Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return new TotpSetup(secret, TotpService.BuildOtpAuthUri(Issuer(configuration), user.Username, secret),
            user.PendingTotpCreatedAt.Value + PendingSetupLifetime);
    }

    /// <summary>A code from the pending secret turns 2FA on. 409 totp_setup_expired: start again.</summary>
    public async Task ConfirmAsync(Guid userId, string? code, AuditContext audit, CancellationToken ct = default)
    {
        var user = await accounts.GetAsync(userId, ct);
        if (user.IsTotpEnabled)
        {
            throw ApiException.Conflict("totp_already_enabled", "Two-factor authentication is on already.");
        }
        var now = Clock.UtcNow;
        var pending = user.PendingTotpCreatedAt is { } created && now - created <= PendingSetupLifetime
            ? secrets.Unprotect(user.PendingTotpSecretProtected)
            : null;
        if (pending is null)
        {
            throw ApiException.Conflict("totp_setup_expired", "The setup has expired (15 minutes). Start it again.");
        }
        if (TotpService.Check(pending, code, now, null, out var step) != TotpCheck.Valid)
        {
            throw ApiException.Invalid("code", "Not the current code. Check that the time on your phone is right.");
        }
        user.TotpSecretProtected = user.PendingTotpSecretProtected;
        user.IsTotpEnabled = true;
        user.TotpLastUsedStep = step;
        user.PendingTotpSecretProtected = null;
        user.PendingTotpCreatedAt = null;
        user.SecurityStamp = UserAccounts.NewStamp();
        accounts.Log(AuthEvents.TotpEnabled, user, audit, null);
        await accounts.SaveAsync(ct);
        sessions.Forget(user.Id);
    }

    /// <summary>
    /// Needs the password and a current code. Refused with 409 totp_required while Auth:TotpPolicy
    /// requires 2FA for the user: the next sign-in would only ask to set it up again.
    /// </summary>
    public async Task DisableAsync(Guid userId, string? password, string? code, AuditContext audit, CancellationToken ct = default)
    {
        var user = await accounts.GetAsync(userId, ct);
        if (!user.IsTotpEnabled)
        {
            throw ApiException.Conflict("totp_not_enabled", "Two-factor authentication is off.");
        }
        if (AuthService.TotpRequired(AuthService.Policy(configuration), user.Role))
        {
            throw ApiException.Conflict("totp_required", "Two-factor authentication is required for your account (Auth:TotpPolicy).");
        }
        var v = new ApiValidator();
        var passwordValid = PasswordHasher.Verify(password, user.PasswordHash);
        if (!passwordValid)
        {
            v.Add("password", "Not your current password.");
        }
        var secret = secrets.Unprotect(user.TotpSecretProtected);
        var check = secret is null ? TotpCheck.Invalid : TotpService.Check(secret, code, Clock.UtcNow, user.TotpLastUsedStep, out _);
        if (check != TotpCheck.Valid)
        {
            v.Add("code", check == TotpCheck.Reused ? "This code was used already. Wait for the next one." : "Not the current code.");
        }
        if (!passwordValid || check != TotpCheck.Valid)
        {
            accounts.Log(AuthEvents.TotpFailed, user, audit, "turning 2FA off refused: " + (passwordValid ? "wrong code" : "wrong password"));
            await accounts.SaveAsync(ct);
        }
        v.ThrowIfInvalid();
        UserAccounts.ClearTotp(user);
        user.SecurityStamp = UserAccounts.NewStamp();
        accounts.Log(AuthEvents.TotpDisabled, user, audit, null);
        await accounts.SaveAsync(ct);
        sessions.Forget(user.Id);
    }
}
