using PNMTD.Knowledge;

namespace PNMTD.Tests.Knowledge;

/// <summary>The server's reading of Markdown (docs/locations-knowledge-plan.md §6.7, §12): plain text, file ids and page links.</summary>
[TestClass]
public sealed class MarkdownTextTests
{
    private static readonly Guid Image = Guid.Parse("0193f3c2-7a1b-7c3d-8e4f-5a6b7c8d9e0f");
    private static readonly Guid Manual = Guid.Parse("0194a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    [TestMethod]
    public void The_plain_text_keeps_what_a_reader_sees_and_leaves_out_the_targets()
    {
        var facts = MarkdownText.Read($$"""
            # Printer setup, 2nd floor

            Install the driver from **\\print01\ricoh** _first_, then ~~reboot~~ log off.
            See [the UPS manual.pdf](/api/v1/files/{{Manual}}/ups-manual.pdf "Manual") and https://support.example.com/ricoh.

            ![Cage B, rack 4](/api/v1/files/{{Image}}/cage-b.png)

            | Printer | Queue |
            |---|---|
            | Ricoh C3000 | `\\print01\floor2` |

            - [x] Driver installed
            - [ ] Test page

            > Ask Anna &amp; Ben.

            ```mermaid
            flowchart LR
              fw[fw-zrh-01] --> core[sw-core-01]
            ```

            <b>raw</b> HTML stays text.

            [ref]: https://hidden.example.com/target
            """);
        Assert.AreEqual("""
            Printer setup, 2nd floor
            Install the driver from \print01\ricoh first, then reboot log off.
            See the UPS manual.pdf and https://support.example.com/ricoh.
            Cage B, rack 4
            Printer	Queue
            Ricoh C3000	\\print01\floor2
            Driver installed
            Test page
            Ask Anna & Ben.
            flowchart LR
              fw[fw-zrh-01] --> core[sw-core-01]
            <b>raw</b> HTML stays text.
            """, facts.PlainText);
        StringAssert.DoesNotMatch(facts.PlainText, new System.Text.RegularExpressions.Regex("api/v1/files|hidden\\.example|Manual\"|ups-manual\\.pdf"));
        CollectionAssert.AreEqual(new[] { Manual, Image }, facts.FileIds.ToArray());
    }

    [TestMethod]
    public void Files_and_pages_are_read_from_links_and_images_once_each_in_order()
    {
        var facts = MarkdownText.Read($"""
            See [KB-12](/pnmt-admin/kb/12), its [history](/pnmt-admin/kb/12/history) and [KB-7](/pnmt-admin/kb/7#steps).
            ![again](/api/v1/files/{Image}/x.png) ![](/api/v1/files/{Image.ToString().ToUpperInvariant()}) [manual][m]
            Not PNMT's: ![a](https://example.com/api/v1/files/{Manual}/x.png), [b](/api/v1/filesx/{Manual}), [c](/pnmt-admin/kb/abc),
            [d](/pnmt-admin/kb/0), [e](/pnmt-admin/kbx/3), and a bare /api/v1/files/{Manual}/y.png or `/pnmt-admin/kb/99`.

            [m]: /api/v1/files/{Manual}/ups.pdf
            """);
        CollectionAssert.AreEqual(new[] { Image, Manual }, facts.FileIds.ToArray());
        CollectionAssert.AreEqual(new[] { 12, 7 }, facts.PageNumbers.ToArray());
    }

    [TestMethod]
    public void Files_in_footnotes_and_anywhere_in_the_raw_text_count_after_the_links_so_none_is_lost()
    {
        var footnote = Guid.Parse("0195aaaa-bbbb-7ccc-8ddd-eeeeffff0001");
        var inCode = Guid.Parse("0195aaaa-bbbb-7ccc-8ddd-eeeeffff0002");
        var absolute = Guid.Parse("0195aaaa-bbbb-7ccc-8ddd-eeeeffff0003");
        var facts = MarkdownText.Read($"""
            The UPS[^ups] is in [the manual](/api/v1/files/{Manual}/ups.pdf).

            Copy it with `curl -O https://pnmt/api/v1/files/{inCode}/x.bin`; the old link was <https://pnmt.example.com/api/v1/files/{absolute}/old.png>.
            <!-- /api/v1/files/{Manual} -->

            [^ups]: Its wiring: ![wiring](/api/v1/files/{footnote}/wiring.png)
            """);
        CollectionAssert.AreEqual(new[] { Manual, footnote, inCode, absolute }, facts.FileIds.ToArray());
        StringAssert.Contains(facts.PlainText, "Its wiring: wiring");
    }

    [TestMethod]
    public void Empty_and_odd_input_read_safely()
    {
        Assert.AreEqual(new MarkdownFacts("", [], []).PlainText, MarkdownText.Read("").PlainText);
        Assert.AreEqual("unclosed emphasis and a [link", MarkdownText.Read("**unclosed* _emphasis_ and a [link").PlainText.Replace("*", ""));
        Assert.AreEqual("a\nb", MarkdownText.Read("a  \nb").PlainText, "a hard line break");
        Assert.AreEqual(MarkdownText.FileId($"/api/v1/files/{Image}"), Image);
        Assert.IsNull(MarkdownText.FileId($"/api/v1/files/{Image}x"));
        Assert.AreEqual(42, MarkdownText.PageNumber("/pnmt-admin/kb/42?x=1"));
    }
}
