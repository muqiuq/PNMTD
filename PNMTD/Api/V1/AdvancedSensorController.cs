using Microsoft.AspNetCore.Mvc;
using PNMTD.Advanced;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;

namespace PNMTD.Api.V1;

/// <summary>
/// An Advanced sensor's own endpoints (docs/advanced-sensor-plan.md §8.3): its reports, their paths and charts, trying rules,
/// and its rules and fields. Its setting goes through <c>/sensors</c>. A sensor whose data names people needs Monitoring:
/// Details besides View for what shows its data (the service checks).
/// </summary>
[Route("api/v1/sensors/{id:guid}/advanced")]
public sealed class AdvancedSensorController(AdvancedSensorService advanced) : V1ControllerBase
{
    [HttpGet]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<AdvancedSensorDetail>> Get(Guid id, CancellationToken ct) => await advanced.RequireDetailAsync(id, ct);

    // --- reports ---------------------------------------------------------------------------------------------------------

    [HttpGet("reports")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<AdvancedReportItem>>> Reports(Guid id, [FromQuery] AdvancedReportQuery query, CancellationToken ct) =>
        await advanced.ReportsAsync(id, query, ct);

    [HttpGet("reports/latest")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<AdvancedReportDetail>> Latest(Guid id, CancellationToken ct) => await advanced.ReportAsync(id, null, ct);

    [HttpGet("reports/{reportId:guid}")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<AdvancedReportDetail>> Report(Guid id, Guid reportId, CancellationToken ct) => await advanced.ReportAsync(id, reportId, ct);

    /// <summary>A report that carried what it shouldn't, such as a password (§13). Audit-logged.</summary>
    [HttpDelete("reports/{reportId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedReportDeleted)]
    public async Task<IActionResult> DeleteReport(Guid id, Guid reportId, CancellationToken ct)
    {
        await advanced.DeleteReportAsync(id, reportId, ct);
        return NoContent();
    }

    /// <summary>Every report and charted value of the sensor. Audit-logged.</summary>
    [HttpDelete("reports")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedReportsCleared)]
    public async Task<IActionResult> ClearReports(Guid id, CancellationToken ct)
    {
        await advanced.ClearReportsAsync(id, ct);
        return NoContent();
    }

    [HttpGet("paths")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<IReadOnlyList<AdvancedPathItem>>> Paths(Guid id, CancellationToken ct) => Ok(await advanced.PathsAsync(id, ct));

    [HttpGet("samples")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<AdvancedSamples>> Samples(Guid id, [FromQuery] AdvancedSamplesQuery query, CancellationToken ct) =>
        await advanced.SamplesAsync(id, query, ct);

    /// <summary>Evaluates rules against a report without storing anything (§6.6).</summary>
    [HttpPost("test")]
    [RequireRight(Area.Monitoring, Level.View)]
    [NotAudited("a test: evaluates rules, stores and notifies nothing")]
    public async Task<ActionResult<AdvancedTestResult>> Test(Guid id, AdvancedTestRequest request, CancellationToken ct) =>
        await advanced.TestAsync(id, request, ct);

    // --- rules -----------------------------------------------------------------------------------------------------------

    /// <summary>The answer waits up to 5 seconds for the evaluator, so it carries the rules' new outcomes (§7.5).</summary>
    [HttpPost("rules")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedRulesChanged)]
    public async Task<ActionResult<AdvancedSensorDetail>> CreateRule(Guid id, AdvancedRuleRequest request, CancellationToken ct) =>
        await advanced.CreateRuleAsync(id, request, ct);

    [HttpPut("rules/order")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedRulesChanged)]
    public async Task<ActionResult<AdvancedSensorDetail>> OrderRules(Guid id, AdvancedOrderRequest request, CancellationToken ct) =>
        await advanced.OrderRulesAsync(id, request, ct);

    [HttpPost("rules/copy")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedRulesChanged)]
    public async Task<ActionResult<AdvancedSensorDetail>> CopyRules(Guid id, AdvancedCopyRequest request, CancellationToken ct) =>
        await advanced.CopyAsync(id, request, ct);

    [HttpPut("rules/{ruleId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedRulesChanged)]
    public async Task<ActionResult<AdvancedSensorDetail>> UpdateRule(Guid id, Guid ruleId, AdvancedRuleRequest request, CancellationToken ct) =>
        await advanced.UpdateRuleAsync(id, ruleId, request, ct);

    [HttpDelete("rules/{ruleId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedRulesChanged)]
    public async Task<ActionResult<AdvancedSensorDetail>> DeleteRule(Guid id, Guid ruleId, CancellationToken ct) =>
        await advanced.DeleteRuleAsync(id, ruleId, ct);

    // --- fields ----------------------------------------------------------------------------------------------------------

    [HttpPost("fields")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedFieldsChanged)]
    public async Task<ActionResult<AdvancedSensorDetail>> CreateField(Guid id, AdvancedFieldRequest request, CancellationToken ct) =>
        await advanced.CreateFieldAsync(id, request, ct);

    [HttpPut("fields/order")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedFieldsChanged)]
    public async Task<ActionResult<AdvancedSensorDetail>> OrderFields(Guid id, AdvancedOrderRequest request, CancellationToken ct) =>
        await advanced.OrderFieldsAsync(id, request, ct);

    [HttpPut("fields/{fieldId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedFieldsChanged)]
    public async Task<ActionResult<AdvancedSensorDetail>> UpdateField(Guid id, Guid fieldId, AdvancedFieldRequest request, CancellationToken ct) =>
        await advanced.UpdateFieldAsync(id, fieldId, request, ct);

    [HttpDelete("fields/{fieldId:guid}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.AdvancedFieldsChanged)]
    public async Task<ActionResult<AdvancedSensorDetail>> DeleteField(Guid id, Guid fieldId, CancellationToken ct) =>
        await advanced.DeleteFieldAsync(id, fieldId, ct);
}
