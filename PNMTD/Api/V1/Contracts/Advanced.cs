using System.Text.Json;
using PNMTD.Advanced;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// An ADVANCED sensor's setting (docs/advanced-sensor-plan.md §8.2), in <c>SensorRequest.Advanced</c>. Null
/// <see cref="NamesPeople"/> keeps the setting (off on a new sensor). Its rules and fields have endpoints of their own.
/// </summary>
public sealed class AdvancedSensorRequest
{
    /// <summary>The data names people: the reports need Monitoring: Details.</summary>
    public bool? NamesPeople { get; init; }

    /// <summary>On creation only: the starter rule "The app's status" (status healthy, when missing: pass; §6.5). On by default.</summary>
    public bool AddStatusRule { get; init; } = true;
}

/// <summary>The verdict the evaluator wrote last: its code and message, and how many rules failed and warned.</summary>
public sealed record AdvancedVerdictItem(int Code, string? Message, int Failed, int Warnings, DateTime? EvaluatedAt);

/// <summary>
/// <c>SensorDetail.Advanced</c>, for <i>Monitoring: View</i> (§8.2): the setting, the receipt, the verdict, the rules with their
/// latest outcomes, the fields with their latest values, and the latest report in brief. No body: the Data tab loads that.
/// For a sensor whose data names people and a caller without Details, <see cref="CanSeeData"/> is false and the values,
/// the app's status and message are left out.
/// </summary>
public sealed record AdvancedSensorDetail(
    bool NamesPeople,
    bool CanSeeData,
    DateTime? LastReportAt,
    DateTime? LastValidReportAt,
    string? LastSource,
    bool Stale,
    DateTime? StaleAt,
    AdvancedVerdictItem? Verdict,
    IReadOnlyList<AdvancedRuleItem> Rules,
    IReadOnlyList<AdvancedFieldItem> Fields,
    AdvancedReportSummary? Latest,
    int Pending,
    int ReportCount);

/// <summary>A rule to create or change (§5.2). The operand is text; the operator decides what it must be (§6.3).</summary>
public sealed class AdvancedRuleRequest
{
    /// <summary>A draft's id in a test (§6.6), so its outcome can be matched; ignored when saving.</summary>
    public Guid? Id { get; init; }

    public string? Name { get; init; }

    public bool Enabled { get; init; } = true;

    public string? Note { get; init; }

    public string? Path { get; init; }

    public AdvancedOperator Operator { get; init; } = AdvancedOperator.Eq;

    public string? Operand { get; init; }

    public string? Operand2 { get; init; }

    public bool IgnoreCase { get; init; }

    public AdvancedQuantifier Quantifier { get; init; } = AdvancedQuantifier.All;

    public AdvancedWhenMissing WhenMissing { get; init; } = AdvancedWhenMissing.Fail;

    public AdvancedSeverity Severity { get; init; } = AdvancedSeverity.Fail;
}

/// <summary>
/// A rule with its condition in words (<c>disk.free_gb ≥ 5</c>), its latest outcome and since when it fails. Without
/// <c>CanSeeData</c>, the outcome carries no values.
/// </summary>
public sealed record AdvancedRuleItem(
    Guid Id,
    int Order,
    string Name,
    bool Enabled,
    string? Note,
    string Path,
    AdvancedOperator Operator,
    string? Operand,
    string? Operand2,
    bool IgnoreCase,
    AdvancedQuantifier Quantifier,
    AdvancedWhenMissing WhenMissing,
    AdvancedSeverity Severity,
    string Condition,
    bool HasWildcard,
    AdvancedRuleResult? Last,
    DateTime? FailingSince);

/// <summary>A field to create or change (§5.3): a path without <c>*</c>, a label, a format and a unit.</summary>
public sealed class AdvancedFieldRequest
{
    public string? Name { get; init; }

    public string? Path { get; init; }

    public AdvancedFieldFormat Format { get; init; } = AdvancedFieldFormat.Auto;

    public string? Unit { get; init; }

    /// <summary>0 to 6; null: as needed.</summary>
    public int? Decimals { get; init; }

    public bool Pinned { get; init; }

    public bool Chart { get; init; }
}

/// <summary>A field with its value in the newest judged report (null when its path matched nothing, or without <c>CanSeeData</c>).</summary>
public sealed record AdvancedFieldItem(
    Guid Id,
    int Order,
    string Name,
    string Path,
    AdvancedFieldFormat Format,
    string? Unit,
    int? Decimals,
    bool Pinned,
    bool Chart,
    JsonElement? Value,
    AdvancedOutcome? Outcome);

