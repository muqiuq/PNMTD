using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PNMTD.Lib.Mikrotik;

/// <summary>
/// An <c>/export terse</c> as PNMT stores and compares it (docs/mikrotik-plan.md §6.1, §6.2). Recorded in M0
/// on 7.13 and 7.25.1: two exports a minute apart differ only in their first line,
/// <c># 2026-11-12 23:14:09 by RouterOS 7.13</c>. A user without <c>sniff</c> on 7.13 gets
/// <c>#error exporting "/ip/firewall/calea"</c> lines, which are stable and stay. Without the policy
/// <c>sensitive</c>, <c>show-sensitive</c> gives <c>*****</c> instead of secrets.
/// </summary>
public static partial class ExportText
{
    /// <summary>The first line, with the time of the export: "# 2026-11-12 23:14:09 by RouterOS 7.13", or older "# nov/12/2026 …".</summary>
    [GeneratedRegex(@"^# .* by RouterOS (?<version>\S+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Header();

    /// <summary>LF line ends, without the time line and trailing blank lines, ending with one LF.</summary>
    public static string Normalise(string export)
    {
        var lines = export.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && Header().IsMatch(lines[0]))
        {
            lines.RemoveAt(0);
        }
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        return lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
    }

    /// <summary>The RouterOS version the export names in its first line, if it has one.</summary>
    public static string? Version(string export)
    {
        var first = export.Replace("\r\n", "\n").Split('\n', 2)[0];
        return Header().Match(first) is { Success: true } m ? m.Groups["version"].Value : null;
    }

    /// <summary>SHA-256 of the normalised text, lower-case hex.</summary>
    public static string Hash(string normalised) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));

    /// <summary>
    /// Why an exec's output is not an export: RouterOS's error ("not enough permissions (9)", "bad command
    /// name …"), or nothing at all. An export starts with a comment line; null when it looks like one.
    /// </summary>
    public static string? Problem(string output)
    {
        var text = output.Replace("\r\n", "\n").TrimStart('\n', ' ');
        if (text.Length == 0)
        {
            return "The router answered with an empty export.";
        }
        if (text.StartsWith('#') || text.StartsWith('/'))
        {
            return null;
        }
        var first = text.Split('\n', 2)[0].Trim();
        return $"The router answered: {(first.Length > 200 ? first[..200] + "…" : first)}";
    }

    public static int LineCount(string normalised) => normalised.Length == 0 ? 0 : normalised.Count(c => c == '\n');
}
