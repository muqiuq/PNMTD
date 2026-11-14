using PNMTD.Knowledge;

namespace PNMTD.Tests.Knowledge;

/// <summary>The content search's query, built in code (docs/locations-knowledge-plan.md §6.7, §12).</summary>
[TestClass]
public sealed class KbSearchTests
{
    [TestMethod]
    public void Every_word_is_a_prefix_and_all_must_match()
    {
        Assert.AreEqual("'drucker':* & 'zürich':*", KbSearch.Parse("Drucker  Zürich").Query);
    }

    [TestMethod]
    public void A_word_with_dashes_is_its_parts_in_sequence_as_the_parser_splits_it()
    {
        Assert.AreEqual("( 'fw':* <-> 'zrh':* <-> '01':* )", KbSearch.Parse("fw-zrh-01").Query);
    }

    [TestMethod]
    public void A_phrase_is_exact_but_for_its_last_word()
    {
        Assert.AreEqual("( 'power' <-> 'outage':* ) & 'ups':*", KbSearch.Parse("\"power outage\" ups").Query);
    }

    [TestMethod]
    public void A_minus_excludes_a_word_or_a_phrase()
    {
        Assert.AreEqual("'printer':* & !( 'ricoh':* ) & !( ( 'second' <-> 'floor':* ) )", KbSearch.Parse("printer -ricoh -\"second floor\"").Query);
    }

    [TestMethod]
    public void Or_joins_its_neighbours_and_binds_tighter_than_the_implicit_and()
    {
        Assert.AreEqual("( 'ups':* | 'battery':* ) & 'zrh':*", KbSearch.Parse("ups or battery zrh").Query);
        Assert.AreEqual("( 'a1':* | 'b2':* | 'c3':* )", KbSearch.Parse("a1 OR b2 or c3").Query);
        // A dangling or, or one before an exclusion, is just a word gone.
        Assert.AreEqual("'ups':*", KbSearch.Parse("or ups or").Query);
        Assert.AreEqual("'ups':* & !( 'apc':* )", KbSearch.Parse("ups or -apc").Query);
    }

    [TestMethod]
    public void A_hash_names_a_tag_and_is_not_searched_as_text()
    {
        var parsed = KbSearch.Parse("#Backup nas #backup #zrh-dc");
        Assert.AreEqual("'nas':*", parsed.Query);
        CollectionAssert.AreEqual(new[] { "backup", "zrh-dc" }, parsed.Tags.ToArray());
        Assert.IsNull(KbSearch.Parse("#backup").Query);
    }

    [TestMethod]
    public void Input_cannot_inject_operators_or_quotes()
    {
        foreach (var attack in new[] { "a' | 'b", "x:* & !y", "') ; DROP TABLE kb_pages; --", "<->", "a & b | c", "\\' ''", "foo:A", "(!)" })
        {
            var query = KbSearch.Parse(attack).Query;
            if (query is null)
            {
                continue;
            }
            // Only quoted runs of letters and digits, and the operators the builder writes.
            var stripped = System.Text.RegularExpressions.Regex.Replace(query, @"'[\p{L}\p{N}]+'(:\*)?", "L");
            StringAssert.Matches(stripped, new System.Text.RegularExpressions.Regex(@"^[L &|!()<\->]*$"), $"{attack} → {query}");
        }
        Assert.AreEqual("'drop':* & 'table':* & ( 'kb':* <-> 'pages':* )", KbSearch.Parse("DROP; TABLE kb_pages").Query);
    }

    [TestMethod]
    public void Nothing_to_search_for_gives_no_query()
    {
        Assert.IsTrue(KbSearch.Parse("  ").IsEmpty);
        Assert.IsTrue(KbSearch.Parse("-- ! ?").IsEmpty);
        Assert.IsTrue(KbSearch.Parse("\"\"").IsEmpty);
    }

    [TestMethod]
    public void Long_input_is_cut_to_a_few_terms()
    {
        var query = KbSearch.Parse(string.Join(" ", Enumerable.Range(0, 100).Select(i => $"w{i}"))).Query!;
        Assert.AreEqual(KbSearch.MaxTerms, query.Split(" & ").Length);
    }
}
