using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Cookie.Lounge;

namespace PNMTD.Api.V1;

/// <summary>
/// The lounge (docs/lounge-plan.md §14): one conversation, the same for every signed-in user; writing needs <i>Lounge: Use</i> and a started
/// lounge.
/// </summary>
[Route("api/v1/lounge")]
[Authorize]
public sealed class LoungeController(LoungeService lounge) : V1ControllerBase
{
    /// <summary>
    /// The newest 100 lines, or those after <c>afterId</c> with the older ones that changed since (a comment taken up, a line removed in the last
    /// 10 minutes), to be replaced by id; and the state. The same for everyone.
    /// </summary>
    [HttpGet]
    public async Task<LoungeRoom> Room([FromQuery] long? afterId, CancellationToken ct) => await lounge.RoomAsync(User, afterId, ct);

    /// <summary>A message (the voices answer it) or a comment (they take it up). 409 while the lounge is closed, 429 over 20 an hour.</summary>
    [HttpPost("messages")]
    [RequireRight(Area.Lounge, Level.Use)]
    [NotAudited("a line of the lounge's conversation, which everyone reads; removing one is audited")]
    public async Task<LoungeMessageItem> Post(LoungePostRequest request, CancellationToken ct) => await lounge.PostAsync(request, User, ct);

    /// <summary>The side panel: the keepsakes, and the convictions with both positions.</summary>
    [HttpGet("about")]
    public async Task<LoungeAbout> About(CancellationToken ct) => await lounge.AboutAsync(ct);

}

/// <summary>One's own switches for the lounge (docs/lounge-plan.md §9.4), with one's account: Cookie and Seraph may talk about one, and may e-mail one.</summary>
[Route("api/v1/account/lounge")]
[Authorize]
public sealed class LoungeAccountController(LoungeService lounge) : V1ControllerBase
{
    [HttpGet]
    public async Task<LoungeSwitches> Get(CancellationToken ct) => await lounge.SwitchesAsync(User, ct);

    [HttpPut]
    [Audit(AuditEvents.LoungeSwitchesChanged)]
    public async Task<LoungeSwitches> Save(LoungeSwitches request, CancellationToken ct) => await lounge.SaveSwitchesAsync(request, User, ct);
}

/// <summary>A user's switches for the lounge on Admin → Users (docs/lounge-plan.md §9.4): admins only, as the user's page is.</summary>
[Route("api/v1/users/{id:guid}/lounge")]
[RequireAdmin]
public sealed class LoungeUsersController(LoungeService lounge) : V1ControllerBase
{
    [HttpGet]
    public async Task<LoungeSwitches> Get(Guid id, CancellationToken ct) => await lounge.SwitchesAsync(id, ct);

    [HttpPut]
    [Audit(AuditEvents.LoungeSwitchesChanged)]
    public async Task<LoungeSwitches> Save(Guid id, LoungeSwitches request, CancellationToken ct) =>
        await lounge.SaveSwitchesAsync(id, request, User.Identity?.Name ?? "an admin", ct);
}

/// <summary>Admin → Cookie → Lounge (docs/lounge-plan.md §14, §15.3): <i>Cookie: Manage</i>, like every tab there.</summary>
[Route("api/v1/cookie/admin/lounge")]
[RequireRight(Area.Cookie, Level.Manage)]
public sealed class LoungeAdminController(LoungeService lounge) : V1ControllerBase
{
    [HttpGet]
    public async Task<LoungeAdminStatus> Status(CancellationToken ct) => await lounge.StatusAsync(ct);

    [HttpPost("start")]
    [Audit(AuditEvents.LoungeStarted)]
    public async Task<LoungeAdminStatus> Start(CancellationToken ct) => await lounge.StartAsync(User, ct);

    [HttpPost("stop")]
    [Audit(AuditEvents.LoungeStopped)]
    public async Task<LoungeAdminStatus> Stop(CancellationToken ct) => await lounge.StopAsync(User, ct);

    [HttpPost("talk")]
    [Audit(AuditEvents.LoungeTalkNow)]
    public async Task<LoungeAdminStatus> Talk(CancellationToken ct) => await lounge.TalkNowAsync(ct);

    [HttpPost("sleep")]
    [Audit(AuditEvents.LoungeSlept)]
    public async Task<LoungeAdminStatus> Sleep(CancellationToken ct) => await lounge.SleepAsync(User, ct);

    [HttpGet("scenes")]
    public async Task<IReadOnlyList<LoungeSceneItem>> Scenes(CancellationToken ct) => await lounge.ScenesAsync(ct);

    [HttpGet("scenes/{id:long}")]
    public async Task<LoungeSceneDetail> Scene(long id, CancellationToken ct) => await lounge.SceneAsync(id, ct);

    [HttpGet("memories")]
    public async Task<IReadOnlyList<LoungeMemoryItem>> Memories([FromQuery] string? voice, CancellationToken ct) => await lounge.MemoriesAsync(voice, ct);

    [HttpDelete("memories/{id:long}")]
    [Audit(AuditEvents.LoungeMemoryDeleted)]
    public async Task<IActionResult> DeleteMemory(long id, CancellationToken ct)
    {
        await lounge.DeleteMemoryAsync(id, ct);
        return NoContent();
    }

    [HttpPost("forget")]
    [Audit(AuditEvents.LoungeForgotten)]
    public async Task<IActionResult> Forget(LoungeForgetRequest request, CancellationToken ct)
    {
        await lounge.ForgetAsync(request, ct);
        return NoContent();
    }

    [HttpDelete("messages/{id:long}")]
    [Audit(AuditEvents.LoungeMessageDeleted)]
    public async Task<IActionResult> DeleteMessage(long id, CancellationToken ct)
    {
        await lounge.DeleteMessageAsync(id, User, ct);
        return NoContent();
    }

    [HttpGet("opinions")]
    public async Task<IReadOnlyList<LoungeOpinionItem>> Opinions(CancellationToken ct) => await lounge.OpinionsAsync(ct);

    [HttpPost("positions/reset")]
    [Audit(AuditEvents.LoungePositionsReset)]
    public async Task<IActionResult> ResetPositions(CancellationToken ct)
    {
        await lounge.ResetPositionsAsync(ct);
        return NoContent();
    }
}
