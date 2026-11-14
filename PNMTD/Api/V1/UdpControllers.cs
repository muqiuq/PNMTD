using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Lib.Udp;
using PNMTD.Services.Domain;
using PNMTD.Udp;

namespace PNMTD.Api.V1;

/// <summary>A UDP node's own endpoints (docs/udp-iot-plan.md §5): keys, resets, last packets, commands, updates.</summary>
[Route("api/v1/sensors/{id:guid}/udp")]
[Authorize]
public sealed class SensorUdpController(UdpNodeService nodes, UdpCommandService commands, UdpOtaService ota) : V1ControllerBase
{
    /// <summary>Overwrites the key or the token manually: sessions and the counter are reset.</summary>
    [HttpPut("key")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.UdpKeyChanged)]
    public async Task<ActionResult<UdpSecretsChanged>> SetKey(Guid id, UdpKeyRequest request, CancellationToken ct) =>
        await nodes.SetSecretsAsync(id, request, ct);

    [HttpPost("regenerate-key")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.UdpKeyChanged)]
    public async Task<ActionResult<UdpSecretsChanged>> Regenerate(Guid id, UdpRegenerateRequest request, CancellationToken ct) =>
        await nodes.RegenerateAsync(id, request, ct);

    [HttpPost("reset-counter")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.UdpCounterReset)]
    public async Task<IActionResult> ResetCounter(Guid id, CancellationToken ct)
    {
        await nodes.ResetCounterAsync(id, ct);
        return NoContent();
    }

    [HttpPost("reset-session")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.UdpSessionReset)]
    public async Task<IActionResult> ResetSession(Guid id, CancellationToken ct)
    {
        await nodes.ResetSessionAsync(id, ct);
        return NoContent();
    }

    /// <summary>The last packet of each type, with its bytes and fields.</summary>
    [HttpGet("last-packets")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<IReadOnlyList<UdpPacketView>>> LastPackets(Guid id, CancellationToken ct) =>
        Ok(await nodes.LastPacketsAsync(id, ct));

    /// <summary>The node's recent rejections, from memory (since the restart).</summary>
    [HttpGet("rejections")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<IReadOnlyList<UdpRejectionView>>> Rejections(Guid id, CancellationToken ct) =>
        Ok(await nodes.RejectionsAsync(id, ct));

    [HttpGet("commands")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<Paged<UdpCommandItem>>> Commands(Guid id, [FromQuery] UdpCommandQuery query, CancellationToken ct)
    {
        await nodes.EnsureNodeAsync(id, ct);
        return await commands.ListAsync(id, query, ct);
    }

    /// <summary>A command's communication, from the permanent command log, oldest first.</summary>
    [HttpGet("commands/{commandId:guid}/log")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<IReadOnlyList<UdpLogItem>>> CommandLog(Guid id, Guid commandId, CancellationToken ct) =>
        Ok(await commands.CommandLogAsync(id, commandId, ct));

    [HttpPost("commands")]
    [RequireRight(Area.Commands, Level.Run)]
    [Audit(AuditEvents.UdpCommandQueued)]
    public async Task<ActionResult<UdpCommandCreated>> Queue(Guid id, UdpCommandRequest request, CancellationToken ct) =>
        await commands.CreateAsync(id, request, ct);

    [HttpDelete("commands/{commandId:guid}")]
    [RequireRight(Area.Commands, Level.Run)]
    [Audit(AuditEvents.UdpCommandCancelled)]
    public async Task<IActionResult> Cancel(Guid id, Guid commandId, CancellationToken ct)
    {
        await commands.CancelAsync(id, commandId, ct);
        return NoContent();
    }

    /// <summary>The node's firmware updates, each with its command-log rows.</summary>
    [HttpGet("ota-jobs")]
    [RequireRight(Area.Monitoring, Level.View)]
    public async Task<ActionResult<IReadOnlyList<UdpOtaJobDetail>>> Updates(Guid id, CancellationToken ct)
    {
        await nodes.EnsureNodeAsync(id, ct);
        return Ok(await ota.NodeJobsAsync(id, ct));
    }
}

/// <summary>The admin page's endpoints (docs/udp-iot-plan.md §5, §6.6): the listener, every node, the log, firmware, updates.</summary>
[Route("api/v1/udp")]
[RequireRight(Area.UdpNodes, Level.Manage)]
public sealed class UdpController(UdpNodeService nodes, UdpCommandService commands, UdpOtaService ota) : V1ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<UdpStatus>> Status(CancellationToken ct) => await nodes.StatusAsync(ct);

    [HttpGet("nodes")]
    public async Task<ActionResult<Paged<UdpNodeListItem>>> Nodes([FromQuery] UdpNodeQuery query, CancellationToken ct) =>
        await nodes.ListAsync(query, ct);

    [HttpGet("command-log")]
    public async Task<ActionResult<Paged<UdpLogItem>>> CommandLog([FromQuery] UdpLogQuery query, CancellationToken ct) =>
        await commands.LogAsync(query, ct);

    [HttpGet("unknown-senders")]
    public async Task<ActionResult<IReadOnlyList<UdpUnknownSenderItem>>> UnknownSenders(CancellationToken ct) =>
        Ok(await nodes.UnknownSendersAsync(ct));

    [HttpGet("firmware")]
    public async Task<ActionResult<IReadOnlyList<UdpFirmwareItem>>> Firmware(CancellationToken ct) => Ok(await ota.FirmwareAsync(ct));

    /// <summary>
    /// Uploads a .bin (multipart/form-data: file, name, version, notes). No Consumes constraint: it
    /// would decide at routing, before authorization, and answer other roles with 404 instead of 403.
    /// </summary>
    [HttpPost("firmware")]
    [FirmwareUploadLimit]
    [Audit(AuditEvents.UdpFirmwareUploaded)]
    public async Task<ActionResult<UdpFirmwareUploaded>> Upload(IFormFile? file, [FromForm] string? name, [FromForm] string? version,
        [FromForm] string? notes, CancellationToken ct) =>
        await ota.UploadAsync(file, name, version, notes, ct);

    [HttpPut("firmware/{id:guid}")]
    [Audit(AuditEvents.UdpFirmwareChanged)]
    public async Task<ActionResult<UdpFirmwareItem>> UpdateFirmware(Guid id, UdpFirmwareRequest request, CancellationToken ct) =>
        await ota.UpdateFirmwareAsync(id, request, ct);

    [HttpDelete("firmware/{id:guid}")]
    [Audit(AuditEvents.UdpFirmwareDeleted)]
    public async Task<IActionResult> DeleteFirmware(Guid id, CancellationToken ct)
    {
        await ota.DeleteFirmwareAsync(id, ct);
        return NoContent();
    }

    [HttpGet("firmware/{id:guid}/content")]
    [ProducesResponseType<FileContentResult>(StatusCodes.Status200OK, "application/octet-stream")]
    public async Task<IActionResult> Content(Guid id, CancellationToken ct)
    {
        var (content, fileName) = await ota.ContentAsync(id, ct);
        return File(content, "application/octet-stream", fileName);
    }

    [HttpGet("ota-jobs")]
    public async Task<ActionResult<Paged<UdpOtaJobItem>>> Updates([FromQuery] UdpOtaJobQuery query, CancellationToken ct) =>
        await ota.JobsAsync(query, ct);

    [HttpPost("ota-jobs")]
    [Audit(AuditEvents.UdpUpdateStarted)]
    public async Task<ActionResult<UdpOtaJobsCreated>> StartUpdates(UdpOtaJobRequest request, CancellationToken ct) =>
        await ota.CreateJobsAsync(request, ct);

    [HttpDelete("ota-jobs/{id:guid}")]
    [Audit(AuditEvents.UdpUpdateCancelled)]
    public async Task<IActionResult> CancelUpdate(Guid id, CancellationToken ct)
    {
        await ota.CancelJobAsync(id, ct);
        return NoContent();
    }
}

/// <summary>The Protocol subpage (docs/udp-iot-plan.md §6.5): every role may read it; the node's real key only for User and Admin.</summary>
[Route("api/v1/udp/protocol")]
[RequireRight(Area.Monitoring, Level.View)]
public sealed class UdpProtocolController(UdpProtocolDocs docs) : V1ControllerBase
{
    [HttpGet("examples")]
    public async Task<ActionResult<UdpProtocolExamples>> Examples([FromQuery] Guid? sensorId, CancellationToken ct) =>
        await docs.ExamplesAsync(sensorId, CanSeeSecrets, ct);

    /// <summary>Builds a packet (never sends it): the payload as Base64.</summary>
    [HttpGet("build")]
    public async Task<ActionResult<UdpBuildResult>> Build([FromQuery] Guid? sensorId, [FromQuery] PacketType type = PacketType.Heartbeat,
        [FromQuery] SecuritySuite suite = SecuritySuite.HmacSha256_32, [FromQuery] long fcnt = 1, [FromQuery] bool ack = false,
        [FromQuery] string? payload = null, CancellationToken ct = default) =>
        await docs.BuildAsync(sensorId, type, suite, fcnt, ack, payload, CanSeeSecrets, ct);
}

/// <summary>Raises the request body limit for the firmware upload alone, to Udp:Ota:MaxImageBytes plus the form's overhead (§4.9).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FirmwareUploadLimitAttribute : Attribute, IResourceFilter, IOrderedFilter
{
    public int Order => -2000;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var max = context.HttpContext.RequestServices.GetRequiredService<IOptionsMonitor<UdpOptions>>().CurrentValue.Ota.MaxImageBytes;
        if (context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max + 64 * 1024;
        }
        context.HttpContext.Features.Set<IFormFeature>(null);
        var formOptions = new FormOptions { MultipartBodyLengthLimit = max + 64 * 1024 };
        context.HttpContext.Features.Set<IFormFeature>(new FormFeature(context.HttpContext.Request, formOptions));
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
