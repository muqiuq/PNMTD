using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace PNMTD.Connectors;

/// <summary>
/// A caller's key (docs/connectors-plan.md §6.2): <c>pnmt_cx_</c> and 43 base64url characters, 32 random bytes, MCP's shape with a
/// prefix of its own. A call finds its caller by the key's SHA-256; the key itself is kept only under Data Protection, to show it again.
/// </summary>
public static partial class ConnectorKeys
{
    public const string Prefix = "pnmt_cx_";

    public static string New() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256, lower-case hex: what the database keeps and looks up.</summary>
    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static string Hint(string key) => key[^4..];

    /// <summary>Shaped like a key; anything else is refused before the database is asked.</summary>
    public static bool LooksLikeOne(string? value) => value is not null && Pattern().IsMatch(value);

    /// <summary>For <see cref="Ai.SecretScanner"/>: a key anywhere in a text.</summary>
    [GeneratedRegex(@"\bpnmt_cx_[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])")]
    public static partial Regex Anywhere();

    [GeneratedRegex(@"^pnmt_cx_[A-Za-z0-9_-]{43}$")]
    private static partial Regex Pattern();
}

/// <summary>The keys under Data Protection (purpose PNMTD.Connectors.Key.v1), in the key ring of the <c>pnmtd-keys</c> volume.</summary>
public sealed class ConnectorKeyProtector(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("PNMTD.Connectors.Key.v1");

    public string Protect(string key) => protector.Protect(key);

    /// <summary>Null when the key ring that protected it is gone (a restore without the volume): the key still works, by its hash.</summary>
    public string? Unprotect(string protectedKey)
    {
        try
        {
            return protector.Unprotect(protectedKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
