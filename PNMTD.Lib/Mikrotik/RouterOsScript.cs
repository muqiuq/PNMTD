using System.Globalization;
using System.Text;

namespace PNMTD.Lib.Mikrotik;

/// <summary>
/// RouterOS script text (docs/mikrotik-plan.md §4.3, §4.5, §8.2): string literals by RouterOS's rules,
/// and the wrapper that runs a text as one script over SSH exec.
/// </summary>
public static class RouterOsScript
{
    /// <summary>
    /// A string literal that RouterOS reads back as exactly <paramref name="value"/>: <c>"</c>, <c>\</c>,
    /// <c>$</c> and <c>?</c> escaped with a backslash; tab, CR and LF as <c>\t</c>, <c>\r</c>, <c>\n</c>;
    /// every other byte outside printable ASCII (control characters, and UTF-8's bytes) as <c>\XX</c>.
    /// The result is printable ASCII on one line, whatever the input.
    /// </summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            switch (b)
            {
                case (byte)'"' or (byte)'\\' or (byte)'$' or (byte)'?':
                    sb.Append('\\').Append((char)b);
                    break;
                case (byte)'\n':
                    sb.Append("\\n");
                    break;
                case (byte)'\r':
                    sb.Append("\\r");
                    break;
                case (byte)'\t':
                    sb.Append("\\t");
                    break;
                case >= 0x20 and < 0x7F:
                    sb.Append((char)b);
                    break;
                default:
                    sb.Append('\\').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// A text to run as one script over a non-interactive exec (§8.2). Sent as it is, RouterOS runs each
    /// line on its own: a block cannot span lines, a <c>:local</c> ends with its line, and an error does not
    /// stop the lines after it. Wrapped in <c>:parse</c>, it is parsed as a whole first (a syntax error runs
    /// nothing), runs as one script, and stops at the first error with a non-zero exit status. M0 confirmed
    /// both on 7.13.
    /// </summary>
    public static string AsOneScript(string text) =>
        $":local pnmtScript [:parse {Quote(text.ReplaceLineEndings("\n"))}]; $pnmtScript";
}
