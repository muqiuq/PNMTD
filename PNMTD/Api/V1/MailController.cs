using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Services.Domain;

namespace PNMTD.Api.V1;

[Route("api/v1/mail-input-rules")]
[Authorize(Policy = Policies.CanRead)]
public sealed class MailInputRulesController(MailService mail) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<MailInputRule>>> List([FromQuery] PageQuery query, CancellationToken ct) =>
        await mail.ListRulesAsync(query, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MailInputRule>> Get(Guid id, CancellationToken ct) => await mail.GetRuleAsync(id, ct);

    [HttpPost]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<MailInputRule>> Create(MailInputRuleRequest request, CancellationToken ct)
    {
        var rule = await mail.CreateRuleAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = rule.Id }, rule);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<ActionResult<MailInputRule>> Update(Guid id, MailInputRuleRequest request, CancellationToken ct) =>
        await mail.UpdateRuleAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Policies.CanWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await mail.DeleteRuleAsync(id, ct);
        return NoContent();
    }
}

/// <summary>Mail contents: User and Admin only (§4.10).</summary>
[Route("api/v1/mail-log")]
[Authorize(Policy = Policies.CanWrite)]
public sealed class MailLogController(MailService mail) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Paged<MailLogItem>>> List([FromQuery] MailLogQuery query, CancellationToken ct) =>
        await mail.ListLogAsync(query, ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MailLogDetail>> Get(Guid id, CancellationToken ct) => await mail.GetLogAsync(id, ct);
}
