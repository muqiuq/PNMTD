namespace PNMTD.Api.V1.Contracts;

/// <summary><c>POST /auth/login</c>.</summary>
public sealed class LoginRequest
{
    public string? Username { get; init; }

    public string? Password { get; init; }
}

/// <summary><c>POST /auth/login/totp</c>: the code from the authenticator app (6 digits, spaces allowed).</summary>
public sealed class TotpCodeRequest
{
    public string? Code { get; init; }
}

/// <summary>
/// After a login step: <c>next</c> is <c>done</c> (signed in, <see cref="User"/> and <see cref="Flags"/>
/// set) or <c>totp</c> (the code is next, within 5 minutes, at <c>/auth/login/totp</c>).
/// </summary>
public sealed record LoginResponse(string Next, CurrentUser? User, SessionFlags? Flags);

/// <summary><c>GET /auth/me</c>: who is signed in. It also issues the XSRF-TOKEN cookie for unsafe calls.</summary>
public sealed record CurrentUser(Guid Id, string Username, string DisplayName, string Role, bool IsTotpEnabled);

/// <summary>
/// What the SPA must do before anything else: change the password (every other call is 403
/// must_change_password until then), set up 2FA because the policy requires it, or offer to.
/// </summary>
public sealed record SessionFlags(bool MustChangePassword, bool TotpSetupRequired, bool TotpSetupSuggested);

public sealed record MeResponse(CurrentUser User, SessionFlags Flags);

/// <summary><c>POST /account/password</c>: the current password and a new one (10 to 256 characters).</summary>
public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; init; }

    public string? NewPassword { get; init; }
}

/// <summary>
/// <c>POST /account/totp/setup</c>: a new secret for the authenticator app, as a QR code
/// (<see cref="OtpauthUri"/>) or typed (<see cref="Secret"/>, Base32). It turns 2FA on only when
/// <c>/account/totp/confirm</c> gets a code from it before <see cref="ExpiresAt"/>.
/// </summary>
public sealed record TotpSetup(string Secret, string OtpauthUri, DateTime ExpiresAt);

/// <summary><c>POST /account/totp/disable</c>: the password and a current code.</summary>
public sealed class TotpDisableRequest
{
    public string? Password { get; init; }

    public string? Code { get; init; }
}

/// <summary>
/// A user as admins see it. LockedUntil is set while a lockout lasts. <see cref="SshKeys"/>: the keys registered for the user in the
/// key manager (docs/linux-sensor-plan.md §6.4).
/// </summary>
public sealed record UserItem(
    Guid Id,
    string Username,
    string DisplayName,
    string Role,
    bool IsActive,
    bool IsTotpEnabled,
    bool MustChangePassword,
    DateTime? TemporaryPasswordExpiresAt,
    DateTime? LockedUntil,
    int FailedLoginCount,
    DateTime CreatedAt,
    DateTime? LastLoginAt,
    DateTime? PasswordChangedAt,
    int SshKeys = 0);

/// <summary><c>POST /users</c>: the new user gets a temporary password, which the response shows once.</summary>
public sealed class CreateUserRequest
{
    public string? Username { get; init; }

    public string? DisplayName { get; init; }

    public string? Role { get; init; } = Auth.Roles.User;
}

/// <summary><c>PUT /users/{id}</c>. The username stays.</summary>
public sealed class UpdateUserRequest
{
    public string? DisplayName { get; init; }

    public string? Role { get; init; }

    public bool IsActive { get; init; } = true;
}

/// <summary>A temporary password: shown once, valid until <see cref="ExpiresAt"/>, changed at the next sign-in.</summary>
public sealed record TemporaryPassword(string Password, DateTime ExpiresAt);

public sealed record CreatedUser(UserItem User, TemporaryPassword TemporaryPassword);


