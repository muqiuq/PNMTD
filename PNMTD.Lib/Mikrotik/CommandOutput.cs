using System.Text.RegularExpressions;

namespace PNMTD.Lib.Mikrotik;

/// <summary>
/// A remote command's output (docs/mikrotik-plan.md §8.2, §8.3): untrusted text from the router, cleaned of
/// terminal escape sequences, checked for RouterOS's error line, and grouped across a batch. Recorded in M0
/// and M6: an error stops the script with exit status 1, and its line comes last, such as "not enough
/// permissions (9)", or on 7.25.1 "bad command name nosuch (line 1 column 2)", "expected end of command
/// (line 2 column 19)" and "input does not match any value of interface (/ip/address/add (interface); line 1)".
/// </summary>
public static partial class CommandOutput
{
    [GeneratedRegex(@"\x1B(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.CultureInvariant)]
    private static partial Regex Escapes();

    [GeneratedRegex(@"^(bad command name|expected end of command|expected command name|syntax error|not enough permissions|failure:|no such item|invalid value|input does not match|missing value|ambiguous value|script error)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ErrorStart();

    /// <summary>Where 7.25 says where the error is: "(line 1 column 2)", "(/ip/address/add (interface); line 1)".</summary>
    [GeneratedRegex(@"\((?:[^;]*; )?line \d+(?: column \d+)?\)$", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorEnd();

    /// <summary>LF line ends, no escape sequences or other control characters but tab and newline.</summary>
    public static string Clean(string output)
    {
        var text = Escapes().Replace(output.Replace("\r\n", "\n").Replace('\r', '\n'), "");
        return new string(text.Where(c => c is '\n' or '\t' || !char.IsControl(c)).ToArray());
    }

    /// <summary>RouterOS's error, if the output ends with one of its error lines: an error stops the script.</summary>
    public static string? Error(string cleaned) =>
        LastLine(cleaned) is { } last && (ErrorStart().IsMatch(last) || ErrorEnd().IsMatch(last)) ? last : null;

    /// <summary>The last line with text, for the message of a non-zero exit status.</summary>
    public static string? LastLine(string cleaned) =>
        cleaned.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    /// <summary>Cut at <paramref name="maxBytes"/> of UTF-8, at a character boundary.</summary>
    public static (string Text, bool Cut) Limit(string text, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return (text, false);
        }
        var length = Math.Min(text.Length, maxBytes);
        while (length > 0 && System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > maxBytes)
        {
            length--;
        }
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }
        return (text[..length], true);
    }

    /// <summary>
    /// A batch's results grouped by <paramref name="key"/>, the outcome and the output (§8.3): fifty routers that
    /// say the same are one group, the largest first, so the three that differ stand out.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<T>> Group<T>(IEnumerable<T> items, Func<T, string> key) =>
        items.GroupBy(key).OrderByDescending(g => g.Count()).Select(g => (IReadOnlyList<T>)g.ToList()).ToList();
}
