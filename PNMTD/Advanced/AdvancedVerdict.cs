using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Models;

namespace PNMTD.Advanced;

/// <summary>A report as the verdict needs it: whether it could be read, why not, and its JSON.</summary>
public sealed record AdvancedReportInput(bool Valid, AdvancedReportError? ErrorKind, string? Error, JsonElement? Root)
{
    public static AdvancedReportInput Of(JsonElement root) => new(true, null, null, root);
}

/// <summary>
/// What the rules make of a report, or of its absence (docs/advanced-sensor-plan.md §6.4): the code, the key that tells
/// whether an event is due (§3.3), the message (§3.1), and each rule's outcome.
/// </summary>
public sealed record AdvancedVerdict(int Code, string Key, string Message, IReadOnlyList<AdvancedRuleResult> Results)
{
    public int Failed => Results.Count(r => r.Outcome == AdvancedOutcome.Fail);

    public int Warnings => Results.Count(r => r.Outcome == AdvancedOutcome.Warn);

    public bool Success => Code <= 399;

    /// <summary>The state a person reads: ok, warning or failing.</summary>
    public string State => Code switch
    {
        PNMTStatusCodes.ADVANCED_OK => "ok",
        PNMTStatusCodes.ADVANCED_WARNING => "warning",
        _ => "failing",
    };

    /// <summary>A report PNMT couldn't evaluate (§7.3, <i>Errors</i>): 496 <c>internal</c>.</summary>
    public static AdvancedVerdict Internal() =>
        new(PNMTStatusCodes.ADVANCED_INVALID, $"{PNMTStatusCodes.ADVANCED_INVALID}|{AdvancedReportErrors.Key(AdvancedReportError.Internal)}",
            "PNMT couldn't evaluate this report", []);

    /// <summary>
    /// The verdict as of <paramref name="at"/>, pure: no database and no clock. Null without a report (the state stays
    /// <i>No data</i>). With an interval and <paramref name="checkStale"/>, a report older than interval plus grace at
    /// <paramref name="at"/> is 460. The rules are evaluated in any case, so the outcomes are there even while stale.
    /// </summary>
    /// <param name="withValues">False for a sensor whose data names people (§13): the message names the rules, not the values.</param>
    public static AdvancedVerdict? Evaluate(IReadOnlyList<AdvancedRuleSpec> rules, AdvancedReportInput? report, JsonElement? previous, DateTime at,
        DateTime? lastReportAt, TimeSpan interval, TimeSpan grace, bool checkStale = true, bool withValues = true)
    {
        if (report is null)
        {
            return null;
        }
        var results = report.Valid && report.Root is { } root
            ? rules.Select(r => AdvancedRules.Evaluate(r, root, previous, at)).ToList()
            : [];
        if (checkStale && interval > TimeSpan.Zero && lastReportAt is { } last && at - last > interval + grace)
        {
            var expected = $"expected every {AdvancedValues.Duration(interval)}" + (grace > TimeSpan.Zero ? $", {AdvancedValues.Duration(grace)} grace" : "");
            return new AdvancedVerdict(PNMTStatusCodes.HEARTBEAT_MISSING, $"{PNMTStatusCodes.HEARTBEAT_MISSING}",
                $"No report since {AdvancedValues.Absolute(last)} ({expected})", results);
        }
        if (!report.Valid)
        {
            var kind = report.ErrorKind ?? AdvancedReportError.NotJson;
            return new AdvancedVerdict(PNMTStatusCodes.ADVANCED_INVALID, $"{PNMTStatusCodes.ADVANCED_INVALID}|{AdvancedReportErrors.Key(kind)}",
                AdvancedValues.Cut(report.Error ?? "The report couldn't be read", AdvancedLimits.MaxMessage), results);
        }
        var failed = results.Where(r => r.Outcome == AdvancedOutcome.Fail).ToList();
        var warned = results.Where(r => r.Outcome == AdvancedOutcome.Warn).ToList();
        if (failed.Count > 0)
        {
            return new AdvancedVerdict(PNMTStatusCodes.ADVANCED_FAILED, KeyOf(PNMTStatusCodes.ADVANCED_FAILED, failed),
                MessageOf(failed.Count == 1 ? "1 rule fails: " : $"{failed.Count} rules fail: ", failed, withValues), results);
        }
        if (warned.Count > 0)
        {
            return new AdvancedVerdict(PNMTStatusCodes.ADVANCED_WARNING, KeyOf(PNMTStatusCodes.ADVANCED_WARNING, warned),
                MessageOf(warned.Count == 1 ? "Warning: " : $"{warned.Count} warnings: ", warned, withValues), results);
        }
        return new AdvancedVerdict(PNMTStatusCodes.ADVANCED_OK, $"{PNMTStatusCodes.ADVANCED_OK}",
            results.Any(r => r.Outcome != AdvancedOutcome.Off) ? "All rules hold" : "Report received", results);
    }

