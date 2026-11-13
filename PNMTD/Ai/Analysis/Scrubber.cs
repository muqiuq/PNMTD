using System.Net;
using System.Text.RegularExpressions;

namespace PNMTD.Ai.Analysis;

/// <summary>
/// The situation analysis's scrubber (docs/ai-operations-plan.md §9.1): in every message, the names PNMT knows become
/// their pseudonyms (<c>H1</c>, <c>S2</c>), and addresses, mail addresses, URLs, host names, GUIDs and long serial-like
/// tokens become placeholders (<c>&lt;ip:1&gt;</c>, <c>&lt;host:2&gt;</c>). The same value gets the same placeholder
/// within a run, so patterns stay visible; numbers and codes stay. <see cref="Values"/> maps the placeholders back.
/// </summary>
public sealed partial class Scrubber
{
    private readonly Dictionary<string, Dictionary<string, string>> byKind = [];
    private readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
    private Regex? known;

    /// <summary>Placeholder → the value it replaced.</summary>
    public Dictionary<string, string> Values { get; } = [];

    /// <summary>A name PNMT knows (a host's or a sensor's): replaced by its pseudonym wherever it occurs.</summary>
    public void Name(string name, string pseudonym)
    {
        name = name.Trim();
        if (name.Length >= 3)
        {
            names.TryAdd(name, pseudonym);
            known = null;
        }
    }

    public string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        if (names.Count > 0)
        {
            // One alternation, longest first, built once per run.
            known ??= new Regex($@"(?<![\w.-])(?:{string.Join("|", names.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape))})(?![\w-])",
                RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
            text = known.Replace(text, m => names[m.Value]);
        }
        text = Url().Replace(text, m => Placeholder("url", m.Value));
        text = Mail().Replace(text, m => Placeholder("mail", m.Value));
        text = Guid().Replace(text, m => Placeholder("id", m.Value));
        text = Mac().Replace(text, m => Placeholder("mac", m.Value));
        text = IPv6().Replace(text, m => IPAddress.TryParse(m.Value, out _) ? Placeholder("ip", m.Value) : m.Value);
        text = IPv4().Replace(text, m => IPAddress.TryParse(m.Value, out _) ? Placeholder("ip", m.Value) : m.Value);
        text = HostName().Replace(text, m => Placeholder("host", m.Value));
        text = Token().Replace(text, m => Placeholder("token", m.Value));
        return text;
    }

    private string Placeholder(string kind, string value)
    {
        if (!byKind.TryGetValue(kind, out var map))
        {
            byKind[kind] = map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        if (!map.TryGetValue(value, out var placeholder))
        {
            placeholder = $"<{kind}:{map.Count + 1}>";
            map[value] = placeholder;
            Values[placeholder] = value;
        }
        return placeholder;
    }

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]{1,15}://[^\s<>""']+", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Url();

    [GeneratedRegex(@"\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Mail();

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Guid();

    [GeneratedRegex(@"\b[0-9a-f]{2}([:-])(?:[0-9a-f]{2}\1){4}[0-9a-f]{2}\b", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Mac();

    /// <summary>Candidates with "::" or eight groups; checked by parsing, so times like 12:30:45 stay.</summary>
    [GeneratedRegex(@"(?<![\w:])(?:(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}|(?:[0-9a-f]{1,4}:)*:(?::[0-9a-f]{1,4})*(?::?[0-9a-f]{1,4})?)(?:%\w+)?(?![\w:])",
        RegexOptions.IgnoreCase, 250)]
    private static partial Regex IPv6();

    [GeneratedRegex(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?:/\d{1,2})?(?![\w.])", RegexOptions.None, 250)]
    private static partial Regex IPv4();

    /// <summary>Names with at least one dot and a top-level part of letters (nas.example.com, srv01.local).</summary>
    [GeneratedRegex(@"(?<![\w.<-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}(?![\w>-])", RegexOptions.IgnoreCase, 250)]
    private static partial Regex HostName();

    /// <summary>16 or more letters and digits mixed: serial numbers, keys, session ids.</summary>
    [GeneratedRegex(@"(?<![\w<])(?=[a-z0-9_-]*\d)(?=[a-z0-9_-]*[a-z])[a-z0-9_-]{16,}(?![\w>])", RegexOptions.IgnoreCase, 250)]
    private static partial Regex Token();
}
