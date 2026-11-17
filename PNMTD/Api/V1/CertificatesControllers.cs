using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Auth.Rights;
using PNMTD.Certificates;

namespace PNMTD.Api.V1;

/// <summary>
/// An SSL certificate sensor's own endpoints (docs/ssl-certificate-plan.md §6.3): the certificate, Check now, accepting a
/// change, the certificates seen and their PEM. Its settings go through <c>/sensors</c>.
/// </summary>
[Route("api/v1/sensors/{id:guid}/certificate")]
public sealed class SensorCertificateController(CertificateSensorService certificates) : V1ControllerBase
{
    [HttpGet]
    [RequireRight(Area.Certificates, Level.View)]
    public async Task<ActionResult<CertificateSensorDetail>> Get(Guid id, CancellationToken ct) => await certificates.DetailAsync(id, ct);

    /// <summary>Checks now, and answers when the check is done.</summary>
    [HttpPost("check")]
    [RequireRight(Area.Certificates, Level.Edit)]
    [Audit(AuditEvents.CertificateCheckRequested)]
    public async Task<ActionResult<CertificateSensorDetail>> Check(Guid id, CancellationToken ct) => await certificates.CheckNowAsync(id, ct);

    /// <summary>Accepts the current certificate (§8.4): only with <c>confirmed: true</c> and the SHA-256 the person saw. Audit-logged.</summary>
    [HttpPost("accept")]
    [RequireRight(Area.Certificates, Level.Edit)]
    [Audit(AuditEvents.CertificateAccepted)]
    public async Task<ActionResult<CertificateSensorDetail>> Accept(Guid id, CertificateAcceptRequest request, CancellationToken ct) =>
        await certificates.AcceptAsync(id, request, ct);

    [HttpGet("versions")]
    [RequireRight(Area.Certificates, Level.View)]
    public async Task<ActionResult<Paged<CertificateVersionItem>>> Versions(Guid id, [FromQuery] CertificateVersionQuery query, CancellationToken ct) =>
        await certificates.VersionsAsync(id, query, ct);

    [HttpGet("versions/{versionId:guid}")]
    [RequireRight(Area.Certificates, Level.View)]
    public async Task<ActionResult<CertificateItem>> Version(Guid id, Guid versionId, CancellationToken ct) =>
        await certificates.VersionAsync(id, versionId, ct);

    /// <summary>The leaf, or with <c>chain=true</c> the chain as the server sent it. Certificates are public: not audited.</summary>
    [HttpGet("versions/{versionId:guid}/pem")]
    [RequireRight(Area.Certificates, Level.View)]
    [Produces("application/x-pem-file")]
    public async Task<IActionResult> Pem(Guid id, Guid versionId, [FromQuery] bool chain, CancellationToken ct)
    {
        var (name, pem) = await certificates.PemAsync(id, versionId, chain, ct);
        return File(System.Text.Encoding.ASCII.GetBytes(pem), "application/x-pem-file", name);
    }
}

/// <summary>The private CAs (docs/ssl-certificate-plan.md §4.5, §8.5): read with Certificates' View, changed with its Manage.</summary>
[Route("api/v1/certificate-authorities")]
public sealed class CertificateAuthoritiesController(CertificateAuthorityService authorities) : V1ControllerBase
{
    [HttpGet]
    [RequireRight(Area.Certificates, Level.View)]
    public async Task<ActionResult<IReadOnlyList<CertificateAuthorityItem>>> List(CancellationToken ct) => Ok(await authorities.ListAsync(ct));

    /// <summary>What §4.5's rules make of a PEM, before saving.</summary>
    [HttpPost("preview")]
    [RequireRight(Area.Certificates, Level.Manage)]
    [NotAudited("a preview: parses a PEM, changes nothing")]
    public ActionResult<CertificateAuthorityPreview> Preview(CertificateAuthorityRequest request) => authorities.Preview(request);

    [HttpPost]
    [RequireRight(Area.Certificates, Level.Manage)]
    [Audit(AuditEvents.CertificateAuthorityAdded)]
    public async Task<ActionResult<CertificateAuthorityItem>> Create(CertificateAuthorityRequest request, CancellationToken ct) =>
        await authorities.CreateAsync(request, ct);

    [HttpDelete("{id:guid}")]
    [RequireRight(Area.Certificates, Level.Manage)]
    [Audit(AuditEvents.CertificateAuthorityRemoved)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await authorities.DeleteAsync(id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/pem")]
    [RequireRight(Area.Certificates, Level.View)]
    [Produces("application/x-pem-file")]
    public async Task<IActionResult> Pem(Guid id, CancellationToken ct)
    {
        var (name, pem) = await authorities.PemAsync(id, ct);
        var file = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-');
        return File(System.Text.Encoding.ASCII.GetBytes(pem), "application/x-pem-file", $"{(file.Length > 0 ? file : "ca")}.pem");
    }
}