    private static string KeyOf(int code, IEnumerable<AdvancedRuleResult> rules) =>
        AdvancedValues.Cut($"{code}|{string.Join(",", rules.Select(r => r.RuleId.ToString("N")))}", AdvancedLimits.MaxVerdictKey);

    /// <summary>The rules in their order, at most 1000 characters, then <c>… and 3 more</c> (§3.1).</summary>
    private static string MessageOf(string prefix, IReadOnlyList<AdvancedRuleResult> rules, bool withValues)
    {
        var sb = new StringBuilder(prefix);
        for (var i = 0; i < rules.Count; i++)
        {
            var part = (i > 0 ? "; " : "") + Part(rules[i], withValues);
            var rest = rules.Count - i - 1;
            var room = AdvancedLimits.MaxMessage - (rest > 0 ? 20 : 0);
            if (sb.Length + part.Length > room && i > 0)
            {
                sb.Append($" … and {rules.Count - i} more");
                return sb.ToString();
            }
            sb.Append(part);
        }
        return AdvancedValues.Cut(sb.ToString(), AdvancedLimits.MaxMessage);
    }

    /// <summary><c>Disk space (disk.free_gb = 3.2, must be at least 5)</c>: the name, the values (cut at 60) and why.</summary>
    public static string Part(AdvancedRuleResult r, bool withValues = true)
    {
        var name = AdvancedValues.Cut(AdvancedValues.Clean(r.Name), 80);
        var bits = new List<string>();
        if (r.Reason == AdvancedReason.Missing)
        {
            bits.Add($"{AdvancedValues.Cut(r.Path, 80)} missing");
        }
        else if (r.Reason == AdvancedReason.TooManyMatches)
        {
            bits.Add($"more than {AdvancedLimits.MaxMatches} matches");
        }
        else if (!withValues)
        {
            bits.Add(r.Detail is { Length: > 0 } detail ? $"{AdvancedValues.Cut(r.Path, 80)}: {detail}" : AdvancedValues.Cut(r.Path, 80));
        }
        else
        {
            bits.AddRange(r.Actual.Select(a => $"{AdvancedValues.Cut(a.Path, 80)} = {AdvancedValues.Cut(a.Value, AdvancedLimits.MaxMessageValueLength)}"));
            if (r.MatchCount > r.Actual.Count && r.Actual.Count > 1)
            {
                bits.Add($"{r.MatchCount - r.Actual.Count} more");
            }
            if (r.Detail is { Length: > 0 } detail)
            {
                bits.Add(detail);
            }
        }
        return bits.Count == 0 ? name : $"{name} ({string.Join(", ", bits)})";
    }
}

/// <summary>One rule against one report (§6.2, §6.3), and what it says in words.</summary>
public static class AdvancedRules
{
    private enum Hold { Yes, No, Warn, WrongKind, Timeout }

    private readonly record struct MatchOutcome(AdvancedMatch Match, Hold Hold, string Shown);

    public static bool IsAge(AdvancedOperator op) => op is AdvancedOperator.OlderThan or AdvancedOperator.NewerThan;

    public static bool IsCount(AdvancedOperator op) => op is AdvancedOperator.CountGe or AdvancedOperator.CountLe or AdvancedOperator.CountEq;

    public static bool IsChange(AdvancedOperator op) =>
        op is AdvancedOperator.Changed or AdvancedOperator.Unchanged or AdvancedOperator.Increased or AdvancedOperator.Decreased or AdvancedOperator.NotDecreased;

    public static bool IsNumeric(AdvancedOperator op) =>
        op is AdvancedOperator.Lt or AdvancedOperator.Le or AdvancedOperator.Gt or AdvancedOperator.Ge or AdvancedOperator.Between or AdvancedOperator.Outside;

    public static bool TakesOperand(AdvancedOperator op) => op switch
    {
        AdvancedOperator.IsTrue or AdvancedOperator.IsFalse or AdvancedOperator.Exists or AdvancedOperator.Absent or AdvancedOperator.IsNull
            or AdvancedOperator.NotNull or AdvancedOperator.Empty or AdvancedOperator.NotEmpty or AdvancedOperator.Healthy => false,
        _ when IsChange(op) => false,
        _ => true,
    };

