using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PNMTD.Lib.Security;

/// <summary>The result of checking a code: valid, wrong, or right but already used (a replay).</summary>
public enum TotpCheck { Valid, Invalid, Reused }

/// <summary>
/// Time-based one-time passwords (RFC 6238: HMAC-SHA1, 6 digits, 30 seconds), as authenticator apps
/// make them. Ported from PortUpAcademy (docs/webui-migration-plan.md §4.6), with the matched time
/// step returned for replay protection: a code counts only if its step is later than the last one used.
/// </summary>
public static class TotpService
{
    public const int Digits = 6;

    public const int PeriodSeconds = 30;

    /// <summary>One step either way, for a phone's clock that is a little off.</summary>
    public const int AllowedDriftSteps = 1;

    /// <summary>160 bits, as RFC 4226 recommends: 32 Base32 characters.</summary>
    public static string GenerateSecret(int byteLength = 20) => Base32.Encode(RandomNumberGenerator.GetBytes(byteLength));

    public static long StepAt(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / PeriodSeconds;

    public static string ComputeCode(string base32Secret, DateTime utc, int digits = Digits) =>
        ComputeCodeForStep(Base32.Decode(base32Secret), StepAt(utc), digits);

    /// <summary>
    /// Checks <paramref name="code"/> (spaces allowed) against the steps around <paramref name="utc"/>.
    /// A match whose step is not later than <paramref name="lastUsedStep"/> is <see cref="TotpCheck.Reused"/>.
    /// <paramref name="matchedStep"/> is the step to store as the last one used.
    /// </summary>
    public static TotpCheck Check(string base32Secret, string? code, DateTime utc, long? lastUsedStep, out long matchedStep)
    {
        matchedStep = 0;
        var normalized = (code ?? "").Replace(" ", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(base32Secret) || normalized.Length != Digits || !normalized.All(char.IsAsciiDigit))
        {
            return TotpCheck.Invalid;
        }

        byte[] key;
        try
        {
            key = Base32.Decode(base32Secret);
        }
        catch (FormatException)
        {
            return TotpCheck.Invalid;
        }

        var typed = Encoding.ASCII.GetBytes(normalized);
        var now = StepAt(utc);
        var result = TotpCheck.Invalid;
        // Every candidate is computed and compared, so the time taken says nothing about which matched.
        for (var step = now - AllowedDriftSteps; step <= now + AllowedDriftSteps; step++)
        {
            var candidate = Encoding.ASCII.GetBytes(ComputeCodeForStep(key, step, Digits));
            if (CryptographicOperations.FixedTimeEquals(candidate, typed) && result != TotpCheck.Valid)
            {
                if (lastUsedStep is { } last && step <= last)
                {
                    result = TotpCheck.Reused;
                }
                else
                {
                    result = TotpCheck.Valid;
                    matchedStep = step;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The enrollment URI the QR code carries (Key URI Format). <paramref name="issuer"/> tells
    /// several PNMT instances apart in the app; a colon in it would end the label's issuer part.
    /// </summary>
    public static string BuildOtpAuthUri(string issuer, string accountName, string base32Secret)
    {
        var cleanIssuer = string.Join(' ', issuer.Split([':', ' '], StringSplitOptions.RemoveEmptyEntries));
        var label = $"{Uri.EscapeDataString(cleanIssuer)}:{Uri.EscapeDataString(accountName)}";
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={Uri.EscapeDataString(cleanIssuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    public static string ComputeCodeForStep(byte[] key, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        var modulus = 1;
        for (var i = 0; i < digits; i++)
        {
            modulus *= 10;
        }
        return (binary % modulus).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}
