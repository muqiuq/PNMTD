using PNMTD.Lib.Certificates;
using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

/// <summary>
/// An SSL_CERTIFICATE sensor's settings (docs/ssl-certificate-plan.md §6.2), in <c>SensorRequest.Certificate</c>; its
/// <c>host:port</c> is the request's <c>Parameters</c>. A null check or rule takes the trust mode's default on a new sensor
/// and when the mode changes, and keeps its value otherwise; null <see cref="AuthorityIds"/> keeps the CAs. The server name
/// and the address to connect to are values: null is none.
/// </summary>
public sealed class CertificateSensorRequest
{
    public CertificateTrust Trust { get; init; } = CertificateTrust.Public;

    /// <summary>The private CAs, in the private mode.</summary>
    public List<Guid>? AuthorityIds { get; init; }

    public bool? CheckName { get; init; }

    public bool? CheckExpiry { get; init; }

    public bool? IncompleteChainFails { get; init; }

    public string? ServerName { get; init; }

    public string? ConnectTo { get; init; }

    /// <summary>1 to 365; null: Certificates__WarnDays (14) on a new sensor.</summary>
    public int? WarnDays { get; init; }

    public bool? AlertOnCertificateChange { get; init; }

    public bool? AlertOnKeyChange { get; init; }

    public bool? AlertOnIssuerChange { get; init; }

    public CertificateIssuerMatch? IssuerMatch { get; init; }
}

/// <summary>A change that waits for a person (§8.3): its kinds ("new key", "issuer A → B") and since when.</summary>
public sealed record CertificatePendingChange(IReadOnlyList<string> Kinds, DateTime Since, bool Certificate, bool Key, bool Issuer);

/// <summary>
/// <c>SensorDetail.Certificate</c>, for <i>Monitoring: View</i> (§6.2): the settings, the schedule and the certificate in brief,
/// what the overview, the status strip and a save need. No certificate details: those are <c>GET …/certificate</c>'s.
/// </summary>
public sealed record CertificateSensorSummary(
    string Host,
    int Port,
    string? ServerName,
    string? ConnectTo,
    CertificateTrust Trust,
    IReadOnlyList<Ref> Authorities,
    bool CheckName,
    bool CheckExpiry,
    bool IncompleteChainFails,
    int WarnDays,
    bool AlertOnCertificateChange,
    bool AlertOnKeyChange,
    bool AlertOnIssuerChange,
    CertificateIssuerMatch IssuerMatch,
    DateTime? LastCheckAt,
    DateTime? NextCheckAt,
    DateTime? LastSuccessAt,
    bool Checking,
    int ConsecutiveFailures,
    DateTime? FirstFailureAt,
    string? LastError,
    DateTime? ExpiresAt,
    int? DaysLeft,
    string? Issuer,
    string? IssuerOrganization,
    string? SubjectCommonName,
    bool SelfSigned,
    DateTime? AcceptedAt,
    string? AcceptedBy,
    CertificatePendingChange? ChangePending);

/// <summary>A version with everything it says, and the chain as built at the last check that saw it.</summary>
public sealed record CertificateItem(Guid Id, CertificateFacts Facts, IReadOnlyList<CertificateChainEntry> Chain, DateTime FirstSeenAt, DateTime LastSeenAt);

/// <summary>One field of the comparison of the accepted and the current certificate (§8.4).</summary>
public sealed record CertificateFieldChange(string Field, string? Accepted, string? Current, bool Changed);

/// <summary>What differs between the accepted and the current certificate, whether that alarms, and since when.</summary>
public sealed record CertificateChange(bool Certificate, bool Key, bool Issuer, bool Alarm, IReadOnlyList<string> Kinds, DateTime Since,
    IReadOnlyList<CertificateFieldChange> Fields);

/// <summary><c>GET /sensors/{id}/certificate</c>, for <i>Certificates: View</i> (§6.2): the certificate, the baseline, the change, the connection, the validation.</summary>
public sealed record CertificateSensorDetail(
    CertificateSensorSummary Summary,
    CertificateItem? Current,
    CertificateItem? Accepted,
    string? AcceptedNote,
    CertificateChange? Change,
    CertificateConnection? Connection,
    CertificateValidation? Validation,
    int VersionCount);

/// <summary>
/// <c>POST /sensors/{id}/certificate/accept</c> (§8.4): the SHA-256 of the certificate the person saw, and their explicit
/// confirmation, which the server requires; a note for the audit log.
/// </summary>
public sealed class CertificateAcceptRequest
{
    public string? Sha256 { get; init; }

    public bool Confirmed { get; init; }

    public string? Note { get; init; }
}

/// <summary>A row of the certificates seen.</summary>
public sealed record CertificateVersionItem(Guid Id, string Sha256, string Serial, string Issuer, string? SubjectCommonName, DateTime NotBefore,
    DateTime NotAfter, DateTime FirstSeenAt, DateTime LastSeenAt, bool Current, bool Accepted);

/// <summary><c>GET /sensors/{id}/certificate/versions</c>: newest first.</summary>
public sealed class CertificateVersionQuery : PageQuery;

/// <summary>A private CA (§4.5) as the list shows it.</summary>
public sealed record CertificateAuthorityItem(Guid Id, string Name, string RootSubject, string RootSha256, DateTime RootNotBefore, DateTime RootNotAfter,
    int IntermediateCount, int UsedBy, DateTime CreatedAt, string? CreatedBy);

/// <summary><c>POST /certificate-authorities</c> and its preview: a name and the PEM (the root, and its intermediates if wanted).</summary>
public sealed class CertificateAuthorityRequest
{
    public string? Name { get; init; }

    public string? Pem { get; init; }
}

/// <summary>One certificate of a CA's PEM, as the preview reads it.</summary>
public sealed record CertificateAuthorityCertificate(CertificateName Subject, CertificateName Issuer, string Sha256, DateTime NotBefore, DateTime NotAfter,
    bool Root);

/// <summary>What §4.5's rules make of a PEM: valid, or why not; the certificates; whether a private key in it was left out.</summary>
public sealed record CertificateAuthorityPreview(bool Valid, string? Error, IReadOnlyList<CertificateAuthorityCertificate> Certificates, bool PrivateKeyLeftOut);
