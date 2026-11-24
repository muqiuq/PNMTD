using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PNMTD.Advanced;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Tools.Read;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Write;

/// <summary>
/// An Advanced sensor's rules and fields (docs/mcp-extensions-plan.md §7), through the same service as its page. No review step: a
/// token acts for its person, who holds Monitoring: Edit. Every change re-judges the latest report, and the answer carries each rule's
/// outcome then, so the AI and the person see at once what the sensor says.
/// </summary>
public static class AdvancedWriteTools
{
    private static Field[] RuleFields() =>
    [
        Text("name", "The rule's name"), Bool("enabled", "true when left out"), Text("note", "A note for whoever reads the rule"),
        Text("path", "Where in the report: a JSON path as disk.free_gb or services[*].state"),
        Choice("operator", "How the value is compared", Enum.GetNames<AdvancedOperator>()),
        Text("operand", "What it is compared with"), Text("operand2", "Between and Outside: the upper bound"),
        Bool("ignoreCase", "Texts compared without case"),
        Choice("quantifier", "With * in the path: All (when left out), Any or None of the values", Enum.GetNames<AdvancedQuantifier>()),
        Choice("whenMissing", "When the path matches nothing: Fail (when left out), Warn or Pass", Enum.GetNames<AdvancedWhenMissing>()),
        Choice("severity", "When it doesn't hold: Fail (when left out) or Warn", Enum.GetNames<AdvancedSeverity>()),
    ];

