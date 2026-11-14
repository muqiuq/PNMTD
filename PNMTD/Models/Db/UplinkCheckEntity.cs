using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>How an uplink check ended (docs/ai-operations-plan.md §7.2).</summary>
public enum UplinkOutcome : short
{
    Ok = 0,
    HttpStatus = 1,
    Timeout = 2,
    ConnectError = 3,
    Dns = 4,
    Tls = 5,
    BadAnswer = 6,
    BadSignature = 7,
}

/// <summary>One uplink check (docs/ai-operations-plan.md §7.2): kept 30 days.</summary>
[Table("uplink_checks")]
[NotAudited("the uplink's checks")]
public class UplinkCheckEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateTime At { get; set; }

    /// <summary>1 to 3: UplinkCheck:Url1..3.</summary>
    public short Endpoint { get; set; }

    [MaxLength(255)]
    public string EndpointHost { get; set; } = "";

    public UplinkOutcome Outcome { get; set; }

    public int? HttpStatus { get; set; }

    public int? LatencyMs { get; set; }

    [MaxLength(500)]
    public string? Error { get; set; }
}
