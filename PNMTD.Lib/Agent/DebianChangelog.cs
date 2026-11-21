using System.Text;
using System.Text.RegularExpressions;

namespace PNMTD.Lib.Agent;

/// <summary>
/// A Debian changelog's entries (deb-changelog(5), docs/linux-os-updates-plan.md §6.2): "source (version) distributions; urgency=…", the
/// change lines, the trailer. Shared by the agent (which sends the part newer than its installed version) and PNMTD (which reads that part
/// again against each server's own installed version). Only code reads the text: the CVE and urgency patterns.
/// </summary>
public static partial class DebianChangelog
{
    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,}\b")]
    public static partial Regex Cve();

    // openssl (3.0.22-1~deb12u2) bookworm-security; urgency=high
    [GeneratedRegex(@"^(?<source>[a-z0-9][a-z0-9+.-]+) \((?<version>[^)\s]+)\) [^;]*;(?:.*\burgency=(?<urgency>[A-Za-z]+))?")]
    private static partial Regex Header();

    /// <summary>
    /// The entries newer than <paramref name="installed"/> (all, when it is null), with the CVEs they name and their urgencies; the text at
    /// most <paramref name="maxChars"/>, cut once where a line no longer fits (the CVEs of every newer entry still count).
    /// </summary>
    public static (string Text, List<string> Cves, List<string> Urgencies) Since(string changelog, string? installed, int maxChars = PackageChangelog.MaxTextChars)
    {
        var text = new StringBuilder();
        var cves = new List<string>();
        var urgencies = new List<string>();
        var started = false;
        var full = false;
        foreach (var raw in changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (Header().Match(line) is { Success: true } header)
            {
                var version = header.Groups["version"].Value;
                if (installed is not null && DebianVersion.Compare(version, installed) <= 0)
                {
                    break;
                }
                started = true;
                if (header.Groups["urgency"].Success)
                {
                    urgencies.Add(header.Groups["urgency"].Value.ToLowerInvariant());
                }
            }
            if (!started)
            {
                continue;
            }
            foreach (Match cve in Cve().Matches(line))
            {
                cves.Add(cve.Value);
            }
            if (!full && text.Length + line.Length + 1 <= maxChars)
            {
                text.Append(line).Append('\n');
            }
            else
            {
                full = true;
            }
        }
        return (text.ToString().TrimEnd(), cves.Distinct(StringComparer.Ordinal).ToList(), urgencies.Distinct().ToList());
    }
}
