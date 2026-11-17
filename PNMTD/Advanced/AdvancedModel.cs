using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD.Advanced;

/// <summary>A rule's operator (docs/advanced-sensor-plan.md §6.3), stored as its name.</summary>
public enum AdvancedOperator
{
    // Compare
    Eq, Ne, Lt, Le, Gt, Ge, Between, Outside,
    // Text
    Contains, NotContains, StartsWith, EndsWith, Matches, NotMatches, OneOf, NotOneOf,
    // Kind
    IsTrue, IsFalse, Exists, Absent, IsNull, NotNull, Empty, NotEmpty,
    // State
    Healthy,
    // Age
    OlderThan, NewerThan,
    // Count
    CountGe, CountLe, CountEq,
    // Change
    Changed, Unchanged, Increased, Decreased, NotDecreased,
}

/// <summary>How several matches of a path with <c>*</c> count (§6.2).</summary>
public enum AdvancedQuantifier { All, Any, None }

/// <summary>What a rule says when its path matches nothing (§6.2). <c>Fail</c>: the rule doesn't hold, so it fails or warns by its severity.</summary>
public enum AdvancedWhenMissing { Fail, Warn, Pass }

/// <summary>How bad a rule that doesn't hold is (§3.2).</summary>
public enum AdvancedSeverity { Fail, Warn }

/// <summary>A rule's outcome for one report (§3.2).</summary>
public enum AdvancedOutcome { Pass, Warn, Fail, Off }

/// <summary>Why an outcome isn't a plain comparison (§3.2).</summary>
public enum AdvancedReason { Missing, WrongKind, Timeout, TooManyMatches }

/// <summary>How a field's value is shown (§5.3).</summary>
public enum AdvancedFieldFormat { Auto, Number, Percent, Bytes, Duration, DateTime, Text, Boolean, Status }

/// <summary>Why a report couldn't be read (§3, 496); its key in the verdict is <see cref="AdvancedReportErrors.Key"/>.</summary>
public enum AdvancedReportError { NotJson, NotObject, TooLarge, TooDeep, DuplicateKey, Internal }

public static class AdvancedReportErrors
{
    /// <summary>The kind in a verdict key: <c>not-json</c>, <c>duplicate-key</c> (§3.3).</summary>
    public static string Key(AdvancedReportError kind) => kind switch
    {
        AdvancedReportError.NotJson => "not-json",
        AdvancedReportError.NotObject => "not-object",
        AdvancedReportError.TooLarge => "too-large",
        AdvancedReportError.TooDeep => "too-deep",
        AdvancedReportError.DuplicateKey => "duplicate-key",
        _ => "internal",
    };
}

/// <summary>The structural limits (§11): constants, not settings.</summary>
public static class AdvancedLimits
{
    public const int MaxDepth = 32;
    public const int MaxRules = 50;
    public const int MaxFields = 30;
    public const int MaxChartedFields = 10;
    public const int MaxSegments = 16;
    public const int MaxMatches = 1000;
    public const int MaxPath = 500;
    public const int MaxRuleName = 200;
    public const int MaxFieldName = 100;
    public const int MaxOperand = 2000;
    public const int MaxOperand2 = 200;
    public const int MaxNote = 1000;
    public const int MaxUnit = 20;
    public const int MaxMessage = 1000;
    public const int MaxStatus = 50;
    public const int MaxError = 500;
    public const int MaxSource = 100;
    public const int MaxVerdictKey = 2000;

    /// <summary>The values an outcome names (§3.2), and the paths it marks.</summary>
    public const int MaxActual = 5;
    public const int MaxMarked = 20;
    public const int MaxActualLength = 200;

    /// <summary>A value in an event's message (§3.1).</summary>
    public const int MaxMessageValueLength = 60;

    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>The lowest interval besides 0 (§8.1).</summary>
    public const int MinIntervalSeconds = 30;
}

/// <summary>A rule as the verdict needs it: from the table, or a draft from the editor (§6.6).</summary>
public sealed record AdvancedRuleSpec(
    Guid Id,
    string Name,
    bool Enabled,
    string Path,
    AdvancedOperator Operator,
    string? Operand,
    string? Operand2,
    bool IgnoreCase,
    AdvancedQuantifier Quantifier,
    AdvancedWhenMissing WhenMissing,
    AdvancedSeverity Severity,
    string? Note = null);

/// <summary>A value an outcome names: its concrete path and its value as text (strings quoted).</summary>
public sealed record AdvancedActual(string Path, string Value);

/// <summary>
/// One rule's outcome for one report (§3.2), stored in a report's <c>Results</c> with the rule's name and condition as they
/// were: an old report shows what was judged then.
/// </summary>
public sealed record AdvancedRuleResult(
    Guid RuleId,
    string Name,
    string Path,
    string Condition,
    AdvancedSeverity Severity,
    AdvancedOutcome Outcome,
    AdvancedReason? Reason,
    string? Detail,
    IReadOnlyList<AdvancedActual> Actual,
    IReadOnlyList<string> Matches,
    int MatchCount,
    bool AssumedUtc = false);

/// <summary>A charted or shown field's value at a report: the raw JSON, or null when nothing matched.</summary>
public sealed record AdvancedFieldValue(Guid FieldId, JsonElement? Value);

/// <summary>The <c>Results</c> column of a report: the rules' outcomes and the fields' values at the time.</summary>
public sealed record AdvancedResults(IReadOnlyList<AdvancedRuleResult> Rules, IReadOnlyList<AdvancedFieldValue> Fields, string? Error = null);

/// <summary>One rule's latest outcome on the side row (<c>RuleStates</c>): the outcome, since when, and its details.</summary>
public sealed record AdvancedRuleState(AdvancedOutcome Outcome, DateTime Since, AdvancedRuleResult Result);

/// <summary>The jsonb columns' JSON: camelCase, enums as strings, as the API.</summary>
public static class AdvancedJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
