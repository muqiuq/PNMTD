using System.Text.RegularExpressions;

namespace PNMTD.Services.Domain;

/// <summary>
/// Colours as <c>#rrggbb</c>, lower-case: the availability plan's types and people, and responsibilities
/// (docs/responsibilities-extensions-plan.md §4.2). One check for all of them.
/// </summary>
internal static partial class Colours
{
    public const string Hint = "A colour as #rrggbb.";

    /// <summary>The colour as stored: trimmed and lower-case; null for nothing or blanks.</summary>
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>Whether a normalized value is <c>#rrggbb</c>.</summary>
    public static bool IsHex(string? value) => value is not null && Hex().IsMatch(value);

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex Hex();
}
