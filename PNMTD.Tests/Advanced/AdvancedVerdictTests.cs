using System.Text.Json;
using PNMTD.Advanced;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;

namespace PNMTD.Tests.Advanced;

/// <summary>
/// The verdict of docs/advanced-sensor-plan.md §6.4, pure: each operator with each kind, the quantifiers, when missing, the
/// severity, the health words, ages at the boundary, changes, precedence, the messages and the keys.
/// </summary>
[TestClass]
public sealed class AdvancedVerdictTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static AdvancedRuleSpec Rule(string path, AdvancedOperator op, string? operand = null, string? operand2 = null,
        AdvancedQuantifier quantifier = AdvancedQuantifier.All, AdvancedWhenMissing whenMissing = AdvancedWhenMissing.Fail,
        AdvancedSeverity severity = AdvancedSeverity.Fail, bool ignoreCase = false, string? name = null, bool enabled = true) =>
        new(Guid.NewGuid(), name ?? $"{path} {op}", enabled, path, op, operand, operand2, ignoreCase, quantifier, whenMissing, severity);

    private static AdvancedOutcome Outcome(string json, AdvancedRuleSpec rule, string? previous = null, DateTime? at = null) =>
        AdvancedRules.Evaluate(rule, Json(json), previous is null ? null : Json(previous), at ?? Now).Outcome;

    private static AdvancedRuleResult Result(string json, AdvancedRuleSpec rule, string? previous = null) =>
        AdvancedRules.Evaluate(rule, Json(json), previous is null ? null : Json(previous), Now);

    private static AdvancedVerdict Verdict(string json, params AdvancedRuleSpec[] rules) =>
        AdvancedVerdict.Evaluate(rules, AdvancedReportInput.Of(Json(json)), null, Now, Now, TimeSpan.Zero, TimeSpan.Zero)!;

    private const AdvancedOutcome Pass = AdvancedOutcome.Pass;
    private const AdvancedOutcome Fail = AdvancedOutcome.Fail;
    private const AdvancedOutcome Warn = AdvancedOutcome.Warn;

    [TestMethod]
    public void ComparesNumbersAsNumbers()
    {
        const string r = """{"n":5,"s":"5.0","t":"abc","b":true,"z":null,"l":[1],"o":{"a":1},"big":1e200,"d":0.30000000000000004}""";
        Assert.AreEqual(Pass, Outcome(r, Rule("n", AdvancedOperator.Eq, "5.0")), "5 equals 5.0");
        Assert.AreEqual(Fail, Outcome(r, Rule("n", AdvancedOperator.Ne, "5")));
        Assert.AreEqual(Fail, Outcome(r, Rule("s", AdvancedOperator.Eq, "5")), "eq compares a string as text");
        Assert.AreEqual(Pass, Outcome(r, Rule("s", AdvancedOperator.Ge, "5")), "numeric operators read a number in a string");
        Assert.AreEqual(Pass, Outcome(r, Rule("n", AdvancedOperator.Lt, "6")));
        Assert.AreEqual(Fail, Outcome(r, Rule("n", AdvancedOperator.Lt, "5")));
        Assert.AreEqual(Pass, Outcome(r, Rule("n", AdvancedOperator.Le, "5")));
        Assert.AreEqual(Pass, Outcome(r, Rule("n", AdvancedOperator.Gt, "4.99")));
        Assert.AreEqual(Pass, Outcome(r, Rule("n", AdvancedOperator.Between, "5", "6")), "the ends are included");
        Assert.AreEqual(Fail, Outcome(r, Rule("n", AdvancedOperator.Outside, "5", "6")));
        Assert.AreEqual(Pass, Outcome(r, Rule("big", AdvancedOperator.Gt, "1e150")), "a double where a decimal doesn't fit");
        Assert.AreEqual(Fail, Outcome(r, Rule("d", AdvancedOperator.Eq, "0.3")), "decimal: no rounding to 0.3");
        foreach (var kind in new[] { "t", "b", "z", "l", "o" })
        {
            var result = Result(r, Rule(kind, AdvancedOperator.Ge, "1"));
            Assert.AreEqual((Fail, (AdvancedReason?)AdvancedReason.WrongKind), (result.Outcome, result.Reason), kind);
            Assert.AreEqual("not a number", result.Detail);
        }
    }

    [TestMethod]
    public void ComparesBooleansNullsAndText()
    {
        const string r = """{"b":true,"z":null,"t":"Ready","l":[1]}""";
        Assert.AreEqual(Pass, Outcome(r, Rule("b", AdvancedOperator.Eq, "true")));
        Assert.AreEqual(Pass, Outcome(r, Rule("z", AdvancedOperator.Eq, "null")));
        Assert.AreEqual(Pass, Outcome(r, Rule("z", AdvancedOperator.Ne, "x")), "null isn't x");
        Assert.AreEqual(Fail, Outcome(r, Rule("t", AdvancedOperator.Eq, "ready")), "case-sensitive");
        Assert.AreEqual(Pass, Outcome(r, Rule("t", AdvancedOperator.Eq, "ready", ignoreCase: true)));
        Assert.AreEqual(AdvancedReason.WrongKind, Result(r, Rule("l", AdvancedOperator.Eq, "1")).Reason);
        Assert.AreEqual(Pass, Outcome(r, Rule("b", AdvancedOperator.IsTrue)));
        Assert.AreEqual(Fail, Outcome("""{"b":"true"}""", Rule("b", AdvancedOperator.IsTrue)), "the text true is not true");
        Assert.AreEqual(Pass, Outcome(r, Rule("z", AdvancedOperator.IsNull)));
        Assert.AreEqual(Fail, Outcome(r, Rule("t", AdvancedOperator.IsNull)));
        Assert.AreEqual(Pass, Outcome(r, Rule("t", AdvancedOperator.NotNull)));
    }

    [TestMethod]
    public void TextOperators()
    {
        const string r = """{"v":"PNMT 1.14.0","n":3.20,"tags":["prod","eu"],"o":{}}""";
        Assert.AreEqual(Pass, Outcome(r, Rule("v", AdvancedOperator.Contains, "1.14")));
        Assert.AreEqual(Pass, Outcome(r, Rule("v", AdvancedOperator.NotContains, "beta")));
        Assert.AreEqual(Pass, Outcome(r, Rule("v", AdvancedOperator.StartsWith, "pnmt", ignoreCase: true)));
        Assert.AreEqual(Pass, Outcome(r, Rule("v", AdvancedOperator.EndsWith, ".0")));
        Assert.AreEqual(Pass, Outcome(r, Rule("n", AdvancedOperator.EndsWith, ".20")), "a number as its JSON text");
        Assert.AreEqual(Pass, Outcome(r, Rule("tags", AdvancedOperator.Contains, "prod")), "a list contains an item");
        Assert.AreEqual(Fail, Outcome(r, Rule("tags", AdvancedOperator.Contains, "pro")));
        Assert.AreEqual(AdvancedReason.WrongKind, Result(r, Rule("o", AdvancedOperator.Contains, "x")).Reason);
        Assert.AreEqual(Pass, Outcome(r, Rule("v", AdvancedOperator.Matches, @"^PNMT \d+\.\d+")));
        Assert.AreEqual(Pass, Outcome(r, Rule("v", AdvancedOperator.NotMatches, "beta")));
        Assert.AreEqual(Pass, Outcome(r, Rule("v", AdvancedOperator.OneOf, "PNMT 1.13.0\nPNMT 1.14.0\n")));
        Assert.AreEqual(Pass, Outcome(r, Rule("n", AdvancedOperator.OneOf, "3.2\n4")), "numbers compare as numbers in a list");
        Assert.AreEqual(Fail, Outcome(r, Rule("v", AdvancedOperator.NotOneOf, "pnmt 1.14.0", ignoreCase: true)));
    }

    [TestMethod]
    public void APatternThatTakesTooLongTimesOut()
    {
        var text = new string('a', 40) + "!";
        var result = Result($$"""{"v":"{{text}}"}""", Rule("v", AdvancedOperator.Matches, "^(a+)+$", severity: AdvancedSeverity.Warn));
        Assert.AreEqual((Warn, (AdvancedReason?)AdvancedReason.Timeout), (result.Outcome, result.Reason), "the rule's severity");
    }

    [TestMethod]
    public void KindOperators()
    {
        const string r = """{"e":"","l":[],"o":{},"z":null,"x":0,"s":" "}""";
        foreach (var path in new[] { "e", "l", "o", "z" })
        {
            Assert.AreEqual(Pass, Outcome(r, Rule(path, AdvancedOperator.Empty)), path);
        }
        Assert.AreEqual(Pass, Outcome(r, Rule("x", AdvancedOperator.NotEmpty)), "0 isn't empty");
        Assert.AreEqual(Pass, Outcome(r, Rule("s", AdvancedOperator.NotEmpty)));
        Assert.AreEqual(Pass, Outcome(r, Rule("x", AdvancedOperator.Exists)));
        Assert.AreEqual(Fail, Outcome(r, Rule("y", AdvancedOperator.Exists)));
        Assert.AreEqual(Pass, Outcome(r, Rule("y", AdvancedOperator.Absent)));
        Assert.AreEqual(Pass, Outcome(r, Rule("z", AdvancedOperator.Exists)), "null exists");
        Assert.AreEqual(Fail, Outcome(r, Rule("y", AdvancedOperator.Exists, whenMissing: AdvancedWhenMissing.Pass)), "exists decides for itself");
    }

    [TestMethod]
    [DataRow("pass", AdvancedOutcome.Pass)]
    [DataRow("warn", AdvancedOutcome.Warn)]
    [DataRow("fail", AdvancedOutcome.Fail)]
    [DataRow("UP", AdvancedOutcome.Pass)]
    [DataRow("DOWN", AdvancedOutcome.Fail)]
    [DataRow("Healthy", AdvancedOutcome.Pass)]
    [DataRow("Degraded", AdvancedOutcome.Warn)]
    [DataRow("Unhealthy", AdvancedOutcome.Fail)]
    [DataRow("ok", AdvancedOutcome.Pass)]
    [DataRow("green", AdvancedOutcome.Pass)]
    [DataRow("yellow", AdvancedOutcome.Warn)]
    [DataRow("error", AdvancedOutcome.Fail)]
    public void HealthWordsOfTheCommonFormats(string word, AdvancedOutcome expected)
    {
        Assert.AreEqual(expected, Outcome($$"""{"status":"{{word}}"}""", Rule("status", AdvancedOperator.Healthy)));
        Assert.AreEqual(expected == Fail ? Warn : expected,
            Outcome($$"""{"status":"{{word}}"}""", Rule("status", AdvancedOperator.Healthy, severity: AdvancedSeverity.Warn)), "a warn rule");
    }

    [TestMethod]
    public void HealthyTakesBooleans()
    {
        Assert.AreEqual(Pass, Outcome("""{"up":true}""", Rule("up", AdvancedOperator.Healthy)));
        Assert.AreEqual(Fail, Outcome("""{"up":false}""", Rule("up", AdvancedOperator.Healthy)));
        Assert.AreEqual(Fail, Outcome("""{"up":1}""", Rule("up", AdvancedOperator.Healthy)));
    }

    [TestMethod]
    public void AgesAtTheBoundaryWithOffsetsAndUnixTimes()
    {
        var rule = Rule("t", AdvancedOperator.NewerThan, "26h");
        var older = Rule("t", AdvancedOperator.OlderThan, "26h");
        string At(DateTime utc) => $$"""{"t":"{{utc:yyyy-MM-ddTHH:mm:ssZ}}"}""";
        Assert.AreEqual(Pass, Outcome(At(Now.AddHours(-26).AddSeconds(1)), rule));
        Assert.AreEqual(Fail, Outcome(At(Now.AddHours(-26)), rule), "exactly 26 h is not newer");
        Assert.AreEqual(Fail, Outcome(At(Now.AddHours(-26)), older), "nor older");
        Assert.AreEqual(Pass, Outcome(At(Now.AddHours(-26).AddSeconds(-1)), older));
        Assert.AreEqual(Pass, Outcome("""{"t":"2026-11-17T02:14:03+01:00"}""", rule), "with an offset");
        Assert.AreEqual(Fail, Outcome("""{"t":"2026-11-15T12:00:00+02:00"}""", rule), "50 h");
        var unix = new DateTimeOffset(Now.AddHours(-1)).ToUnixTimeSeconds();
        Assert.AreEqual(Pass, Outcome($$"""{"t":{{unix}}}""", rule), "Unix seconds");
        Assert.AreEqual(Pass, Outcome($$"""{"t":{{unix * 1000}}}""", rule), "milliseconds above 10^11");
        Assert.AreEqual(Fail, Outcome($$"""{"t":{{unix}}}""", Rule("t", AdvancedOperator.NewerThan, "30m")));
        var assumed = Result("""{"t":"2026-11-17T11:00:00"}""", rule);
        Assert.AreEqual((Pass, true), (assumed.Outcome, assumed.AssumedUtc), "without an offset: UTC, and the outcome says so");
        Assert.AreEqual(AdvancedReason.WrongKind, Result("""{"t":"yesterday"}""", rule).Reason);
        Assert.AreEqual(Fail, Outcome(At(Now.AddHours(-2)), rule, at: Now.AddHours(25)), "an age moves with the time it is judged at");
        Assert.AreEqual(Pass, Outcome(At(Now.AddDays(-1)), Rule("t", AdvancedOperator.NewerThan, "PT26H")), "ISO 8601 durations");
    }

    [TestMethod]
    public void Counts()
    {
        const string r = """{"jobs":[{"s":"a"},{"s":"b"},{"s":"c"}],"o":{"a":1,"b":2},"t":"abcd","n":5}""";
        Assert.AreEqual(Pass, Outcome(r, Rule("jobs", AdvancedOperator.CountEq, "3")));
        Assert.AreEqual(Pass, Outcome(r, Rule("jobs[*]", AdvancedOperator.CountGe, "3")), "the matches of a path with *");
        Assert.AreEqual(Pass, Outcome(r, Rule("nothing[*]", AdvancedOperator.CountLe, "0")), "no match is a count of 0, not missing");
        Assert.AreEqual(Pass, Outcome(r, Rule("o", AdvancedOperator.CountEq, "2")));
        Assert.AreEqual(Pass, Outcome(r, Rule("t", AdvancedOperator.CountGe, "4")));
        Assert.AreEqual(AdvancedReason.WrongKind, Result(r, Rule("n", AdvancedOperator.CountGe, "1")).Reason);
        var failing = Result(r, Rule("jobs[*]", AdvancedOperator.CountGe, "5"));
        Assert.AreEqual("jobs[*] = 3 matches", $"{failing.Actual[0].Path} = {failing.Actual[0].Value}");
    }

    [TestMethod]
    public void ChangesAgainstThePreviousReport()
    {
        Assert.AreEqual(Pass, Outcome("""{"c":5}""", Rule("c", AdvancedOperator.Increased)), "the first report");
        Assert.AreEqual(Pass, Outcome("""{"c":6}""", Rule("c", AdvancedOperator.Increased), """{"c":5}"""));
        Assert.AreEqual(Fail, Outcome("""{"c":5}""", Rule("c", AdvancedOperator.Increased), """{"c":5}"""), "a repeat doesn't increase");
        Assert.AreEqual(Pass, Outcome("""{"c":5}""", Rule("c", AdvancedOperator.NotDecreased), """{"c":5}"""));
        Assert.AreEqual(Fail, Outcome("""{"c":4}""", Rule("c", AdvancedOperator.NotDecreased), """{"c":5}"""));
        Assert.AreEqual(Pass, Outcome("""{"c":4}""", Rule("c", AdvancedOperator.Decreased), """{"c":"5"}"""));
        Assert.AreEqual(Pass, Outcome("""{"v":{"a":[1,2]}}""", Rule("v", AdvancedOperator.Unchanged), """{"v":{"a":[1,2.0]}}"""), "by value");
        Assert.AreEqual(Pass, Outcome("""{"v":"2"}""", Rule("v", AdvancedOperator.Changed), """{"v":"1"}"""));
        Assert.AreEqual(Pass, Outcome("""{"new":1}""", Rule("new", AdvancedOperator.Changed), """{"v":"1"}"""), "nothing to compare with");
        Assert.AreEqual(Fail, Outcome("""{"j":[{"n":1},{"n":2}]}""", Rule("j[*].n", AdvancedOperator.Increased), """{"j":[{"n":1},{"n":1}]}"""),
            "each match against its own path");
        var shown = Result("""{"c":4}""", Rule("c", AdvancedOperator.NotDecreased), """{"c":5}""");
        Assert.AreEqual("4 (was 5)", shown.Actual[0].Value);
    }

    [TestMethod]
    public void QuantifiersWithNoneOneAndManyMatches()
    {
        const string r = """{"jobs":[{"state":"done","errors":0},{"state":"failed","errors":3},{"state":"done","errors":0}],"empty":[]}""";
        Assert.AreEqual(Fail, Outcome(r, Rule("jobs[*].state", AdvancedOperator.Eq, "done")), "all");
        Assert.AreEqual(Pass, Outcome(r, Rule("jobs[*].state", AdvancedOperator.Eq, "done", quantifier: AdvancedQuantifier.Any)));
        Assert.AreEqual(Fail, Outcome(r, Rule("jobs[*].state", AdvancedOperator.Eq, "failed", quantifier: AdvancedQuantifier.None)));
        Assert.AreEqual(Pass, Outcome(r, Rule("jobs[*].state", AdvancedOperator.Eq, "lost", quantifier: AdvancedQuantifier.None)));
        Assert.AreEqual(Pass, Outcome(r, Rule("jobs[0].state", AdvancedOperator.Eq, "done", quantifier: AdvancedQuantifier.None)),
            "one value: the quantifier isn't read");
        var none = Result(r, Rule("jobs[*].state", AdvancedOperator.Eq, "failed", quantifier: AdvancedQuantifier.None));
        Assert.AreEqual("jobs[1].state = \"failed\"", string.Join(", ", none.Actual.Select(a => $"{a.Path} = {a.Value}")), "the match that decided");
        CollectionAssert.AreEqual(new[] { "jobs[0].state", "jobs[1].state", "jobs[2].state" }, none.Matches.ToArray(), "every match marked");
        Assert.AreEqual(AdvancedReason.Missing, Result(r, Rule("empty[*]", AdvancedOperator.Eq, "x")).Reason, "an empty list under * is missing");
    }

    [TestMethod]
    public void WhenMissingAndSeverity()
    {
        const string r = """{"a":1}""";
        Assert.AreEqual(Fail, Outcome(r, Rule("b", AdvancedOperator.Eq, "1")), "a value that disappears fails by default");
        Assert.AreEqual(Warn, Outcome(r, Rule("b", AdvancedOperator.Eq, "1", whenMissing: AdvancedWhenMissing.Warn)));
        Assert.AreEqual(Pass, Outcome(r, Rule("b", AdvancedOperator.Eq, "1", whenMissing: AdvancedWhenMissing.Pass)));
        Assert.AreEqual(Warn, Outcome(r, Rule("b", AdvancedOperator.Eq, "1", severity: AdvancedSeverity.Warn)), "fail when missing: by the severity");
        Assert.AreEqual(Warn, Outcome(r, Rule("a", AdvancedOperator.Eq, "2", severity: AdvancedSeverity.Warn)));
        Assert.AreEqual(AdvancedOutcome.Off, Outcome(r, Rule("a", AdvancedOperator.Eq, "2", enabled: false)));
    }

    [TestMethod]
    public void TooManyMatchesFailTheRule()
    {
        var json = """{"x":[""" + string.Join(",", Enumerable.Range(0, 1001)) + "]}";
        var result = Result(json, Rule("x[*]", AdvancedOperator.Ge, "0"));
        Assert.AreEqual((Fail, (AdvancedReason?)AdvancedReason.TooManyMatches), (result.Outcome, result.Reason));
    }

    [TestMethod]
    public void TheExampleOfThePlan()
    {
        // docs/advanced-sensor-plan.md §6.7, judged at 17 Nov 2026 12:00 UTC.
        const string report = """
            {
              "status": "ok",
              "message": "Nightly backup done",
              "time": "2026-11-17T02:14:03+01:00",
              "backup": { "finished": "2026-11-17T02:14:03+01:00", "size_bytes": 48213307392, "duration_s": 3712 },
              "disk": { "free_gb": 3.2, "total_gb": 2000 },
              "jobs": [
                { "name": "db", "state": "done", "errors": 0 },
                { "name": "files", "state": "failed", "errors": 3 }
              ]
            }
            """;
        var status = Rule("status", AdvancedOperator.Healthy, whenMissing: AdvancedWhenMissing.Pass, name: "The app's status");
        var backup = Rule("backup.finished", AdvancedOperator.NewerThan, "26h", name: "Last backup");
        var disk = Rule("disk.free_gb", AdvancedOperator.Ge, "5", name: "Disk space");
        var job = Rule("jobs[*].state", AdvancedOperator.Eq, "failed", quantifier: AdvancedQuantifier.None, name: "No failed job");
        var errors = Rule("jobs[*].errors", AdvancedOperator.Le, "0", severity: AdvancedSeverity.Warn, name: "No job errors");
        var verdict = Verdict(report, status, backup, disk, job, errors);
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_FAILED, verdict.Code);
        CollectionAssert.AreEqual(new[] { Pass, Pass, Fail, Fail, Warn }, verdict.Results.Select(r => r.Outcome).ToArray());
        Assert.AreEqual("2 rules fail: Disk space (disk.free_gb = 3.2, must be at least 5); No failed job (jobs[1].state = \"failed\")", verdict.Message);
        Assert.AreEqual($"495|{disk.Id:N},{job.Id:N}", verdict.Key);

        // Tomorrow: 3.1 GB and the same failed job are the same problem; the job succeeding is another.
        var tomorrow = Verdict(report.Replace("3.2", "3.1"), status, backup, disk, job, errors);
        Assert.AreEqual(verdict.Key, tomorrow.Key, "a changed value keeps the key");
        var fixedJob = Verdict(report.Replace("\"failed\"", "\"done\""), status, backup, disk, job, errors);
        Assert.AreEqual($"495|{disk.Id:N}", fixedJob.Key, "another failing rule changes it");

        var warning = Verdict(report.Replace("3.2", "30").Replace("\"failed\"", "\"done\""), status, backup, disk, job, errors);
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_WARNING, warning.Code);
        Assert.AreEqual("Warning: No job errors (jobs[1].errors = 3, must be at most 0)", warning.Message);
        Assert.AreEqual($"296|{errors.Id:N}", warning.Key);
    }

    [TestMethod]
    public void PrecedenceOfStaleInvalidFailedWarningOk()
    {
        var fail = Rule("a", AdvancedOperator.Eq, "1");
        var warn = Rule("b", AdvancedOperator.Eq, "1", severity: AdvancedSeverity.Warn);
        var report = AdvancedReportInput.Of(Json("""{"a":2,"b":2}"""));
        var interval = TimeSpan.FromMinutes(5);
        var grace = TimeSpan.FromMinutes(1);
        AdvancedVerdict At(AdvancedReportInput input, TimeSpan age, params AdvancedRuleSpec[] rules) =>
            AdvancedVerdict.Evaluate(rules, input, null, Now, Now - age, interval, grace)!;

        Assert.IsNull(AdvancedVerdict.Evaluate([fail], null, null, Now, null, interval, grace), "no report: no verdict");
        Assert.AreEqual(PNMTStatusCodes.HEARTBEAT_MISSING, At(report, TimeSpan.FromMinutes(6).Add(TimeSpan.FromSeconds(1)), fail).Code);
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_FAILED, At(report, TimeSpan.FromMinutes(6), fail).Code, "exactly interval plus grace is not stale");
        var invalid = new AdvancedReportInput(false, AdvancedReportError.DuplicateKey, "The report has the key \"a\" twice", null);
        Assert.AreEqual(PNMTStatusCodes.HEARTBEAT_MISSING, At(invalid, TimeSpan.FromHours(1), fail).Code, "460 before 496");
        var bad = At(invalid, TimeSpan.Zero, fail);
        Assert.AreEqual((PNMTStatusCodes.ADVANCED_INVALID, "496|duplicate-key"), (bad.Code, bad.Key));
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_FAILED, At(report, TimeSpan.Zero, warn, fail).Code, "495 before 296");
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_WARNING, At(report, TimeSpan.Zero, warn).Code);
        var ok = At(AdvancedReportInput.Of(Json("""{"a":1}""")), TimeSpan.Zero, fail);
        Assert.AreEqual((PNMTStatusCodes.ADVANCED_OK, "295", "All rules hold"), (ok.Code, ok.Key, ok.Message));
        Assert.AreEqual("Report received", At(report, TimeSpan.Zero).Message, "without rules");
        var missing = At(report, TimeSpan.FromHours(1), fail);
        Assert.AreEqual(1, missing.Results.Count, "the rules are evaluated while stale too");
        StringAssert.StartsWith(missing.Message, "No report since ");
        StringAssert.EndsWith(missing.Message, "(expected every 5 min, 1 min grace)");
        Assert.AreEqual(PNMTStatusCodes.ADVANCED_FAILED,
            AdvancedVerdict.Evaluate([fail], report, null, Now, Now.AddHours(-1), interval, grace, checkStale: false)!.Code, "460 held back");
    }

    [TestMethod]
    public void MessagesAreCutCleanAndAbsolute()
    {
        var long_ = new string('x', 300);
        var rule = Rule("v", AdvancedOperator.Eq, "y", name: "Version");
        var verdict = Verdict($$"""{"v":"line1\u0007\nline2 {{long_}}"}""", rule);
        var part = verdict.Message["1 rule fails: ".Length..];
        StringAssert.StartsWith(part, "Version (v = \"line1 line2 xxx");
        Assert.IsFalse(verdict.Message.Any(char.IsControl), "no control characters");
        Assert.IsTrue(part.Length < 120, part);

        var many = Enumerable.Range(0, 50).Select(i => Rule($"k{i}", AdvancedOperator.Eq, "y", name: $"Rule number {i} with a long name to fill")).ToArray();
        var json = "{" + string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"k{i}\":\"{new string('v', 50)}\"")) + "}";
        var all = Verdict(json, many);
        Assert.IsTrue(all.Message.Length <= 1000, $"{all.Message.Length}");
        StringAssert.StartsWith(all.Message, "50 rules fail: Rule number 0");
        StringAssert.Matches(all.Message, new System.Text.RegularExpressions.Regex(@" … and \d+ more$"));

        var original = Clock.TimeZone;
        try
        {
            Clock.TimeZone = Clock.FromName("Europe/Zurich");
            var age = Verdict("""{"t":"2026-11-15T09:30:00Z"}""", Rule("t", AdvancedOperator.NewerThan, "1d", name: "Backup"));
            Assert.AreEqual("1 rule fails: Backup (t = 15 Nov 2026 10:30, must be newer than 1 d)", age.Message, "dates absolute, in TZ");
        }
        finally
        {
            Clock.TimeZone = original;
        }
    }

    [TestMethod]
    public void OperandsAreValidated()
    {
        Assert.IsNull(AdvancedRules.ValidateOperand(AdvancedOperator.Ge, "0.25", null));
        Assert.AreEqual("A number, such as 5 or 0.25.", AdvancedRules.ValidateOperand(AdvancedOperator.Ge, "five", null)?.Message);
        Assert.AreEqual("operand2", AdvancedRules.ValidateOperand(AdvancedOperator.Between, "5", "1")?.Field);
        StringAssert.StartsWith(AdvancedRules.ValidateOperand(AdvancedOperator.Matches, "(", null)?.Message, "Not a valid pattern");
        StringAssert.StartsWith(AdvancedRules.ValidateOperand(AdvancedOperator.OlderThan, "a day", null)?.Message, "A duration");
        Assert.IsNull(AdvancedRules.ValidateOperand(AdvancedOperator.OlderThan, "1h30m", null));
        Assert.IsNotNull(AdvancedRules.ValidateOperand(AdvancedOperator.CountGe, "-1", null));
        Assert.IsNotNull(AdvancedRules.ValidateOperand(AdvancedOperator.OneOf, "\n \n", null));
        Assert.IsNull(AdvancedRules.ValidateOperand(AdvancedOperator.Eq, "", null), "equal to the empty text");
        Assert.IsNull(AdvancedRules.ValidateOperand(AdvancedOperator.Healthy, null, null));
    }

    [TestMethod]
    public void ConditionsInWords()
    {
        Assert.AreEqual("disk.free_gb ≥ 5", AdvancedRules.Describe(Rule("disk.free_gb", AdvancedOperator.Ge, "5")));
        Assert.AreEqual("none of jobs[*].state = \"failed\"", AdvancedRules.Describe(Rule("jobs[*].state", AdvancedOperator.Eq, "failed", quantifier: AdvancedQuantifier.None)));
        Assert.AreEqual("every jobs[*].errors ≤ 0", AdvancedRules.Describe(Rule("$.jobs[*].errors", AdvancedOperator.Le, "0")));
        Assert.AreEqual("backup.finished newer than 1 d 2 h", AdvancedRules.Describe(Rule("backup.finished", AdvancedOperator.NewerThan, "26h")));
        Assert.AreEqual("jobs[*] count ≥ 2", AdvancedRules.Describe(Rule("jobs[*]", AdvancedOperator.CountGe, "2")));
        Assert.AreEqual("status = \"OK\" (ignoring case)", AdvancedRules.Describe(Rule("status", AdvancedOperator.Eq, "OK", ignoreCase: true)));
    }

    [TestMethod]
    public void ReportsAreReadWithinTheLimits()
    {
        static AdvancedParsedReport Read(string text) => AdvancedReportReader.Read(System.Text.Encoding.UTF8.GetBytes(text));
        using (var ok = Read("\uFEFF{\"status\":\"ok\",\"message\":\"Done\\u0007 now\",\"time\":1763370000}"))
        {
            Assert.IsTrue(ok.Valid);
            Assert.AreEqual(("ok", "Done now"), (ok.Status, ok.Message));
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1763370000).UtcDateTime, ok.MeasuredAt);
            Assert.AreEqual(64, ok.Sha256.Length);
        }
        Assert.AreEqual(AdvancedReportError.DuplicateKey, Read("""{"disk":1,"disk":2}""").Kind);
        Assert.AreEqual("The report has the key \"disk\" twice", Read("""{"disk":1,"disk":2}""").Error);
        Assert.AreEqual(AdvancedReportError.DuplicateKey, Read("""{"a":{"x":1,"x":2}}""").Kind, "nested too");
        Assert.AreEqual(AdvancedReportError.NotObject, Read("[1]").Kind);
        Assert.AreEqual("The report must be a JSON object, not an array", Read("[1]").Error);
        Assert.AreEqual(AdvancedReportError.TooDeep, Read(new string('[', 40) + new string(']', 40)).Kind);
        var broken = Read("{\n\"a\": 1,\n\"b\": 2 x}");
        Assert.AreEqual(AdvancedReportError.NotJson, broken.Kind);
        StringAssert.StartsWith(broken.Error, "The report isn't valid JSON: line 3, position 8: ");
        Assert.AreEqual(AdvancedReportError.NotJson, Read("{\"a\":1,}").Kind, "no trailing commas");
        Assert.AreEqual(AdvancedReportError.NotJson, Read("{/* c */\"a\":1}").Kind, "no comments");
        StringAssert.StartsWith(Read("  ").Error, "The report is empty");
        Assert.AreEqual("The report is 1.2 MiB, more than 256 KiB", AdvancedReportReader.TooLarge(1_258_291, 262_144).Error);
    }
}
