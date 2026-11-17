using System.Text.Json;
using PNMTD.Advanced;

namespace PNMTD.Tests.Advanced;

/// <summary>The paths of docs/advanced-sensor-plan.md §6.1: parsing and formatting, brackets and escapes, indexes, *, errors, limits.</summary>
[TestClass]
public sealed class AdvancedPathTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [TestMethod]
    [DataRow("disk.free_gb", "disk.free_gb")]
    [DataRow("$.disk.free_gb", "disk.free_gb")]
    [DataRow("$[\"disk\"].free_gb", "disk.free_gb")]
    [DataRow("[\"free space\"]", "[\"free space\"]")]
    [DataRow("disk[\"a.b\"]", "disk[\"a.b\"]")]
    [DataRow("disk['a.b']", "disk[\"a.b\"]")]
    [DataRow("jobs[0].state", "jobs[0].state")]
    [DataRow("jobs[-1].state", "jobs[-1].state")]
    [DataRow("jobs[*].state", "jobs[*].state")]
    [DataRow("checks.*", "checks[*]")]
    [DataRow("*.status", "[*].status")]
    [DataRow("[\"größe\"].wert", "[\"größe\"].wert")]
    [DataRow("a-b_c.d1", "a-b_c.d1")]
    [DataRow("x[\"quote\\\"d\"]", "x[\"quote\\\"d\"]")]
    [DataRow("x[\"tab\\t\"]", "x[\"tab\\t\"]")]
    [DataRow("x[\"\\u00e9\"]", "x[\"é\"]")]
    public void ParsesAndFormatsRoundTrip(string text, string normalized)
    {
        var path = AdvancedPath.Parse(text);
        Assert.AreEqual(normalized, path.Text);
        Assert.AreEqual(normalized, AdvancedPath.Parse(path.Text).Text, "the normalized form parses to itself");
    }

    [TestMethod]
    [DataRow("", "A path")]
    [DataRow("$", "inside the report")]
    [DataRow("a..b", "Position 3")]
    [DataRow(".a", "Position 1")]
    [DataRow("a.", "Position 3")]
    [DataRow("1abc", "Position 1")]
    [DataRow("a[", "Position 3")]
    [DataRow("a[x]", "Position 3")]
    [DataRow("a[1", "Position 4")]
    [DataRow("a[\"open", "a closing")]
    [DataRow("a b", "Position 2")]
    [DataRow("a[?(@.x)]", "Position 3")]
    [DataRow("a..*", "Position 3")]
    [DataRow("größe", "Position 3")]
    public void RejectsWithAPosition(string text, string expected)
    {
        Assert.IsFalse(AdvancedPath.TryParse(text, out _, out var error));
        StringAssert.Contains(error, expected);
    }

    [TestMethod]
    public void KnowsWildcards()
    {
        Assert.IsFalse(AdvancedPath.Parse("jobs[1].state").HasWildcard);
        Assert.IsTrue(AdvancedPath.Parse("jobs[*].state").HasWildcard);
        Assert.IsTrue(AdvancedPath.Parse("checks.*.status").HasWildcard);
    }

    [TestMethod]
    public void SelectsMatchesWithConcretePaths()
    {
        var report = Json("""{"jobs":[{"name":"db","state":"done"},{"name":"files","state":"failed"},{"name":"x"}],"checks":{"db:ping":{"status":"pass"},"disk":{"status":"warn"}},"a b":1}""");
        CollectionAssert.AreEqual(new[] { "jobs[0].state", "jobs[1].state" }, AdvancedPath.Parse("jobs[*].state").Select(report).Select(m => m.Path).ToArray());
        CollectionAssert.AreEqual(new[] { "checks[\"db:ping\"].status", "checks.disk.status" },
            AdvancedPath.Parse("checks.*.status").Select(report).Select(m => m.Path).ToArray());
        Assert.AreEqual("\"files\"", AdvancedPath.Parse("jobs[-2].name").Select(report).Single().Value.GetRawText());
        Assert.AreEqual("jobs[1].name", AdvancedPath.Parse("jobs[-2].name").Select(report).Single().Path, "a negative index is shown as it resolved");
        Assert.AreEqual(1, AdvancedPath.Parse("[\"a b\"]").Select(report).Count);
        Assert.AreEqual(0, AdvancedPath.Parse("jobs[3]").Select(report).Count, "past the end");
        Assert.AreEqual(0, AdvancedPath.Parse("jobs.name").Select(report).Count, "a member of a list is nothing");
        Assert.AreEqual(0, AdvancedPath.Parse("nope[*]").Select(report).Count);
        Assert.IsTrue(AdvancedPath.Parse("jobs[0].name").TryGet(report, out var name));
        Assert.AreEqual("db", name.GetString());
    }

    [TestMethod]
    public void LimitsSegmentsAndMatches()
    {
        var sixteen = string.Join(".", Enumerable.Range(0, 16).Select(i => $"k{i}"));
        Assert.IsTrue(AdvancedPath.TryParse(sixteen, out _, out _));
        Assert.IsFalse(AdvancedPath.TryParse(sixteen + ".k16", out _, out var error));
        StringAssert.Contains(error, "16");
        Assert.IsFalse(AdvancedPath.TryParse(new string('a', 501), out _, out _));

        var many = Json("[" + string.Join(",", Enumerable.Range(0, 1500)) + "]");
        var root = Json($$"""{"x":{{many.GetRawText()}}}""");
        Assert.AreEqual(AdvancedLimits.MaxMatches + 1, AdvancedPath.Parse("x[*]").Select(root).Count, "stops one past the limit");
    }

    [TestMethod]
    public void EnumeratesPathsForTheEditor()
    {
        var paths = AdvancedPath.Enumerate(Json("""{"status":"ok","disk":{"free_gb":3.2},"jobs":[{"state":"done"},{"state":"failed"}],"tags":["a"]}"""));
        var names = paths.Select(p => p.Path).ToList();
        CollectionAssert.IsSubsetOf(new[] { "status", "disk", "disk.free_gb", "jobs", "jobs[0]", "jobs[0].state", "jobs[*].state", "tags", "tags[0]" }, names);
        Assert.AreEqual("3.2", paths.Single(p => p.Path == "disk.free_gb").Preview);
        Assert.AreEqual(JsonValueKind.Number, paths.Single(p => p.Path == "disk.free_gb").Kind);
    }
}