    public static bool TakesOperand2(AdvancedOperator op) => op is AdvancedOperator.Between or AdvancedOperator.Outside;

    /// <summary>Whether ignore case means anything for the operator.</summary>
    public static bool UsesCase(AdvancedOperator op) => op is AdvancedOperator.Eq or AdvancedOperator.Ne or AdvancedOperator.Contains
        or AdvancedOperator.NotContains or AdvancedOperator.StartsWith or AdvancedOperator.EndsWith or AdvancedOperator.Matches
        or AdvancedOperator.NotMatches or AdvancedOperator.OneOf or AdvancedOperator.NotOneOf;

    /// <summary>
    /// The operand checked when a rule is saved (§6.3): null when it fits the operator, else the message for the field.
    /// <paramref name="field"/> is <c>operand</c> or <c>operand2</c>.
    /// </summary>
    public static (string Field, string Message)? ValidateOperand(AdvancedOperator op, string? operand, string? operand2)
    {
        if (operand is { Length: > AdvancedLimits.MaxOperand })
        {
            return ("operand", $"At most {AdvancedLimits.MaxOperand} characters.");
        }
        if (operand2 is { Length: > AdvancedLimits.MaxOperand2 })
        {
            return ("operand2", $"At most {AdvancedLimits.MaxOperand2} characters.");
        }
        switch (op)
        {
            case AdvancedOperator.Eq or AdvancedOperator.Ne:
                return operand is null ? ("operand", "A value to compare with: a number, true, false, null or text.") : null;
            case AdvancedOperator.Lt or AdvancedOperator.Le or AdvancedOperator.Gt or AdvancedOperator.Ge:
                return AdvancedNumber.TryParse(operand, out _) ? null : ("operand", "A number, such as 5 or 0.25.");
            case AdvancedOperator.Between or AdvancedOperator.Outside:
                if (!AdvancedNumber.TryParse(operand, out var low))
                {
                    return ("operand", "A number, such as 5 or 0.25.");
                }
                if (!AdvancedNumber.TryParse(operand2, out var high))
                {
                    return ("operand2", "A number, such as 5 or 0.25.");
                }
                return low <= high ? null : ("operand2", "At least the first number.");
            case AdvancedOperator.Contains or AdvancedOperator.NotContains or AdvancedOperator.StartsWith or AdvancedOperator.EndsWith:
                return string.IsNullOrEmpty(operand) ? ("operand", "The text to look for.") : null;
            case AdvancedOperator.Matches or AdvancedOperator.NotMatches:
                if (string.IsNullOrEmpty(operand))
                {
                    return ("operand", "A regular expression (.NET), such as ^v2\\.");
                }
                try
                {
                    _ = new Regex(operand, RegexOptions.CultureInvariant, AdvancedLimits.PatternTimeout);
                    return null;
                }
                catch (ArgumentException e)
                {
                    return ("operand", $"Not a valid pattern: {e.Message}");
                }
            case AdvancedOperator.OneOf or AdvancedOperator.NotOneOf:
                return Lines(operand).Count == 0 ? ("operand", "The values, one per line.") : null;
            case AdvancedOperator.OlderThan or AdvancedOperator.NewerThan:
                return AdvancedValues.TryDuration(operand, out _) ? null : ("operand", "A duration: 90s, 15m, 26h or 7d (or ISO 8601, such as PT26H).");
            case AdvancedOperator.CountGe or AdvancedOperator.CountLe or AdvancedOperator.CountEq:
                return int.TryParse(operand?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _) ? null : ("operand", "A whole number, 0 or more.");
            default:
                return null;
        }
    }