/// <summary>The latest report in brief (§8.2): when, how often, from where, how large, whether valid, and the app's own words.</summary>
public sealed record AdvancedReportSummary(
    Guid Id,
    DateTime ReceivedAt,
    DateTime LastReceivedAt,
    int Repeats,
    string? Source,
    int Bytes,
    bool Valid,
    AdvancedReportError? ErrorKind,
    string? Error,
    string? Status,
    string? Message,
    DateTime? MeasuredAt,
    int? ClockSkewSeconds,
    bool Evaluated,
    int? Code);

/// <summary>A row of the history (§10.3): <c>Evaluated</c> is false while the report waits for the evaluator.</summary>
public sealed record AdvancedReportItem(
    Guid Id,
    DateTime ReceivedAt,
    DateTime LastReceivedAt,
    int Repeats,
    string? Source,
    int Bytes,
    bool Valid,
    AdvancedReportError? ErrorKind,
    string? Error,
    string? Status,
    string? Message,
    DateTime? MeasuredAt,
    bool Evaluated,
    int? Code,
    int Failed,
    int Warnings);

/// <summary>A report with its body (the JSON as received, a string) and its outcomes at the time, and the report before it.</summary>
public sealed record AdvancedReportDetail(
    AdvancedReportItem Report,
    string? Body,
    IReadOnlyList<AdvancedRuleResult> Results,
    IReadOnlyList<AdvancedFieldValue> Fields,
    string? EvaluationError,
    Guid? PreviousId);

/// <summary><c>GET …/advanced/reports</c>: newest first.</summary>
public sealed class AdvancedReportQuery : PageQuery;

/// <summary>
/// <c>POST …/advanced/test</c> (§6.6): the report (a stored one by its id, JSON as text, or else the latest), against the
/// saved rules or drafts. Nothing is stored and nothing is notified.
/// </summary>
public sealed class AdvancedTestRequest
{
    public Guid? ReportId { get; init; }

    /// <summary>A report as JSON text, within the limits of a real one.</summary>
    public string? Report { get; init; }

    /// <summary>Drafts instead of the saved rules (the editor's).</summary>
    public List<AdvancedRuleRequest>? Rules { get; init; }
}

/// <summary>Each rule's outcome and the verdict the sensor would have; <c>Error</c> when the pasted report can't be read.</summary>
public sealed record AdvancedTestResult(int? Code, string? State, string? Message, IReadOnlyList<AdvancedRuleResult> Results,
    IReadOnlyList<AdvancedFieldValue> Fields, string? Error, Guid? ReportId);

/// <summary><c>PUT …/rules/order</c> and <c>…/fields/order</c>: every id once, in the new order.</summary>
public sealed class AdvancedOrderRequest
{
    public List<Guid> Ids { get; init; } = [];
}

/// <summary><c>POST …/rules/copy</c> (§8.3): another Advanced sensor's rules and fields, appended or replacing these.</summary>
public sealed class AdvancedCopyRequest
{
    public Guid FromSensorId { get; init; }

    public bool Replace { get; init; }
}

/// <summary>A path in the latest report (§8.2), for the editor's suggestions: its JSON kind and a short preview.</summary>
public sealed record AdvancedPathItem(string Path, string Kind, string? Preview);

/// <summary><c>GET …/advanced/samples</c>: a field and <c>24h</c>, <c>7d</c> or <c>30d</c>.</summary>
public sealed class AdvancedSamplesQuery
{
    public Guid FieldId { get; set; }

    public string Range { get; set; } = "24h";
}

public sealed record AdvancedSamplePoint(DateTime At, double Min, double Max, double Last, int Count);

/// <summary>A rule's number on the same path as a field, drawn as a line on its chart.</summary>
public sealed record AdvancedThreshold(string Rule, AdvancedOperator Operator, double Value, AdvancedSeverity Severity);

/// <summary>A field's chart: per minute for 24 hours, per hour for 7 and 30 days.</summary>
public sealed record AdvancedSamples(Guid FieldId, string Range, DateTime From, DateTime To, int StepSeconds, IReadOnlyList<AdvancedSamplePoint> Points,
    IReadOnlyList<AdvancedThreshold> Thresholds);
