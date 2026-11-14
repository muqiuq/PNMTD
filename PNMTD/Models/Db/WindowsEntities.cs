using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

// What stays Windows' own after L1 (docs/linux-sensor-plan.md §3.3): the shared tables are in AgentEntities.cs.

/// <summary>
/// A Windows run-as account (docs/windows-sensor-plan.md §7.4): the service itself, an account whose password is stored on the server (entered there; PNMT knows the
/// name only), one whose password is typed per run, or a gMSA. No password is ever stored in PNMT.
/// </summary>
[Table("windows_run_as")]
[Audited]
public class WindowsRunAsEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    public Lib.Agent.RunAsKind Kind { get; set; }

    /// <summary>DOMAIN\user, or the gMSA with its $; null for the service.</summary>
    [MaxLength(256)]
    public string? Account { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; }
}
