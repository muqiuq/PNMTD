using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>How bad a release's fixes are (docs/ai-operations-plan.md §10.3). Only Critical alerts.</summary>
public enum ReleaseSeverity : short
{
    Normal = 0,
    Important = 1,
    Critical = 2,
}

/// <summary>A RouterOS release from MikroTik's feed (§10.2), with its assessment. Kept 2 years.</summary>
[Table("mikrotik_releases")]
[NotAudited("RouterOS releases, fetched")]
public class MikrotikReleaseEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(32)]
    public string Version { get; set; } = "";

    [MaxLength(32)]
    public string Channel { get; set; } = "";

    public DateTime? PublishedAt { get; set; }

    public string Changelog { get; set; } = "";

    public DateTime FirstSeenAt { get; set; }

    public ReleaseSeverity Severity { get; set; }

    public bool ZeroDay { get; set; }

    public bool Exploited { get; set; }

    public bool RemoteWithoutCredentials { get; set; }

    public List<string> Cves { get; set; } = [];

    [MaxLength(1000)]
    public string? Summary { get; set; }

    /// <summary>code, ai, verified (the AI's critical, confirmed), or disputed (downgraded).</summary>
    [MaxLength(16)]
    public string AssessedBy { get; set; } = "code";

    public DateTime? AssessedAt { get; set; }

    public Guid? WorkId { get; set; }
}
