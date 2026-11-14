using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PNMTD.Knowledge;

/// <summary>
/// Replacing a leaked text with <c>[redacted]</c> (docs/locations-knowledge-plan.md §6.3), wherever a page's text was
/// copied: plain text (titles, content, notes, tags, change lists), and JSON bodies (the AI request log), where it may
/// stand escaped once (a JSON string) or twice (JSON inside a JSON string, as a task's input inside a chat request), by
/// the relaxed and the default encoder, with <c>\u</c> escapes in either case.
/// </summary>
public sealed partial class KbRedaction
{
    public const string Replacement = "[redacted]";

    private readonly string text;
    private readonly Regex json;

    public KbRedaction(string text)
    {
        this.text = text;
        var relaxed = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string Escape(string s, JsonSerializerOptions? o) => JsonSerializer.Serialize(s, o)[1..^1];
        var once = new[] { Escape(text, null), Escape(text, relaxed) };
        var forms = new[] { text }.Concat(once).Concat(once.SelectMany(f => new[] { Escape(f, null), Escape(f, relaxed) }))
            .Distinct(StringComparer.Ordinal).OrderByDescending(f => f.Length);
        json = new Regex(string.Join("|", forms.Select(Pattern)), RegexOptions.None, TimeSpan.FromSeconds(5));
    }

    /// <summary>A form as a pattern: literal, but a <c>ü</c> escape matches in either case.</summary>
    private static string Pattern(string form)
    {
        var pattern = new StringBuilder();
        var last = 0;
        foreach (Match escape in UnicodeEscape().Matches(form))
        {
            pattern.Append(Regex.Escape(form[last..escape.Index]));
            pattern.Append(@"\\u(?i:").Append(escape.Value[2..]).Append(')');
            last = escape.Index + escape.Length;
        }
        return pattern.Append(Regex.Escape(form[last..])).ToString();
    }

    public int Count(string? haystack)
    {
        if (string.IsNullOrEmpty(haystack))
        {
            return 0;
        }
        var count = 0;
        for (var i = haystack.IndexOf(text, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(text, i + text.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>The text replaced, cut to <paramref name="max"/> characters where the field has a limit.</summary>
    public string? Replace(string? value, int max = int.MaxValue)
    {
        if (value is null)
        {
            return null;
        }
        var replaced = value.Replace(text, Replacement, StringComparison.Ordinal);
        return replaced.Length <= max ? replaced : replaced[..(max - 1)] + "…";
    }

    /// <summary>A JSON body (or any text holding JSON) with every form of the text replaced, and how many there were.</summary>
    public (string? Value, int Count) ReplaceInJson(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return (value, 0);
        }
        var count = 0;
        var replaced = json.Replace(value, _ =>
        {
            count++;
            return Replacement;
        });
        return (replaced, count);
    }

    [GeneratedRegex(@"\\u[0-9A-Fa-f]{4}")]
    private static partial Regex UnicodeEscape();
}
