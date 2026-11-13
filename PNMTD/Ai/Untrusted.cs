using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PNMTD.Ai;

/// <summary>
/// Layer 1 (docs/ai-operations-plan.md §3.4): untrusted text as plain, visible text. HTML becomes text; invisible and
/// control characters go (zero-width, bidi overrides, Unicode tag characters), and are counted; Unicode is normalised
/// (NFC) and whitespace collapsed; the size is capped, keeping the start and the end.
/// </summary>
public static partial class InputSanitizer
{
    public sealed record Cleaned(string Text, int InvisibleRemoved, bool Truncated);

    public static Cleaned Clean(string? input, int maxChars)
    {
        var text = input ?? "";
        if (LooksLikeHtml(text))
        {
            text = HtmlToText(text);
        }
        var builder = new StringBuilder(text.Length);
        var removed = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var cp = char.ConvertToUtf32(c, text[i + 1]);
                i++;
                // Tag characters (U+E0000–U+E007F) hide text from people and not from models.
                if (cp is >= 0xE0000 and <= 0xE007F)
                {
                    removed++;
                    continue;
                }
                builder.Append(char.ConvertFromUtf32(cp));
                continue;
            }
            if (Invisible(c))
            {
                removed++;
                continue;
            }
            builder.Append(c);
        }
        var normalised = Whitespace().Replace(builder.ToString().Normalize(NormalizationForm.FormC), " ");
        normalised = SpacedBreaks().Replace(normalised, "\n");
        normalised = BlankLines().Replace(normalised, "\n\n").Trim();
        var truncated = normalised.Length > maxChars;
        return new Cleaned(truncated ? HeadAndTail(normalised, maxChars / 2) : normalised, removed, truncated);
    }

    /// <summary>The first and the last <paramref name="half"/> characters, with a marker between.</summary>
    public static string HeadAndTail(string text, int half) =>
        text.Length <= half * 2 ? text : text[..half] + "\n[… shortened …]\n" + text[^half..];

    private static bool Invisible(char c) =>
        c is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u00AD' or '\u180E'
        || c is >= '\u202A' and <= '\u202E'
        || c is >= '\u2066' and <= '\u2069'
        || (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
        || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;

    private static bool LooksLikeHtml(string text) => HtmlTag().IsMatch(text);

    private static string HtmlToText(string html)
    {
        var withoutHidden = HiddenBlocks().Replace(html, " ");
        var withBreaks = BlockTags().Replace(withoutHidden, "\n");
        return WebUtility.HtmlDecode(AnyTag().Replace(withBreaks, " "));
    }

    [GeneratedRegex(@"<\s*(html|body|div|p|br|table|span|a)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HiddenBlocks();

    [GeneratedRegex(@"<\s*(br|/p|/div|/tr|/li|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"[ \t]*\n[ \t]*")]
    private static partial Regex SpacedBreaks();
}

/// <summary>
/// Layer 2 (§3.4): what an injection usually looks like, as flags and a score from 0 to 1. A high score skips the
/// models; lower ones travel with the input to the decision.
/// </summary>
public static partial class InjectionScanner
{
    public sealed record Scan(IReadOnlyList<string> Flags, double Score)
    {
        public static readonly Scan None = new([], 0);
    }

    /// <summary>At or above this score, no model sees the text.</summary>
    public const double BlockScore = 0.8;

    private static readonly (Regex Pattern, string Flag, double Weight)[] Rules =
    [
        (Ignore(), "asks to ignore instructions", 0.6),
        (SystemPrompt(), "mentions a system prompt", 0.4),
        (YouAreNow(), "tries to give the model a role", 0.4),
        (NewInstructions(), "announces new instructions", 0.5),
        (RoleMarker(), "contains a chat role marker", 0.6),
        (Base64Blob(), "contains a long encoded blob", 0.2),
    ];

    public static Scan Check(string text, int invisibleRemoved = 0)
    {
        var flags = new List<string>();
        var score = 0.0;
        foreach (var (pattern, flag, weight) in Rules)
        {
            if (pattern.IsMatch(text))
            {
                flags.Add(flag);
                score += weight;
            }
        }
        if (Links().Count(text) > 20)
        {
            flags.Add("has many links");
            score += 0.1;
        }
        if (invisibleRemoved > 10)
        {
            flags.Add($"had {invisibleRemoved} invisible characters");
            score += invisibleRemoved > 100 ? 0.5 : 0.3;
        }
        return new Scan(flags, Math.Min(1.0, Math.Round(score, 2)));
    }

    [GeneratedRegex(@"\b(ignore|disregard|forget|override)\b.{0,40}\b(previous|prior|above|earlier|all|your|the)\b.{0,20}\b(instructions?|prompts?|rules?|directions?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline, 250)]
    private static partial Regex Ignore();

    [GeneratedRegex(@"\b(system|developer)\s+(prompt|message|instructions?)\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex SystemPrompt();

    [GeneratedRegex(@"\b(you are now|act as|pretend to be|from now on you)\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex YouAreNow();

    [GeneratedRegex(@"\b(new|updated|real|actual)\s+instructions?\s*[:\-]|\bBEGIN\s+(INSTRUCTIONS|PROMPT)\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex NewInstructions();

    [GeneratedRegex(@"<\|im_(start|end)\|>|<\|(system|assistant|user)\|>|^\s*#{2,}\s*(system|assistant)\b|^\s*(assistant|system)\s*:", RegexOptions.IgnoreCase | RegexOptions.Multiline, 250)]
    private static partial Regex RoleMarker();

    [GeneratedRegex(@"[A-Za-z0-9+/=]{400,}", RegexOptions.None, 250)]
    private static partial Regex Base64Blob();

    [GeneratedRegex(@"https?://", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Links();
}
