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

    /// <summary>
    /// The twelve swatches responsibilities offer (docs/responsibilities-extensions-plan.md §4.3), for MCP's <c>responsibility_options</c>
    /// (docs/responsibilities-mcp-and-map-plan.md §6.3). The same as the web UI's <c>palette</c> in <c>components/colours.ts</c>, which
    /// a test compares.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Palette =
    [
        ("Red", "#d94f4f"), ("Orange", "#e8833a"), ("Amber", "#d9a400"), ("Lime", "#7cb342"), ("Green", "#2e9d5b"), ("Teal", "#1fa3a3"),
        ("Blue", "#2f8fd8"), ("Indigo", "#4f5bd5"), ("Violet", "#8e5cd9"), ("Pink", "#d0559b"), ("Brown", "#8d6e63"), ("Slate", "#78909c"),
    ];

    /// <summary>A swatch's name, or null for a custom colour or none.</summary>
    public static string? NameOf(string? hex) => Palette.FirstOrDefault(p => p.Hex == hex).Name;

    /// <summary>The first swatch nobody uses yet, as the page suggests for a new responsibility; the first one when all are taken.</summary>
    public static string FirstUnused(IEnumerable<string?> used)
    {
        var taken = used.OfType<string>().Select(c => c.ToLowerInvariant()).ToHashSet();
        return Palette.FirstOrDefault(p => !taken.Contains(p.Hex)).Hex ?? Palette[0].Hex;
    }

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex Hex();
}