    private static List<string> Lines(string? operand) =>
        (operand ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    // --- words -----------------------------------------------------------------------------------------------------------

    /// <summary>The rule in words: <c>disk.free_gb ≥ 5</c>, <c>none of jobs[*].state = "failed"</c>.</summary>
    public static string Describe(AdvancedRuleSpec rule)
    {
        var wildcard = AdvancedPath.TryParse(rule.Path, out var path, out _) && path.HasWildcard;
        var text = path?.Text ?? rule.Path;
        var subject = wildcard && !IsCount(rule.Operator) && rule.Operator is not (AdvancedOperator.Exists or AdvancedOperator.Absent)
            ? rule.Quantifier switch
            {
                AdvancedQuantifier.Any => $"any of {text}",
                AdvancedQuantifier.None => $"none of {text}",
                _ => $"every {text}",
            }
            : text;
        var o = rule.Operand;
        var predicate = rule.Operator switch
        {
            AdvancedOperator.Eq => $"= {AdvancedValues.Operand(o)}",
            AdvancedOperator.Ne => $"≠ {AdvancedValues.Operand(o)}",
            AdvancedOperator.Lt => $"< {o?.Trim()}",
            AdvancedOperator.Le => $"≤ {o?.Trim()}",
            AdvancedOperator.Gt => $"> {o?.Trim()}",
            AdvancedOperator.Ge => $"≥ {o?.Trim()}",
            AdvancedOperator.Between => $"between {o?.Trim()} and {rule.Operand2?.Trim()}",
            AdvancedOperator.Outside => $"outside {o?.Trim()} to {rule.Operand2?.Trim()}",
            AdvancedOperator.Contains => $"contains {AdvancedValues.Operand(o)}",
            AdvancedOperator.NotContains => $"doesn't contain {AdvancedValues.Operand(o)}",
            AdvancedOperator.StartsWith => $"starts with {AdvancedValues.Operand(o)}",
            AdvancedOperator.EndsWith => $"ends with {AdvancedValues.Operand(o)}",
            AdvancedOperator.Matches => $"matches /{AdvancedValues.Cut(o ?? "", 80)}/",
            AdvancedOperator.NotMatches => $"doesn't match /{AdvancedValues.Cut(o ?? "", 80)}/",
            AdvancedOperator.OneOf => $"is one of {List(o)}",
            AdvancedOperator.NotOneOf => $"is none of {List(o)}",
            AdvancedOperator.IsTrue => "is true",
            AdvancedOperator.IsFalse => "is false",
            AdvancedOperator.Exists => "exists",
            AdvancedOperator.Absent => "is absent",
            AdvancedOperator.IsNull => "is null",
            AdvancedOperator.NotNull => "isn't null",
            AdvancedOperator.Empty => "is empty",
            AdvancedOperator.NotEmpty => "isn't empty",
            AdvancedOperator.Healthy => "is healthy",
            AdvancedOperator.OlderThan => $"older than {DurationText(o)}",
            AdvancedOperator.NewerThan => $"newer than {DurationText(o)}",
            AdvancedOperator.CountGe => $"count ≥ {o?.Trim()}",
            AdvancedOperator.CountLe => $"count ≤ {o?.Trim()}",
            AdvancedOperator.CountEq => $"count = {o?.Trim()}",
            AdvancedOperator.Changed => "changed",
            AdvancedOperator.Unchanged => "unchanged",
            AdvancedOperator.Increased => "increased",
            AdvancedOperator.Decreased => "decreased",
            AdvancedOperator.NotDecreased => "didn't decrease",
            _ => rule.Operator.ToString(),
        };
        return $"{subject} {predicate}" + (rule.IgnoreCase && UsesCase(rule.Operator) ? " (ignoring case)" : "");
    }

    private static string List(string? operand)
    {
        var lines = Lines(operand);
        var shown = string.Join(", ", lines.Take(5).Select(AdvancedValues.Operand));
        return lines.Count > 5 ? $"{shown} and {lines.Count - 5} more" : shown;
    }

    private static string DurationText(string? operand) =>
        AdvancedValues.TryDuration(operand, out var d) ? AdvancedValues.Duration(d) : operand ?? "";

    /// <summary>What a value must be, for a message: <c>must be at least 5</c>.</summary>
    private static string Expectation(AdvancedRuleSpec rule)
    {
        var o = rule.Operand;
        return rule.Operator switch
        {
            AdvancedOperator.Eq => $"must be {AdvancedValues.Operand(o)}",
            AdvancedOperator.Ne => $"must not be {AdvancedValues.Operand(o)}",
            AdvancedOperator.Lt => $"must be less than {o?.Trim()}",
            AdvancedOperator.Le => $"must be at most {o?.Trim()}",
            AdvancedOperator.Gt => $"must be more than {o?.Trim()}",
            AdvancedOperator.Ge => $"must be at least {o?.Trim()}",
            AdvancedOperator.Between => $"must be between {o?.Trim()} and {rule.Operand2?.Trim()}",
            AdvancedOperator.Outside => $"must be outside {o?.Trim()} to {rule.Operand2?.Trim()}",
            AdvancedOperator.Contains => $"must contain {AdvancedValues.Operand(o)}",
            AdvancedOperator.NotContains => $"must not contain {AdvancedValues.Operand(o)}",
            AdvancedOperator.StartsWith => $"must start with {AdvancedValues.Operand(o)}",
            AdvancedOperator.EndsWith => $"must end with {AdvancedValues.Operand(o)}",
            AdvancedOperator.Matches => $"must match /{AdvancedValues.Cut(o ?? "", 40)}/",
            AdvancedOperator.NotMatches => $"must not match /{AdvancedValues.Cut(o ?? "", 40)}/",
            AdvancedOperator.OneOf => $"must be one of {List(o)}",
            AdvancedOperator.NotOneOf => $"must not be one of {List(o)}",
            AdvancedOperator.IsTrue => "must be true",
            AdvancedOperator.IsFalse => "must be false",
            AdvancedOperator.Exists => "missing",
            AdvancedOperator.Absent => "must be absent",
            AdvancedOperator.IsNull => "must be null",
            AdvancedOperator.NotNull => "must not be null",
            AdvancedOperator.Empty => "must be empty",
            AdvancedOperator.NotEmpty => "must not be empty",
            AdvancedOperator.Healthy => "not healthy",
            AdvancedOperator.OlderThan => $"must be older than {DurationText(o)}",
            AdvancedOperator.NewerThan => $"must be newer than {DurationText(o)}",
            AdvancedOperator.CountGe => $"must be at least {o?.Trim()}",
            AdvancedOperator.CountLe => $"must be at most {o?.Trim()}",
            AdvancedOperator.CountEq => $"must be {o?.Trim()}",
            AdvancedOperator.Changed => "must change",
            AdvancedOperator.Unchanged => "must not change",
            AdvancedOperator.Increased => "must increase",
            AdvancedOperator.Decreased => "must decrease",
            AdvancedOperator.NotDecreased => "must not decrease",
            _ => "",
        };
    }

    // --- evaluating ------------------------------------------------------------------------------------------------------

    /// <summary>One rule's outcome for <paramref name="root"/>, as of <paramref name="at"/> (§6.2, §6.3).</summary>
    public static AdvancedRuleResult Evaluate(AdvancedRuleSpec rule, JsonElement root, JsonElement? previous, DateTime at)
    {
        var condition = Describe(rule);
        AdvancedRuleResult Result(AdvancedOutcome outcome, AdvancedReason? reason, string? detail, IEnumerable<AdvancedActual> actual,
            IReadOnlyList<string> marked, int count, bool assumedUtc = false) =>
            new(rule.Id, rule.Name, rule.Path, condition, rule.Severity, outcome, reason, detail, actual.Take(AdvancedLimits.MaxActual).ToList(),
                marked, count, assumedUtc);
        var notHeld = rule.Severity == AdvancedSeverity.Warn ? AdvancedOutcome.Warn : AdvancedOutcome.Fail;

        if (!rule.Enabled)
        {
            return Result(AdvancedOutcome.Off, null, null, [], [], 0);
        }
        if (!AdvancedPath.TryParse(rule.Path, out var path, out var pathError))
        {
            return Result(notHeld, AdvancedReason.WrongKind, $"invalid path: {pathError}", [], [], 0);
        }
        var matches = path.Select(root);
        if (matches.Count > AdvancedLimits.MaxMatches)
        {
            return Result(notHeld, AdvancedReason.TooManyMatches, $"more than {AdvancedLimits.MaxMatches} matches", [], [], matches.Count);
        }
        var marked = matches.Take(AdvancedLimits.MaxMarked).Select(m => m.Path).ToList();
        var count = matches.Count;

        switch (rule.Operator)
        {
            case AdvancedOperator.Exists:
                return count > 0
                    ? Result(AdvancedOutcome.Pass, null, null, matches.Select(m => Actual(m)), marked, count)
                    : Result(notHeld, null, "missing", [], marked, 0);
            case AdvancedOperator.Absent:
                return count == 0
                    ? Result(AdvancedOutcome.Pass, null, null, [], marked, 0)
                    : Result(notHeld, null, Expectation(rule), matches.Select(m => Actual(m)), marked, count);
            case var op when IsCount(op) && path.HasWildcard:
            {
                var limit = long.Parse(rule.Operand!.Trim(), CultureInfo.InvariantCulture);
                var holds = Compare(op, count, limit);
                var shown = new AdvancedActual(path.Text, $"{count} match{(count == 1 ? "" : "es")}");
                return holds
                    ? Result(AdvancedOutcome.Pass, null, null, [shown], marked, count)
                    : Result(notHeld, null, Expectation(rule), [shown], marked, count);
            }
        }

        if (count == 0)
        {
            var outcome = rule.WhenMissing switch
            {
                AdvancedWhenMissing.Pass => AdvancedOutcome.Pass,
                AdvancedWhenMissing.Warn => AdvancedOutcome.Warn,
                _ => notHeld,
            };
            return Result(outcome, AdvancedReason.Missing, "missing", [], marked, 0);
        }

        var assumedUtc = false;
        var outcomes = new List<MatchOutcome>(count);
        foreach (var match in matches)
        {
            var (hold, shown, assumed) = Test(rule, path, match, previous, at);
            assumedUtc |= assumed;
            outcomes.Add(new MatchOutcome(match, hold, shown));
        }

        var single = !path.HasWildcard;
        var quantifier = single ? AdvancedQuantifier.All : rule.Quantifier;
        static bool IsError(Hold h) => h is Hold.WrongKind or Hold.Timeout;
        static AdvancedReason? ReasonOf(IEnumerable<MatchOutcome> deciding)
        {
            var list = deciding.ToList();
            return list.Count > 0 && list.All(o => IsError(o.Hold))
                ? list[0].Hold == Hold.Timeout ? AdvancedReason.Timeout : AdvancedReason.WrongKind
                : null;
        }
        string ErrorDetail(AdvancedReason? reason) => reason switch
        {
            AdvancedReason.Timeout => "the pattern took too long",
            AdvancedReason.WrongKind => WrongKindText(rule.Operator),
            _ => Expectation(rule),
        };
        IEnumerable<AdvancedActual> Shown(IEnumerable<MatchOutcome> list) => list.Select(o => new AdvancedActual(o.Match.Path, o.Shown));

        switch (quantifier)
        {
            case AdvancedQuantifier.Any:
            {
                var yes = outcomes.Where(o => o.Hold == Hold.Yes).ToList();
                if (yes.Count > 0)
                {
                    return Result(AdvancedOutcome.Pass, null, null, Shown(yes.Take(1)), marked, count, assumedUtc);
                }
                var warn = outcomes.Where(o => o.Hold == Hold.Warn).ToList();
                if (warn.Count > 0)
                {
                    return Result(AdvancedOutcome.Warn, null, "a warning", Shown(warn), marked, count, assumedUtc);
                }
                var reason = ReasonOf(outcomes);
                return Result(notHeld, reason, ErrorDetail(reason), Shown(outcomes), marked, count, assumedUtc);
            }
            case AdvancedQuantifier.None:
            {
                var yes = outcomes.Where(o => o.Hold == Hold.Yes).ToList();
                if (yes.Count > 0)
                {
                    // "none of jobs[*].state is failed": the matches that are say it all.
                    return Result(notHeld, null, null, Shown(yes), marked, count, assumedUtc);
                }
                var errors = outcomes.Where(o => IsError(o.Hold)).ToList();
                if (errors.Count > 0)
                {
                    var reason = ReasonOf(errors);
                    return Result(notHeld, reason, ErrorDetail(reason), Shown(errors), marked, count, assumedUtc);
                }
                return Result(AdvancedOutcome.Pass, null, null, Shown(outcomes), marked, count, assumedUtc);
            }
            default:
            {
                var bad = outcomes.Where(o => o.Hold is Hold.No || IsError(o.Hold)).ToList();
                if (bad.Count > 0)
                {
                    var reason = ReasonOf(bad);
                    return Result(notHeld, reason, ErrorDetail(reason), Shown(bad), marked, count, assumedUtc);
                }
                var warn = outcomes.Where(o => o.Hold == Hold.Warn).ToList();
                if (warn.Count > 0)
                {
                    return Result(AdvancedOutcome.Warn, null, "a warning", Shown(warn), marked, count, assumedUtc);
                }
                return Result(AdvancedOutcome.Pass, null, null, Shown(outcomes), marked, count, assumedUtc);
            }
        }
    }

    private static string WrongKindText(AdvancedOperator op) => op switch
    {
        _ when IsNumeric(op) || op is AdvancedOperator.Increased or AdvancedOperator.Decreased or AdvancedOperator.NotDecreased => "not a number",
        _ when IsAge(op) => "not a time",
        _ when IsCount(op) => "not a list, an object or text",
        _ => "can't be compared",
    };

    private static AdvancedActual Actual(AdvancedMatch m) => new(m.Path, AdvancedValues.Display(m.Value));

    private static bool Compare(AdvancedOperator op, long value, long limit) => op switch
    {
        AdvancedOperator.CountGe => value >= limit,
        AdvancedOperator.CountLe => value <= limit,
        _ => value == limit,
    };

    private static (Hold Hold, string Shown, bool AssumedUtc) Test(AdvancedRuleSpec rule, AdvancedPath path, AdvancedMatch match, JsonElement? previous, DateTime at)
    {
        var v = match.Value;
        var shown = AdvancedValues.Display(v);
        static Hold Of(bool holds) => holds ? Hold.Yes : Hold.No;
        var comparison = rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        switch (rule.Operator)
        {
            case AdvancedOperator.Eq or AdvancedOperator.Ne:
            {
                var equal = EqualsOperand(v, rule.Operand ?? "", comparison);
                if (equal is null)
                {
                    return (Hold.WrongKind, shown, false);
                }
                return (Of(rule.Operator == AdvancedOperator.Eq ? equal.Value : !equal.Value), shown, false);
            }
            case AdvancedOperator.Lt or AdvancedOperator.Le or AdvancedOperator.Gt or AdvancedOperator.Ge:
            {
                if (!AdvancedValues.TryNumber(v, out var n) || !AdvancedNumber.TryParse(rule.Operand, out var limit))
                {
                    return (Hold.WrongKind, shown, false);
                }
                return (Of(rule.Operator switch
                {
                    AdvancedOperator.Lt => n < limit,
                    AdvancedOperator.Le => n <= limit,
                    AdvancedOperator.Gt => n > limit,
                    _ => n >= limit,
                }), shown, false);
            }
            case AdvancedOperator.Between or AdvancedOperator.Outside:
            {
                if (!AdvancedValues.TryNumber(v, out var n) || !AdvancedNumber.TryParse(rule.Operand, out var low)
                    || !AdvancedNumber.TryParse(rule.Operand2, out var high))
                {
                    return (Hold.WrongKind, shown, false);
                }
                var inside = n >= low && n <= high;
                return (Of(rule.Operator == AdvancedOperator.Between ? inside : !inside), shown, false);
            }
            case AdvancedOperator.Contains or AdvancedOperator.NotContains:
            {
                bool found;
                if (v.ValueKind == JsonValueKind.Array)
                {
                    // A list contains a value when one of its items is that value, as text.
                    found = v.EnumerateArray().Any(item => AdvancedValues.TryText(item, out var t) && string.Equals(t, rule.Operand, comparison));
                }
                else if (AdvancedValues.TryText(v, out var text))
                {
                    found = text.Contains(rule.Operand ?? "", comparison);
                }
                else
                {
                    return (Hold.WrongKind, shown, false);
                }
                return (Of(rule.Operator == AdvancedOperator.Contains ? found : !found), shown, false);
            }
            case AdvancedOperator.StartsWith or AdvancedOperator.EndsWith:
            {
                if (!AdvancedValues.TryText(v, out var text))
                {
                    return (Hold.WrongKind, shown, false);
                }
                return (Of(rule.Operator == AdvancedOperator.StartsWith
                    ? text.StartsWith(rule.Operand ?? "", comparison)
                    : text.EndsWith(rule.Operand ?? "", comparison)), shown, false);
            }
            case AdvancedOperator.Matches or AdvancedOperator.NotMatches:
            {
                if (!AdvancedValues.TryText(v, out var text))
                {
                    return (Hold.WrongKind, shown, false);
                }
                var regex = Pattern(rule.Operand ?? "", rule.IgnoreCase);
                if (regex is null)
                {
                    return (Hold.WrongKind, shown, false);
                }
                try
                {
                    var found = regex.IsMatch(text);
                    return (Of(rule.Operator == AdvancedOperator.Matches ? found : !found), shown, false);
                }
                catch (RegexMatchTimeoutException)
                {
                    return (Hold.Timeout, shown, false);
                }
            }
            case AdvancedOperator.OneOf or AdvancedOperator.NotOneOf:
            {
                if (v.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                {
                    return (Hold.WrongKind, shown, false);
                }
                var found = Lines(rule.Operand).Any(line => EqualsOperand(v, line, comparison) == true);
                return (Of(rule.Operator == AdvancedOperator.OneOf ? found : !found), shown, false);
            }
            case AdvancedOperator.IsTrue:
                return (Of(v.ValueKind == JsonValueKind.True), shown, false);
            case AdvancedOperator.IsFalse:
                return (Of(v.ValueKind == JsonValueKind.False), shown, false);
            case AdvancedOperator.IsNull:
                return (Of(v.ValueKind == JsonValueKind.Null), shown, false);
            case AdvancedOperator.NotNull:
                return (Of(v.ValueKind != JsonValueKind.Null), shown, false);
            case AdvancedOperator.Empty or AdvancedOperator.NotEmpty:
            {
                var empty = v.ValueKind switch
                {
                    JsonValueKind.Null => true,
                    JsonValueKind.String => v.GetString()!.Length == 0,
                    JsonValueKind.Array => v.GetArrayLength() == 0,
                    JsonValueKind.Object => !v.EnumerateObject().Any(),
                    _ => false,
                };
                return (Of(rule.Operator == AdvancedOperator.Empty ? empty : !empty), shown, false);
            }
            case AdvancedOperator.Healthy:
                return (AdvancedValues.Health(v) switch
                {
                    AdvancedOutcome.Pass => Hold.Yes,
                    AdvancedOutcome.Warn => Hold.Warn,
                    _ => Hold.No,
                }, shown, false);
            case AdvancedOperator.OlderThan or AdvancedOperator.NewerThan:
            {
                if (!AdvancedValues.TryTime(v, out var time, out var assumedUtc) || !AdvancedValues.TryDuration(rule.Operand, out var duration))
                {
                    return (Hold.WrongKind, shown, false);
                }
                var age = at - time;
                var when = AdvancedValues.Absolute(time) + (assumedUtc ? " (UTC assumed)" : "");
                return (Of(rule.Operator == AdvancedOperator.OlderThan ? age > duration : age < duration), when, assumedUtc);
            }
            case AdvancedOperator.CountGe or AdvancedOperator.CountLe or AdvancedOperator.CountEq:
            {
                long n;
                switch (v.ValueKind)
                {
                    case JsonValueKind.Array:
                        n = v.GetArrayLength();
                        break;
                    case JsonValueKind.Object:
                        n = v.EnumerateObject().Count();
                        break;
                    case JsonValueKind.String:
                        n = v.GetString()!.Length;
                        break;
                    default:
                        return (Hold.WrongKind, shown, false);
                }
                var limit = long.Parse(rule.Operand!.Trim(), CultureInfo.InvariantCulture);
                return (Of(Compare(rule.Operator, n, limit)), $"{shown}, count {n}", false);
            }
            case var op when IsChange(op):
            {
                JsonElement before = default;
                var had = previous is { } p && (path.HasWildcard
                    ? AdvancedPath.TryParse(match.Path, out var concrete, out _) && concrete.TryGet(p, out before)
                    : path.TryGet(p, out before));
                if (!had)
                {
                    // The first report, or a value that is new: nothing to compare with.
                    return (Hold.Yes, shown, false);
                }
                var was = $"{shown} (was {AdvancedValues.Display(before, 60)})";
                if (op is AdvancedOperator.Changed or AdvancedOperator.Unchanged)
                {
                    var same = JsonElement.DeepEquals(v, before);
                    return (Of(op == AdvancedOperator.Changed ? !same : same), was, false);
                }
                if (!AdvancedValues.TryNumber(v, out var now) || !AdvancedValues.TryNumber(before, out var then))
                {
                    return (Hold.WrongKind, was, false);
                }
                return (Of(op switch
                {
                    AdvancedOperator.Increased => now > then,
                    AdvancedOperator.Decreased => now < then,
                    _ => now >= then,
                }), was, false);
            }
            default:
                return (Hold.WrongKind, shown, false);
        }
    }

    /// <summary>
    /// Equality with an operand (§6.3): a number as a number (5 equals 5.0), true and false as booleans, null as null,
    /// anything else as text. Null: the kinds can't be compared (a list, an object).
    /// </summary>
    private static bool? EqualsOperand(JsonElement v, string operand, StringComparison comparison)
    {
        var o = operand.Trim();
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                if (AdvancedNumber.TryParse(o, out var expected) && AdvancedValues.TryNumber(v, out var actual))
                {
                    return actual.CompareTo(expected) == 0;
                }
                return string.Equals(v.GetRawText(), o, comparison);
            case JsonValueKind.True or JsonValueKind.False:
                return string.Equals(v.ValueKind == JsonValueKind.True ? "true" : "false", o, StringComparison.OrdinalIgnoreCase);
            case JsonValueKind.Null:
                return string.Equals(o, "null", StringComparison.OrdinalIgnoreCase);
            case JsonValueKind.String:
                return string.Equals(v.GetString(), operand, comparison) || (operand != o && string.Equals(v.GetString(), o, comparison));
            default:
                return null;
        }
    }

    private static readonly ConcurrentDictionary<(string, bool), Regex?> Patterns = new();

    private static Regex? Pattern(string pattern, bool ignoreCase)
    {
        if (Patterns.Count > 1000)
        {
            Patterns.Clear();
        }
        return Patterns.GetOrAdd((pattern, ignoreCase), key =>
        {
            try
            {
                return new Regex(key.Item1, RegexOptions.CultureInvariant | (key.Item2 ? RegexOptions.IgnoreCase : RegexOptions.None),
                    AdvancedLimits.PatternTimeout);
            }
            catch (ArgumentException)
            {
                return null;
            }
        });
    }
}
