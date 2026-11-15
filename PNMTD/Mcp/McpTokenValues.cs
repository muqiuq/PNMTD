using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PNMTD.Models.Db;

namespace PNMTD.Mcp;

/// <summary>
/// A token's value (docs/mcp-plan.md §4.2): <c>pnmt_ro_</c> or <c>pnmt_rw_</c> and 43 base64url characters, 32 random bytes.
/// The prefix is for people and secret scanners; the server takes the kind from the row the hash matches.
/// </summary>
public static partial class McpTokenValues
{
    public const string ReadPrefix = "pnmt_ro_";
    public const string WritePrefix = "pnmt_rw_";

    public static string New(McpTokenKind kind) =>
        (kind == McpTokenKind.Write ? WritePrefix : ReadPrefix) + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256, lower-case hex: what the database keeps.</summary>
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Hint(string value) => value[^4..];

    /// <summary>Shaped like a token; anything else is refused before the database is asked.</summary>
    public static bool LooksLikeOne(string? value) => value is not null && Pattern().IsMatch(value);

    /// <summary>For <see cref="Ai.SecretScanner"/>: a token anywhere in a text.</summary>
    [GeneratedRegex(@"\bpnmt_r[ow]_[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])")]
    public static partial Regex Anywhere();

    [GeneratedRegex(@"^pnmt_r[ow]_[A-Za-z0-9_-]{43}$")]
    private static partial Regex Pattern();
}
