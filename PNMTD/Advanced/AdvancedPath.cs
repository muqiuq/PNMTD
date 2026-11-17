using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PNMTD.Advanced;

/// <summary>One step of a path: a member, an element, or every member or element (<c>*</c>).</summary>
public abstract record AdvancedSegment
{
    public sealed record Member(string Name) : AdvancedSegment;

    /// <summary>Negative counts from the end: <c>[-1]</c> is the last.</summary>
    public sealed record Index(int Value) : AdvancedSegment;

    /// <summary><c>[*]</c> or <c>.*</c>: every element of a list, or every member value of an object.</summary>
    public sealed record Wildcard : AdvancedSegment;
}

/// <summary>A value a path names in a report: its concrete path (<c>jobs[3].state</c>) and the value.</summary>
public readonly record struct AdvancedMatch(string Path, JsonElement Value);

/// <summary>
/// A path into a report (docs/advanced-sensor-plan.md §6.1), a small subset of JSONPath: <c>a.b</c>, <c>["a key"]</c>,
/// <c>[0]</c>, <c>[-1]</c>, <c>[*]</c> and <c>.*</c>; a leading <c>$</c> is dropped. No filters, no recursive descent, no
/// functions: the quantifiers and the count operators cover the common needs, and a path stays readable in a notification.
/// </summary>
public sealed class AdvancedPath
{
    private AdvancedPath(IReadOnlyList<AdvancedSegment> segments)
    {
        Segments = segments;
        HasWildcard = segments.Any(s => s is AdvancedSegment.Wildcard);
        Text = Format(segments);
    }

    public IReadOnlyList<AdvancedSegment> Segments { get; }

    /// <summary>The path can match several values; its rule's quantifier counts.</summary>
    public bool HasWildcard { get; }

    /// <summary>The normalized text: <c>$.</c> dropped, keys quoted only where needed.</summary>
    public string Text { get; }

    public override string ToString() => Text;

    public static AdvancedPath Parse(string text) =>
        TryParse(text, out var path, out var error) ? path : throw new FormatException(error);

    public static AdvancedPath FromSegments(IReadOnlyList<AdvancedSegment> segments) => new(segments);

