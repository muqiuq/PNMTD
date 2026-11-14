using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PNMTD.Knowledge;

/// <summary>
/// What the server reads from a page's or a field's Markdown (§6.7): its plain text, the files it references, and the
/// pages it links to. <see cref="PageNumbers"/> are KB numbers, <c>/pnmt-admin/kb/42</c> giving 42. Both lists are
/// distinct, in the order of first use.
/// </summary>
public sealed record MarkdownFacts(string PlainText, IReadOnlyList<Guid> FileIds, IReadOnlyList<int> PageNumbers);

/// <summary>
/// The server's reading of Markdown with Markdig (docs/locations-knowledge-plan.md §5.1, §6.7, §8.3), GitHub Flavored as
/// the editors write it: tables, task lists, strikethrough, autolinks, and raw HTML as text. It only reads: the
/// stored Markdown is never rewritten, and never rendered to HTML on the server.
/// <list type="bullet">
/// <item><b>The plain text</b> keeps what a reader sees: text, link texts (so attachments' names), bare URLs, alt texts and
///   code, mermaid blocks included. It leaves out image and link targets and titles. One line per block, a tab between
///   table cells.</item>
/// <item><b>Files</b> are references through <c>/api/v1/files/{id}/…</c>, as images and as links (§5.1), footnotes
///   included; then, since a file no save names is deleted, every <c>/api/v1/files/{id}</c> anywhere in the raw text (in
///   code, a reference definition, an absolute URL). A file named there needlessly only stays longer.</item>
/// <item><b>Pages</b> are links to <c>/pnmt-admin/kb/{number}</c>, with or without a sub-path (<c>/history</c>).</item>
/// </list>
/// </summary>
public static partial class MarkdownText
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseFootnotes()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public static MarkdownFacts Read(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return new MarkdownFacts("", [], []);
        }
        var reader = new Reader();
        reader.Blocks(Markdown.Parse(markdown, Pipeline));
        var fileIds = reader.FileIds;
        foreach (Match m in AnyFileUrl().Matches(markdown))
        {
            if (Guid.TryParse(m.Groups[1].Value, out var id) && !fileIds.Contains(id))
            {
                fileIds.Add(id);
            }
        }
        return new MarkdownFacts(reader.Text(), fileIds, reader.PageNumbers);
    }

    [GeneratedRegex(@"<br\s*/?\s*>", RegexOptions.IgnoreCase, 250)]
    private static partial Regex BareBreak();

    [GeneratedRegex(@"/api/v1/files/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?![0-9a-fA-F])", RegexOptions.None, 1000)]
    private static partial Regex AnyFileUrl();

    /// <summary>The file a URL points to: <c>/api/v1/files/{id}</c>, then the end, a <c>/</c>, <c>?</c> or <c>#</c>.</summary>
    public static Guid? FileId(string? url) =>
        url is not null && FileUrl().Match(url) is { Success: true } m && Guid.TryParse(m.Groups[1].Value, out var id) ? id : null;

    /// <summary>The page a URL points to: <c>/pnmt-admin/kb/{number}</c>, then the end, a <c>/</c>, <c>?</c> or <c>#</c>.</summary>
    public static int? PageNumber(string? url) =>
        url is not null && PageUrl().Match(url) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? n : null;

    [GeneratedRegex(@"^/api/v1/files/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?:[/?#]|$)", RegexOptions.None, 250)]
    private static partial Regex FileUrl();

    [GeneratedRegex(@"^/pnmt-admin/kb/([0-9]{1,9})(?:[/?#]|$)", RegexOptions.None, 250)]
    private static partial Regex PageUrl();

    private sealed class Reader
    {
        private readonly StringBuilder text = new();
        private readonly HashSet<Guid> seenFiles = [];
        private readonly HashSet<int> seenPages = [];

        public List<Guid> FileIds { get; } = [];

        public List<int> PageNumbers { get; } = [];

        /// <summary>The text, with each line's trailing spaces and runs of empty lines gone.</summary>
        public string Text()
        {
            // The visual editor writes each empty line as a bare <br />, which DisableHtml leaves as text: a line break,
            // as the reader sees it (docs/locations-knowledge-plan.md §5.6), not the word "br" in the search.
            var lines = BareBreak().Replace(text.ToString(), "\n").Split('\n').Select(l => l.TrimEnd());
            var result = new StringBuilder();
            var blank = true;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    if (!blank)
                    {
                        result.Append('\n');
                    }
                    blank = true;
                    continue;
                }
                result.Append(line).Append('\n');
                blank = false;
            }
            return result.ToString().TrimEnd('\n');
        }

        public void Blocks(ContainerBlock container)
        {
            foreach (var block in container)
            {
                switch (block)
                {
                    case Table table:
                        foreach (var row in table.OfType<TableRow>())
                        {
                            var first = true;
                            foreach (var cell in row.OfType<TableCell>())
                            {
                                if (!first)
                                {
                                    text.Append('\t');
                                }
                                first = false;
                                foreach (var leaf in cell.OfType<LeafBlock>())
                                {
                                    Inlines(leaf.Inline);
                                }
                            }
                            NewLine();
                        }
                        break;
                    case ContainerBlock inner:
                        Blocks(inner);
                        break;
                    case LinkReferenceDefinition:
                        // A target only; the links that use it are read where they are.
                        break;
                    case CodeBlock code:
                        // Fenced and indented code, mermaid's source among it (§5.8).
                        foreach (var line in code.Lines.Lines.Take(code.Lines.Count))
                        {
                            text.Append(line.Slice.ToString()).Append('\n');
                        }
                        break;
                    case LeafBlock leaf:
                        Inlines(leaf.Inline);
                        NewLine();
                        break;
                }
            }
        }

        private void NewLine()
        {
            if (text.Length > 0 && text[^1] != '\n')
            {
                text.Append('\n');
            }
        }

        private void Inlines(ContainerInline? container)
        {
            if (container is null)
            {
                return;
            }
            var afterTask = false;
            foreach (var inline in container)
            {
                var task = afterTask;
                afterTask = false;
                switch (inline)
                {
                    case LinkInline link:
                        Reference(link.Url);
                        // The link's text, or an image's alt text.
                        Inlines(link);
                        break;
                    case AutolinkInline auto:
                        Reference(auto.Url);
                        text.Append(auto.Url);
                        break;
                    case CodeInline code:
                        text.Append(code.Content);
                        break;
                    case LiteralInline literal:
                        // "- [x] Driver installed": the text without the space after the box.
                        text.Append(task ? literal.Content.ToString().TrimStart() : literal.Content.ToString());
                        break;
                    case HtmlEntityInline entity:
                        text.Append(entity.Transcoded.ToString());
                        break;
                    case LineBreakInline:
                        text.Append('\n');
                        break;
                    case TaskList:
                        afterTask = true;
                        break;
                    case HtmlInline html:
                        // DisableHtml makes raw HTML text; should a tag come through, it is still only text (§5.1).
                        text.Append(html.Tag);
                        break;
                    case DelimiterInline delimiter:
                        text.Append(delimiter.ToLiteral());
                        Inlines(delimiter);
                        break;
                    case ContainerInline inner:
                        Inlines(inner);
                        break;
                }
            }
        }

        private void Reference(string? url)
        {
            if (FileId(url) is { } id && seenFiles.Add(id))
            {
                FileIds.Add(id);
            }
            else if (PageNumber(url) is { } number && seenPages.Add(number))
            {
                PageNumbers.Add(number);
            }
        }
    }
}