    /// <summary>A rule's JSON Schema, for <c>advanced_test</c>'s drafts too.</summary>
    public static JsonObject RuleSchema() => Schema(RuleFields());

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("advanced_rules_change", "Change an advanced sensor's rules",
            "Creates, changes (by ruleId; what is left out stays) or removes an Advanced sensor's rule; orders them (ids: every rule once); or copies " +
            "another Advanced sensor's rules and fields (fromSensorId, replace or append). The answer is the sensor with each rule's outcome against " +
            "the latest report, judged again with the new rules. advanced_test tries rules without saving them.",
            Schema([Id("sensorId", "The ADVANCED sensor"), Choice("action", "What to do", ["create", "update", "remove", "order", "copy"], true),
                Id("ruleId", "update, remove: the rule", required: false), Ids("ids", "order: every rule's id, in the new order", required: false),
                Id("fromSensorId", "copy: the sensor to copy from", required: false), Bool("replace", "copy: replace this sensor's rules and fields"),
                .. RuleFields()]),
            Area.Monitoring, Level.Edit, ToolKind.Write, RulesAsync) { AuditEvents = [AuditEvents.AdvancedRulesChanged] },
        new("advanced_fields_change", "Change an advanced sensor's fields",
            "Creates, changes (by fieldId; what is left out stays) or removes an Advanced sensor's field, the values its page shows and charts; or " +
            "orders them (ids: every field once).",
            Schema(Id("sensorId", "The ADVANCED sensor"), Choice("action", "What to do", ["create", "update", "remove", "order"], true),
                Id("fieldId", "update, remove: the field", required: false), Ids("ids", "order: every field's id, in the new order", required: false),
                Text("name", "The label"), Text("path", "Where in the report, without *"),
                Choice("format", "How the value shows: Auto when left out", Enum.GetNames<AdvancedFieldFormat>()), Text("unit", "A unit, as GB"),
                Int("decimals", "0 to 6; as needed when left out"), Bool("pinned", "Shown at the top"), Bool("chart", "Charted over time")),
            Area.Monitoring, Level.Edit, ToolKind.Write, FieldsAsync) { AuditEvents = [AuditEvents.AdvancedFieldsChanged] },
    ];

    /// <summary>A rule from the arguments, merged with <paramref name="current"/> on a change.</summary>
    public static AdvancedRuleRequest Rule(JsonElement args, AdvancedRuleItem? current) => new()
    {
        Name = OptionalText(args, "name", 200) ?? current?.Name,
        Enabled = OptionalBool(args, "enabled") ?? current?.Enabled ?? true,
        Note = OptionalText(args, "note", 1000) is { } note ? (note.Length == 0 ? null : note) : current?.Note,
        Path = OptionalText(args, "path", 500) ?? current?.Path,
        Operator = OptionalEnum<AdvancedOperator>(args, "operator") ?? current?.Operator ?? AdvancedOperator.Eq,
        Operand = OptionalText(args, "operand", 1000) ?? current?.Operand,
        Operand2 = OptionalText(args, "operand2", 1000) ?? current?.Operand2,
        IgnoreCase = OptionalBool(args, "ignoreCase") ?? current?.IgnoreCase ?? false,
        Quantifier = OptionalEnum<AdvancedQuantifier>(args, "quantifier") ?? current?.Quantifier ?? AdvancedQuantifier.All,
        WhenMissing = OptionalEnum<AdvancedWhenMissing>(args, "whenMissing") ?? current?.WhenMissing ?? AdvancedWhenMissing.Fail,
        Severity = OptionalEnum<AdvancedSeverity>(args, "severity") ?? current?.Severity ?? AdvancedSeverity.Fail,
    };

    private static AdvancedFieldRequest Field(JsonElement args, AdvancedFieldItem? current) => new()
    {
        Name = OptionalText(args, "name", 200) ?? current?.Name,
        Path = OptionalText(args, "path", 500) ?? current?.Path,
        Format = OptionalEnum<AdvancedFieldFormat>(args, "format") ?? current?.Format ?? AdvancedFieldFormat.Auto,
        Unit = OptionalText(args, "unit", 32) is { } unit ? (unit.Length == 0 ? null : unit) : current?.Unit,
        Decimals = Has(args, "decimals") ? IntOf(args, "decimals") : current?.Decimals,
        Pinned = OptionalBool(args, "pinned") ?? current?.Pinned ?? false,
        Chart = OptionalBool(args, "chart") ?? current?.Chart ?? false,
    };

    private static async Task<string> NameAsync(PnmtdDbContext db, Guid id, CancellationToken ct) =>
        await db.Sensors.AsNoTracking().Where(s => s.Id == id && s.Type == SensorType.ADVANCED).Select(s => s.Parent.Name + " / " + s.Name)
            .SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("Advanced sensor");

    private static object Answer(ToolContext ctx, Guid id, string name, AdvancedSensorDetail detail) => new
    {
        Sensor = MonitoringReadTools.AdvancedView(ctx, id, name, detail, null),
        Note = "Each rule's outcome is the latest report judged again with the rules as they are now.",
    };

    private static async Task<object> RulesAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var name = await NameAsync(db, id, ct);
        ctx.Target("Sensor", id, name);
        var advanced = ctx.Services.GetRequiredService<AdvancedSensorService>();
        var action = OptionalText(args, "action", 16);
        AdvancedSensorDetail detail;
        switch (action)
        {
            case "create":
                detail = await advanced.CreateRuleAsync(id, Rule(args, null), ct);
                break;
            case "update":
                var ruleId = IdOf(args, "ruleId");
                var current = (await advanced.RequireDetailAsync(id, ct)).Rules.FirstOrDefault(r => r.Id == ruleId) ?? throw ToolException.NotFound("rule");
                detail = await advanced.UpdateRuleAsync(id, ruleId, Rule(args, current), ct);
                break;
            case "remove":
                detail = await advanced.DeleteRuleAsync(id, IdOf(args, "ruleId"), ct);
                break;
            case "order":
                detail = await advanced.OrderRulesAsync(id, new AdvancedOrderRequest { Ids = [.. IdsOf(args, "ids", AdvancedLimits.MaxRules)] }, ct);
                break;
            case "copy":
                detail = await advanced.CopyAsync(id, new AdvancedCopyRequest { FromSensorId = IdOf(args, "fromSensorId"), Replace = OptionalBool(args, "replace") ?? false }, ct);
                break;
            default:
                throw ToolException.Invalid("action is create, update, remove, order or copy.");
        }
        ctx.Summary = $"{action} the rules of {name}";
        return Answer(ctx, id, name, detail);
    }

    private static async Task<object> FieldsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args, "sensorId");
        var name = await NameAsync(db, id, ct);
        ctx.Target("Sensor", id, name);
        var advanced = ctx.Services.GetRequiredService<AdvancedSensorService>();
        var action = OptionalText(args, "action", 16);
        AdvancedSensorDetail detail;
        switch (action)
        {
            case "create":
                detail = await advanced.CreateFieldAsync(id, Field(args, null), ct);
                break;
            case "update":
                var fieldId = IdOf(args, "fieldId");
                var current = (await advanced.RequireDetailAsync(id, ct)).Fields.FirstOrDefault(f => f.Id == fieldId) ?? throw ToolException.NotFound("field");
                detail = await advanced.UpdateFieldAsync(id, fieldId, Field(args, current), ct);
                break;
            case "remove":
                detail = await advanced.DeleteFieldAsync(id, IdOf(args, "fieldId"), ct);
                break;
            case "order":
                detail = await advanced.OrderFieldsAsync(id, new AdvancedOrderRequest { Ids = [.. IdsOf(args, "ids", AdvancedLimits.MaxFields)] }, ct);
                break;
            default:
                throw ToolException.Invalid("action is create, update, remove or order.");
        }
        ctx.Summary = $"{action} the fields of {name}";
        return Answer(ctx, id, name, detail);
    }
}
