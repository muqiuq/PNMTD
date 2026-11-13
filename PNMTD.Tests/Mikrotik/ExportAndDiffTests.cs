using PNMTD.Lib.Mikrotik;

namespace PNMTD.Tests.Mikrotik;

/// <summary>Export normalisation and the line diff (docs/mikrotik-plan.md §6.2, §6.4).</summary>
[TestClass]
public sealed class ExportAndDiffTests
{
    private static string Fixture(string version) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikrotik", version, "export.rsc"));

    [TestMethod]
    public void Normalising_drops_only_the_time_line()
    {
        foreach (var version in new[] { "7.13", "7.25.1" })
        {
            var raw = Fixture(version);
            var normalised = ExportText.Normalise(raw);
            Assert.IsFalse(normalised.StartsWith("# 20", StringComparison.Ordinal), version);
            Assert.IsFalse(normalised.Contains('\r'), version);
            StringAssert.EndsWith(normalised, "\n");
            Assert.AreEqual(version, ExportText.Version(raw));
            Assert.IsNull(ExportText.Problem(raw), version);
        }
        // M0: two exports a minute apart differ only in their first line.
        var a = "# 2026-11-12 23:25:22 by RouterOS 7.25.1\r\n# system id = x\r\n/system identity set name=r25\r\n";
        var b = "# 2026-11-12 23:26:24 by RouterOS 7.25.1\n# system id = x\n/system identity set name=r25\n\n";
        Assert.AreEqual(ExportText.Hash(ExportText.Normalise(a)), ExportText.Hash(ExportText.Normalise(b)));
        // The older date format of the first line.
        Assert.AreEqual("/x\n", ExportText.Normalise("# nov/12/2026 23:25:22 by RouterOS 7.13\n/x\n"));
        // 7.13's CALEA lines stay: they are part of what the router can export.
        StringAssert.Contains(ExportText.Normalise(Fixture("7.13")), "#error exporting \"/ip/firewall/calea\"");
    }

    [TestMethod]
    public void An_error_is_not_an_export()
    {
        Assert.IsNotNull(ExportText.Problem("not enough permissions (9)"));
        Assert.IsNotNull(ExportText.Problem("\n"));
        StringAssert.Contains(ExportText.Problem("bad command name export (line 1 column 2)"), "bad command name");
    }

    [TestMethod]
    public void A_unified_diff_as_diff_u_writes_it()
    {
        var before = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"line {i}")) + "\n";
        var after = before.Replace("line 3\n", "line 3 changed\n").Replace("line 11\n", "") + "line 13\n";
        var diff = LineDiff.Compare(before, after);
        Assert.AreEqual(2, diff.Added);
        Assert.AreEqual(2, diff.Removed);
        Assert.AreEqual(2, diff.Hunks.Count, "the changes are more than 6 lines apart");
        Assert.AreEqual("@@ -1,6 +1,6 @@", diff.Hunks[0].Header);
        Assert.AreEqual("@@ -8,5 +8,5 @@", diff.Hunks[1].Header);
        var unified = diff.Unified("a.rsc", "b.rsc");
        StringAssert.StartsWith(unified, "--- a.rsc\n+++ b.rsc\n@@ -1,6 +1,6 @@\n line 1\n line 2\n-line 3\n+line 3 changed\n line 4\n");
        StringAssert.Contains(unified, "-line 11\n line 12\n+line 13\n");
        Assert.IsTrue(LineDiff.Compare(before, before).Identical);
        Assert.AreEqual("", LineDiff.Compare(before, before).Unified("a", "b"));
    }

    [TestMethod]
    public void Changes_close_together_share_a_hunk()
    {
        var before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"{i}")) + "\n";
        var after = before.Replace("\n5\n", "\nfive\n").Replace("\n10\n", "\nten\n");
        var diff = LineDiff.Compare(before, after);
        Assert.AreEqual(1, diff.Hunks.Count, "4 unchanged lines between them, less than 2 × 3");
        Assert.AreEqual("@@ -2,12 +2,12 @@", diff.Hunks[0].Header);
    }

    [TestMethod]
    public void Empty_texts()
    {
        var added = LineDiff.Compare("", "a\nb\n");
        Assert.AreEqual(2, added.Added);
        Assert.AreEqual("@@ -0,0 +1,2 @@", added.Hunks[0].Header);
        var removed = LineDiff.Compare("a\nb\n", "");
        Assert.AreEqual(2, removed.Removed);
        Assert.AreEqual("@@ -1,2 +0,0 @@", removed.Hunks[0].Header);
        Assert.IsTrue(LineDiff.Compare("", "").Identical);
    }

    [TestMethod]
    public void Random_texts_diff_minimally_and_rebuild_both_sides()
    {
        var random = new Random(4711);
        for (var round = 0; round < 300; round++)
        {
            var a = Enumerable.Range(0, random.Next(0, 30)).Select(_ => ((char)('a' + random.Next(0, 4))).ToString()).ToArray();
            var b = Enumerable.Range(0, random.Next(0, 30)).Select(_ => ((char)('a' + random.Next(0, 4))).ToString()).ToArray();
            var oldText = a.Length == 0 ? "" : string.Join('\n', a) + "\n";
            var newText = b.Length == 0 ? "" : string.Join('\n', b) + "\n";
            var diff = LineDiff.Compare(oldText, newText, context: int.MaxValue / 4);
            var lines = diff.Hunks.SelectMany(h => h.Lines).ToList();
            if (!diff.Identical)
            {
                CollectionAssert.AreEqual(a, lines.Where(l => l.Kind != DiffLineKind.Added).Select(l => l.Text).ToArray(), $"round {round}: old side");
                CollectionAssert.AreEqual(b, lines.Where(l => l.Kind != DiffLineKind.Removed).Select(l => l.Text).ToArray(), $"round {round}: new side");
            }
            var lcs = Lcs(a, b);
            Assert.AreEqual(a.Length - lcs, diff.Removed, $"round {round}: minimal");
            Assert.AreEqual(b.Length - lcs, diff.Added, $"round {round}: minimal");
        }
    }

    private static int Lcs(string[] a, string[] b)
    {
        var t = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
            }
        }
        return t[0, 0];
    }

    [TestMethod]
    public void Keyed_line_digests_count_as_the_text_does()
    {
        // docs/mikrotik-backup-plan.md §3.7: the counts of 243 without the previous text.
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var a = ExportText.Normalise(Fixture("7.25.1"));
        var b = a.Replace("/system identity set name=r25", "/system identity set name=office-gw") + "/ip dns set servers=192.0.2.53\n";
        var text = LineDiff.Compare(a, b);
        var keyed = ExportDigest.Count(ExportDigest.Lines(key, a), ExportDigest.Lines(key, b));
        Assert.AreEqual((text.Added, text.Removed), keyed);
        Assert.AreEqual((2, 1), keyed);
        Assert.AreEqual(ExportDigest.Hash(key, a), ExportDigest.Hash(key, a));
        Assert.AreNotEqual(ExportDigest.Hash(key, a), ExportDigest.Hash(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), a), "keyed");
        Assert.AreNotEqual(ExportText.Hash(a), ExportDigest.Hash(key, a));
    }

}
