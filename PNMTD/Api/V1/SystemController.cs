using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Services.Domain;
using TaskStatus = PNMTD.Api.V1.Contracts.TaskStatus;

namespace PNMTD.Api.V1;

[Route("api/v1/system")]
[Authorize]
public sealed class SystemController(SystemService system) : V1ControllerBase
{
    /// <summary>Anonymous: the login screen shows the identity. Signed in, more (see <see cref="SystemInfo"/>).</summary>
    [HttpGet("info")]
    [AllowAnonymous]
    public ActionResult<SystemInfo> Info() =>
        system.Info(User.Identity?.IsAuthenticated == true);

    [HttpGet("uplink")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<UplinkStatus>> Uplink(CancellationToken ct) => await system.UplinkAsync(ct);

    [HttpGet("tasks")]
    [RequireRight(Area.System, Level.Manage)]
    public async Task<ActionResult<IReadOnlyList<TaskStatus>>> Tasks(CancellationToken ct) => Ok(await system.TasksAsync(ct));

    [HttpGet("status-codes")]
    [RequireRight(Area.Monitoring, Level.View)]
    public ActionResult<IReadOnlyList<StatusCodeInfo>> StatusCodes() => Ok(SystemService.StatusCodes());

    [HttpGet("sensor-types")]
    [RequireRight(Area.Monitoring, Level.View)]
    public ActionResult<IReadOnlyList<SensorTypeInfo>> SensorTypes() => Ok(SystemService.SensorTypes());

    [HttpGet("notification-rule-types")]
    [RequireRight(Area.Monitoring, Level.View)]
    public ActionResult<IReadOnlyList<NotificationRuleTypeInfo>> NotificationRuleTypes() => Ok(SystemService.NotificationRuleTypes());
}

[Route("api/v1/dashboard")]
[RequireRight(Area.Monitoring, Level.View)]
public sealed class DashboardController(DashboardCache dashboard) : V1ControllerBase
{
    /// <summary>At most about 10 seconds old; <c>generatedAt</c> says when it was built.</summary>
    [HttpGet]
    public async Task<ActionResult<Dashboard>> Get() => await dashboard.GetAsync();
}

[Route("api/v1/search")]
[RequireRight(Area.Monitoring, Level.View)]
public sealed class SearchController(SearchService search) : V1ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SearchResult>> Get([FromQuery] string? q, [FromQuery] int limit = 10, CancellationToken ct = default) =>
        await search.SearchAsync(q, limit, ct);
}