    /// <summary>A name that needs no quotes: <c>[A-Za-z_][A-Za-z0-9_-]*</c>.</summary>
    public static bool IsPlainName(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out AdvancedPath? path, [NotNullWhen(false)] out string? error)
    {
        path = null;
        var s = text?.Trim() ?? "";
        if (s.Length == 0)
        {
            error = "A path, such as disk.free_gb or jobs[*].state.";
            return false;
        }
        if (s.Length > AdvancedLimits.MaxPath)
        {
            error = $"At most {AdvancedLimits.MaxPath} characters.";
            return false;
        }
        var segments = new List<AdvancedSegment>();
        var i = 0;
        if (s[0] == '$')
        {
            i = 1;
            if (i < s.Length && s[i] == '.')
            {
                i++;
            }
            else if (i < s.Length && s[i] != '[')
            {
                error = Error(i, "'.' or '[' after '$'");
                return false;
            }
        }
        var first = true;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '[')
            {
                if (!TryBracket(s, ref i, out var segment, out error))
                {
                    return false;
                }
                segments.Add(segment);
            }
            else
            {
                if (!first)
                {
                    if (c != '.')
                    {
                        error = Error(i, "'.' or '['");
                        return false;
                    }
                    i++;
                    if (i >= s.Length)
                    {
                        error = Error(i, "a name after '.'");
                        return false;
                    }
                }
                if (s[i] == '*')
                {
                    segments.Add(new AdvancedSegment.Wildcard());
                    i++;
                }
                else
                {
                    var start = i;
                    while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '_' or '-'))
                    {
                        i++;
                    }
                    var name = s[start..i];
                    if (name.Length == 0 || !IsPlainName(name))
                    {
                        error = Error(start, "a name (letters, digits, _ and -, not starting with a digit or -), or [\"…\"] for any other key");
                        return false;
                    }
                    segments.Add(new AdvancedSegment.Member(name));
                }
            }
            first = false;
            if (segments.Count > AdvancedLimits.MaxSegments)
            {
                error = $"At most {AdvancedLimits.MaxSegments} steps.";
                return false;
            }
        }
        if (segments.Count == 0)
        {
            error = "The path must name a value inside the report, such as status.";
            return false;
        }
        path = new AdvancedPath(segments);
        error = null;
        return true;
    }

    private static string Error(int position, string expected) => $"Position {position + 1}: {expected} expected.";

    private static bool TryBracket(string s, ref int i, [NotNullWhen(true)] out AdvancedSegment? segment, [NotNullWhen(false)] out string? error)
    {
        segment = null;
        var open = i;
        i++;
        if (i >= s.Length)
        {
            error = Error(i, "an index, * or a quoted key after '['");
            return false;
        }
        if (s[i] == '*')
        {
            i++;
            if (i >= s.Length || s[i] != ']')
            {
                error = Error(i, "']'");
                return false;
            }
            i++;
            segment = new AdvancedSegment.Wildcard();
            error = null;
            return true;
        }
        if (s[i] is '"' or '\'')
        {
            var quote = s[i];
            var sb = new StringBuilder();
            i++;
            while (true)
            {
                if (i >= s.Length)
                {
                    error = Error(open, $"a closing {quote}");
                    return false;
                }
                var c = s[i];
                if (c == quote)
                {
                    i++;
                    break;
                }
                if (c == '\\')
                {
                    if (i + 1 >= s.Length)
                    {
                        error = Error(i, "an escape after '\\'");
                        return false;
                    }
                    var e = s[i + 1];
                    switch (e)
                    {
                        case '"' or '\'' or '\\' or '/':
                            sb.Append(e);
                            i += 2;
                            break;
                        case 'b': sb.Append('\b'); i += 2; break;
                        case 'f': sb.Append('\f'); i += 2; break;
                        case 'n': sb.Append('\n'); i += 2; break;
                        case 'r': sb.Append('\r'); i += 2; break;
                        case 't': sb.Append('\t'); i += 2; break;
                        case 'u':
                            if (i + 6 > s.Length || !int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            {
                                error = Error(i, "four hex digits after \\u");
                                return false;
                            }
                            sb.Append((char)code);
                            i += 6;
                            break;
                        default:
                            error = Error(i, "a JSON escape (\\\" \\\\ \\n \\t \\uXXXX)");
                            return false;
                    }
                    continue;
                }
                sb.Append(c);
                i++;
            }
            if (i >= s.Length || s[i] != ']')
            {
                error = Error(i, "']'");
                return false;
            }
            i++;
            segment = new AdvancedSegment.Member(sb.ToString());
            error = null;
            return true;
        }
        var startNumber = i;
        if (s[i] == '-')
        {
            i++;
        }
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            i++;
        }
        if (i == startNumber || (s[startNumber] == '-' && i == startNumber + 1)
            || !int.TryParse(s.AsSpan(startNumber, i - startNumber), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
        {
            error = Error(startNumber, "an index such as 0 or -1, *, or a key in double quotes");
            return false;
        }
        if (i >= s.Length || s[i] != ']')
        {
            error = Error(i, "']'");
            return false;
        }
        i++;
        segment = new AdvancedSegment.Index(index);
        error = null;
        return true;
    }

    // --- formatting ------------------------------------------------------------------------------------------------------

    public static string Format(IReadOnlyList<AdvancedSegment> segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case AdvancedSegment.Member m:
                    AppendMember(sb, m.Name);
                    break;
                case AdvancedSegment.Index x:
                    sb.Append('[').Append(x.Value.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                default:
                    sb.Append("[*]");
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary><c>.name</c>, or <c>name</c> at the start, or <c>["a key"]</c>.</summary>
    public static void AppendMember(StringBuilder sb, string name)
    {
        if (IsPlainName(name))
        {
            if (sb.Length > 0)
            {
                sb.Append('.');
            }
            sb.Append(name);
        }
        else
        {
            sb.Append('[').Append(JsonSerializer.Serialize(name, QuoteOptions)).Append(']');
        }
    }

    /// <summary>Quotes as JSON does, without escaping what JSON allows as it is (é, &lt;, ').</summary>
    private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // --- matching --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The values this path names in <paramref name="root"/>, in document order, each with its concrete path. Stops after
    /// <paramref name="limit"/> + 1, so a caller can tell "too many" from "exactly the limit".
    /// </summary>
    public List<AdvancedMatch> Select(JsonElement root, int limit = AdvancedLimits.MaxMatches)
    {
        var results = new List<AdvancedMatch>();
        Walk(root, 0, new StringBuilder(), results, limit + 1);
        return results;
    }

    /// <summary>The one value a path without <c>*</c> names, or false.</summary>
    public bool TryGet(JsonElement root, out JsonElement value)
    {
        var current = root;
        foreach (var segment in Segments)
        {
            if (!Step(current, segment, out current))
            {
                value = default;
                return false;
            }
        }
        value = current;
        return true;
    }

    private static bool Step(JsonElement current, AdvancedSegment segment, out JsonElement next)
    {
        next = default;
        switch (segment)
        {
            case AdvancedSegment.Member m when current.ValueKind == JsonValueKind.Object:
                return current.TryGetProperty(m.Name, out next);
            case AdvancedSegment.Index x when current.ValueKind == JsonValueKind.Array:
                var length = current.GetArrayLength();
                var at = x.Value < 0 ? length + x.Value : x.Value;
                if (at < 0 || at >= length)
                {
                    return false;
                }
                next = current[at];
                return true;
            default:
                return false;
        }
    }

    private void Walk(JsonElement current, int depth, StringBuilder path, List<AdvancedMatch> results, int stopAt)
    {
        if (results.Count >= stopAt)
        {
            return;
        }
        if (depth == Segments.Count)
        {
            results.Add(new AdvancedMatch(path.ToString(), current));
            return;
        }
        var length = path.Length;
        switch (Segments[depth])
        {
            case AdvancedSegment.Wildcard when current.ValueKind == JsonValueKind.Array:
                var index = 0;
                foreach (var item in current.EnumerateArray())
                {
                    path.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
                    Walk(item, depth + 1, path, results, stopAt);
                    path.Length = length;
                    index++;
                    if (results.Count >= stopAt)
                    {
                        return;
                    }
                }
                break;
            case AdvancedSegment.Wildcard when current.ValueKind == JsonValueKind.Object:
                foreach (var member in current.EnumerateObject())
                {
                    AppendMember(path, member.Name);
                    Walk(member.Value, depth + 1, path, results, stopAt);
                    path.Length = length;
                    if (results.Count >= stopAt)
                    {
                        return;
                    }
                }
                break;
            case AdvancedSegment.Member m when current.ValueKind == JsonValueKind.Object && current.TryGetProperty(m.Name, out var value):
                AppendMember(path, m.Name);
                Walk(value, depth + 1, path, results, stopAt);
                path.Length = length;
                break;
            case AdvancedSegment.Index x when current.ValueKind == JsonValueKind.Array:
                var count = current.GetArrayLength();
                var at = x.Value < 0 ? count + x.Value : x.Value;
                if (at >= 0 && at < count)
                {
                    path.Append('[').Append(at.ToString(CultureInfo.InvariantCulture)).Append(']');
                    Walk(current[at], depth + 1, path, results, stopAt);
                    path.Length = length;
                }
                break;
        }
    }

    /// <summary>
    /// Every value in a report, for the editor's suggestions (§8.2): a path, the JSON kind and a short preview. Lists show
    /// their first element and a <c>[*]</c> path; at most <paramref name="max"/> entries.
    /// </summary>
    public static List<(string Path, JsonValueKind Kind, string? Preview)> Enumerate(JsonElement root, int max = 2000)
    {
        var list = new List<(string, JsonValueKind, string?)>();
        void Visit(JsonElement e, string path, int depth)
        {
            if (list.Count >= max || depth > AdvancedLimits.MaxSegments)
            {
                return;
            }
            if (path.Length > 0)
            {
                list.Add((path, e.ValueKind, e.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? null : AdvancedValues.Display(e, 80)));
            }
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var member in e.EnumerateObject())
                    {
                        var sb = new StringBuilder(path);
                        AppendMember(sb, member.Name);
                        Visit(member.Value, sb.ToString(), depth + 1);
                    }
                    break;
                case JsonValueKind.Array:
                    var i = 0;
                    foreach (var item in e.EnumerateArray())
                    {
                        // The first three elements by index, and the shape of all of them once through [*].
                        if (i < 3)
                        {
                            Visit(item, $"{path}[{i}]", depth + 1);
                        }
                        i++;
                    }
                    if (i > 0 && e[0].ValueKind is JsonValueKind.Object)
                    {
                        foreach (var member in e[0].EnumerateObject())
                        {
                            var sb = new StringBuilder(path + "[*]");
                            AppendMember(sb, member.Name);
                            if (list.Count < max)
                            {
                                list.Add((sb.ToString(), member.Value.ValueKind, null));
                            }
                        }
                    }
                    break;
            }
        }
        Visit(root, "", 0);
        return list;
    }
}
