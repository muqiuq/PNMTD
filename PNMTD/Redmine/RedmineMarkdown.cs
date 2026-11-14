using System.Text;
using System.Text.RegularExpressions;

namespace PNMTD.Redmine;

/// <summary>
/// A ticket's Markdown for PNMT's <c>MarkdownView</c> (docs/audit-redmine-zabbix-plan.md §6.8): Redmine's own references
/// become links, outside code: <c>#1234</c> to PNMT's ticket page; <c>attachment:"x.png"</c> and <c>![](x.png)</c> to the
/// attachment in Redmine, which needs the person's own sign-in there. Nothing else changes; MarkdownView renders no HTML.
/// </summary>
public static partial class RedmineMarkdown
{
    public const string TicketPath = "/pnmt-admin/redmine/issues/";

    [GeneratedRegex(@"(?<![\w&/#\[])#(\d{1,9})\b")]
    private static partial Regex Ticket();

    [GeneratedRegex(@"attachment:(?:""(?<q>[^""]+)""|(?<n>[^\s\])]+))")]
    private static partial Regex Attachment();

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<src>[^)\s]+)\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"(`+)(.+?)\1")]
    private static partial Regex CodeSpan();

    public static string Prepare(string text, string redmineUrl, IReadOnlyList<(string Filename, int Id)> attachments)
    {
        var output = new StringBuilder(text.Length + 64);
        var fence = (string?)null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (fence is null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = trimmed[..3];
                output.Append(line).Append('\n');
                continue;
            }
            if (fence is not null)
            {
                if (trimmed.StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                }
                output.Append(line).Append('\n');
                continue;
            }
            output.Append(OutsideCode(line, redmineUrl, attachments)).Append('\n');
        }
        return output.ToString().TrimEnd('\n');
    }

    /// <summary>One line, its code spans left as they are.</summary>
    private static string OutsideCode(string line, string redmineUrl, IReadOnlyList<(string Filename, int Id)> attachments)
    {
        var result = new StringBuilder();
        var at = 0;
        foreach (Match span in CodeSpan().Matches(line))
        {
            result.Append(Convert(line[at..span.Index], redmineUrl, attachments)).Append(span.Value);
            at = span.Index + span.Length;
        }
        return result.Append(Convert(line[at..], redmineUrl, attachments)).ToString();
    }

    private static string Convert(string text, string redmineUrl, IReadOnlyList<(string Filename, int Id)> attachments)
    {
        string? Url(string name) => attachments.FirstOrDefault(a => a.Filename.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Id: > 0 } a
            ? $"{redmineUrl}/attachments/{a.Id}" : null;
        text = Image().Replace(text, m =>
        {
            var src = m.Groups["src"].Value;
            if (src.Contains("://", StringComparison.Ordinal) || src.StartsWith('/'))
            {
                return m.Value;
            }
            var alt = m.Groups["alt"].Value is { Length: > 0 } a ? a : src;
            return Url(src) is { } url ? $"[{alt}]({url})" : $"[{alt}]";
        });
        text = Attachment().Replace(text, m =>
        {
            var name = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["n"].Value;
            return Url(name) is { } url ? $"[{name}]({url})" : name;
        });
        return Ticket().Replace(text, m => $"[#{m.Groups[1].Value}]({TicketPath}{m.Groups[1].Value})");
    }
}
