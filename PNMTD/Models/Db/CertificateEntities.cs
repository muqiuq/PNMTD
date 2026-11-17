using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PNMTD.Lib.Certificates;

namespace PNMTD.Models.Db;

/// <summary>How a certificate's issuer is compared (docs/ssl-certificate-plan.md §5.3): by its organisation, or by its full name.</summary>
public enum CertificateIssuerMatch { Organization, Exact }

/// <summary>
/// An SSL_CERTIFICATE sensor's endpoint, trust, rules, state and baseline (docs/ssl-certificate-plan.md §4.1), 1:1, deleted
/// with it. Its <c>host:port</c> is the sensor's <c>Parameters</c>.
/// </summary>
[Table("certificate_sensors")]
[Audited]
public class CertificateSensorEntity
{
    [Key]
    public Guid SensorId { get; set; }

    public virtual SensorEntity Sensor { get; set; } = null!;

    // --- the endpoint -----------------------------------------------------------------------------------------------------

    /// <summary>The SNI and the name the certificate must match. Null: the host, unless the host is an address.</summary>
    [MaxLength(253)]
    public string? ServerName { get; set; }

    /// <summary>An address or name to connect to instead of the host; the server name stays.</summary>
    [MaxLength(253)]
    public string? ConnectTo { get; set; }

    // --- the trust --------------------------------------------------------------------------------------------------------

    public CertificateTrust Trust { get; set; } = CertificateTrust.Public;

    public bool CheckName { get; set; } = true;

    public bool CheckExpiry { get; set; } = true;

    public bool IncompleteChainFails { get; set; }

    // --- the change rules -------------------------------------------------------------------------------------------------

    public int WarnDays { get; set; } = 14;

    public bool AlertOnCertificateChange { get; set; }

    [AuditPlain("a rule's switch: fail on a new public key")]
    public bool AlertOnKeyChange { get; set; }

    public bool AlertOnIssuerChange { get; set; } = true;

    public CertificateIssuerMatch IssuerMatch { get; set; } = CertificateIssuerMatch.Organization;

    // --- the state --------------------------------------------------------------------------------------------------------

    [AuditIgnore]
    public DateTime? NextCheckAt { get; set; }

    [AuditIgnore]
    public DateTime? LastCheckAt { get; set; }

    [AuditIgnore]
    public DateTime? LastSuccessAt { get; set; }

    [AuditIgnore]
    public int ConsecutiveFailures { get; set; }

    /// <summary>The first failed check of the current run of failures: "unreachable since".</summary>
    [AuditIgnore]
    public DateTime? FirstFailureAt { get; set; }

    [AuditIgnore]
    [MaxLength(500)]
    public string? LastError { get; set; }

    [AuditIgnore]
    public Guid? CurrentVersionId { get; set; }

    /// <summary>The connection of the last successful check (<see cref="CertificateConnection"/>).</summary>
    [AuditIgnore]
    [Column(TypeName = "jsonb")]
    public string? Connection { get; set; }

    /// <summary>The validation of the last successful check (<see cref="CertificateValidation"/>).</summary>
    [AuditIgnore]
    [Column(TypeName = "jsonb")]
    public string? Validation { get; set; }

    /// <summary>The version the last state event was about: it tells 286 from 285.</summary>
    [AuditIgnore]
    public Guid? AnnouncedVersionId { get; set; }

    [AuditIgnore]
    public int? LastCode { get; set; }

    [AuditIgnore]
    [MaxLength(300)]
    public string? LastVerdictKey { get; set; }

    // --- the baseline -----------------------------------------------------------------------------------------------------

    public Guid? AcceptedVersionId { get; set; }

    public DateTime? AcceptedAt { get; set; }

    /// <summary>"first check", "automatically (renewal)", or the person's name.</summary>
    [MaxLength(200)]
    public string? AcceptedBy { get; set; }

    /// <summary>What the person wrote when accepting (§8.4).</summary>
    [MaxLength(500)]
    public string? AcceptedNote { get; set; }

    [AuditIgnore]
    public DateTime Created { get; set; }

    public virtual List<CertificateSensorAuthorityEntity> Authorities { get; set; } = [];
}

/// <summary>One distinct leaf certificate a sensor has seen (docs/ssl-certificate-plan.md §4.2), by its SHA-256.</summary>
[Table("certificate_versions")]
[NotAudited("the certificates a check saw")]
public class CertificateVersionEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid SensorId { get; set; }

    /// <summary>Hex, upper case, of the DER: the Redmine and Zabbix pin format.</summary>
    [MaxLength(64)]
    public string Sha256 { get; set; } = "";

    /// <summary>Base64 SHA-256 of the SPKI, for the key rule.</summary>
    [MaxLength(64)]
    public string SpkiSha256 { get; set; } = "";

    [MaxLength(1000)]
    public string Subject { get; set; } = "";

    [MaxLength(1000)]
    public string? SubjectCommonName { get; set; }

    [MaxLength(1000)]
    public string Issuer { get; set; } = "";

    [MaxLength(1000)]
    public string? IssuerOrganization { get; set; }

    [MaxLength(1000)]
    public string? IssuerCommonName { get; set; }

    [MaxLength(128)]
    public string Serial { get; set; } = "";

    public DateTime NotBefore { get; set; }

    public DateTime NotAfter { get; set; }

    public List<string> Names { get; set; } = [];

    public bool SelfSigned { get; set; }

    /// <summary>Everything parsed (<see cref="CertificateFacts"/>).</summary>
    [Column(TypeName = "jsonb")]
    public string Facts { get; set; } = "{}";

    /// <summary>The chain as built at the last check that saw this leaf (<see cref="CertificateChainEntry"/>s).</summary>
    [Column(TypeName = "jsonb")]
    public string Chain { get; set; } = "[]";

    /// <summary>The leaf and the intermediates as the server sent them, at most 64 KB.</summary>
    public string ChainPem { get; set; } = "";

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }
}

/// <summary>
/// A private CA (docs/ssl-certificate-plan.md §4.5): one root and the intermediates under it, trusted by the sensors that
/// name it. Never edited: a new root is a new CA.
/// </summary>
[Table("certificate_authorities")]
[Audited]
public class CertificateAuthorityEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = "";

    [AuditPlain("a CA's public certificates")]
    public string Pem { get; set; } = "";

    [MaxLength(64)]
    public string RootSha256 { get; set; } = "";

    [MaxLength(1000)]
    public string RootSubject { get; set; } = "";

    public DateTime RootNotBefore { get; set; }

    public DateTime RootNotAfter { get; set; }

    public int IntermediateCount { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    public string? CreatedBy { get; set; }
}

/// <summary>Which private CAs a sensor in the private mode trusts.</summary>
[Table("certificate_sensor_authorities")]
[Audited]
public class CertificateSensorAuthorityEntity
{
    public Guid SensorId { get; set; }

    public Guid AuthorityId { get; set; }

    public virtual CertificateAuthorityEntity Authority { get; set; } = null!;
}
