using System.Text;

namespace PNMTD.Knowledge;

/// <summary>
/// The knowledge base's content search (docs/locations-knowledge-plan.md §6.7): PostgreSQL's full-text search with the
/// <c>simple</c> configuration over the pages' generated <c>SearchVector</c>, ranked by <c>ts_rank_cd</c>, with
/// <c>ts_headline</c> snippets for the page of results only. <see cref="Parse"/> builds the <c>tsquery</c> text in code,
/// never from the raw input: every word matches as a prefix (<i>drucker</i> finds <i>Druckerwarteschlange</i>),
/// <c>"…"</c> is a phrase, <c>-word</c> excludes, <c>or</c> between two terms gives alternatives, and <c>#tag</c> filters
/// by a tag. What reaches PostgreSQL is only lexemes of letters and digits, quoted, and the operators put between them.
/// </summary>
public static class KbSearch
{
    /// <summary>Where a snippet's match starts and ends (Unicode private use): the UI makes them <c>&lt;mark&gt;</c>.</summary>
    public const char MarkStart = '\uE000';

    public const char MarkEnd = '\uE001';

    /// <summary>The configuration of the vector and the queries: no stemming, so German and English behave the same.</summary>
    public const string Configuration = "simple";

    /// <summary>ts_headline's options: two short fragments around the matches.</summary>
    public static readonly string HeadlineOptions =
        $"StartSel={MarkStart}, StopSel={MarkEnd}, MaxWords=30, MinWords=12, ShortWord=2, MaxFragments=2, FragmentDelimiter=\" … \"";

    /// <summary>More terms than this are ignored: a search is a few words.</summary>
    public const int MaxTerms = 24;

    /// <summary>The plain text a snippet is cut from: as much as the vector holds (§8.2).</summary>
    public const int HeadlineChars = 200_000;

    /// <summary>A parsed search: the <c>tsquery</c> text (null when only tags or nothing is left) and the tags it names.</summary>
    public sealed record Parsed(string? Query, IReadOnlyList<string> Tags)
    {
        public bool IsEmpty => Query is null && Tags.Count == 0;
    }

    private sealed record Term(string Text, bool Negated);

    public static Parsed Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new Parsed(null, []);
        }
        var tags = new List<string>();
        // Groups joined by AND; each group's terms are alternatives (or).
        var groups = new List<List<Term>>();
        var joinNext = false;
        var terms = 0;
        foreach (var (token, quoted, minus) in Tokens(input))
        {
            if (terms >= MaxTerms)
            {
                break;
            }
            if (!quoted && token.Equals("or", StringComparison.OrdinalIgnoreCase))
            {
                joinNext = groups.Count > 0;
                continue;
            }
            var negated = minus;
            var text = token;
            if (!quoted && text.StartsWith('#') && KbTags.Normalize(text) is { } tag)
            {
                if (!tags.Contains(tag))
                {
                    tags.Add(tag);
                }
                joinNext = false;
                continue;
            }
            if (!quoted && text.StartsWith('-'))
            {
                negated = true;
                text = text[1..];
            }
            var built = quoted ? Phrase(text) : Word(text);
            if (built is null)
            {
                joinNext = false;
                continue;
            }
            terms++;
            var term = new Term(built, negated);
            if (joinNext && !negated && groups[^1].All(t => !t.Negated))
            {
                groups[^1].Add(term);
            }
            else
            {
                groups.Add([term]);
            }
            joinNext = false;
        }
        if (groups.Count == 0)
        {
            return new Parsed(null, tags);
        }
        var query = string.Join(" & ", groups.Select(g => g.Count == 1
            ? Render(g[0])
            : "( " + string.Join(" | ", g.Select(Render)) + " )"));
        return new Parsed(query, tags);
    }

    private static string Render(Term term) => term.Negated ? $"!( {term.Text} )" : term.Text;

    /// <summary>
    /// A word: its runs of letters and digits, as PostgreSQL's parser splits <i>fw-zrh-01</i> too, each a prefix, in
    /// sequence. Null when nothing is left.
    /// </summary>
    private static string? Word(string word)
    {
        var parts = Lexemes(word);
        return parts.Count switch
        {
            0 => null,
            1 => Prefix(parts[0]),
            _ => "( " + string.Join(" <-> ", parts.Select(Prefix)) + " )",
        };
    }

    /// <summary>A phrase: its words in sequence, exactly, the last one as a prefix.</summary>
    private static string? Phrase(string phrase)
    {
        var parts = Lexemes(phrase);
        if (parts.Count == 0)
        {
            return null;
        }
        var rendered = parts.Select((p, i) => i == parts.Count - 1 ? Prefix(p) : Quote(p)).ToList();
        return rendered.Count == 1 ? rendered[0] : "( " + string.Join(" <-> ", rendered) + " )";
    }

    private static string Prefix(string lexeme) => Quote(lexeme) + ":*";

    /// <summary>A lexeme of letters and digits only, so the quotes need no escaping.</summary>
    private static string Quote(string lexeme) => $"'{lexeme}'";

    /// <summary>The runs of letters and digits, in lower case, at most 64 characters each.</summary>
    internal static List<string> Lexemes(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }
            Flush();
        }
        Flush();
        return parts;

        void Flush()
        {
            if (current.Length > 0)
            {
                parts.Add(current.Length > 64 ? current.ToString(0, 64) : current.ToString());
                current.Clear();
            }
        }
    }

    /// <summary>The input's tokens: words, and quoted phrases, which a <c>-</c> before the quote excludes.</summary>
    private static IEnumerable<(string Token, bool Quoted, bool Negated)> Tokens(string input)
    {
        var i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && char.IsWhiteSpace(input[i]))
            {
                i++;
            }
            if (i >= input.Length)
            {
                yield break;
            }
            var negated = input[i] == '-' && i + 1 < input.Length && input[i + 1] == '"';
            var start = negated ? i + 1 : i;
            if (input[start] == '"')
            {
                var end = input.IndexOf('"', start + 1);
                var phrase = end < 0 ? input[(start + 1)..] : input[(start + 1)..end];
                i = end < 0 ? input.Length : end + 1;
                yield return (phrase, true, negated);
                continue;
            }
            var stop = i;
            while (stop < input.Length && !char.IsWhiteSpace(input[stop]))
            {
                stop++;
            }
            yield return (input[i..stop], false, false);
            i = stop;
        }
    }
}
