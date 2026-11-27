using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;

namespace PNMTD.Api.V1;

/// <summary>Groups and their rights (docs/audit-redmine-zabbix-plan.md §5.5): only admins.</summary>
[Route("api/v1/groups")]
[RequireAdmin]
public sealed class GroupsController(GroupService groups) : V1ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<GroupItem>> List(CancellationToken ct) => await groups.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<GroupDetail> Get(Guid id, CancellationToken ct) => await groups.GetAsync(id, ct);

    [HttpPost]
    [Audit(AuditEvents.GroupCreated)]
    public async Task<ActionResult<GroupDetail>> Create(GroupRequest request, CancellationToken ct)
    {
        var group = await groups.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = group.Id }, group);
    }

    [HttpPut("{id:guid}")]
    [Audit(AuditEvents.GroupUpdated, AuditEvents.GroupRightsChanged)]
    public async Task<GroupDetail> Update(Guid id, GroupRequest request, CancellationToken ct) => await groups.UpdateAsync(id, request, ct);

    [HttpDelete("{id:guid}")]
    [Audit(AuditEvents.GroupDeleted)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await groups.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/members")]
    [Audit(AuditEvents.GroupMembersChanged)]
    public async Task<GroupDetail> Members(Guid id, GroupMembersRequest request, CancellationToken ct) => await groups.SetMembersAsync(id, request, ct);
}

/// <summary>The areas and levels (§5.2), for the group page and Your access: every signed-in user.</summary>
[Route("api/v1/rights")]
[Authorize]
public sealed class RightsController : V1ControllerBase
{
    [HttpGet]
    public IReadOnlyList<RightsAreaItem> Catalog([FromServices] IConfiguration configuration) => GroupService.Catalog(configuration);
}
