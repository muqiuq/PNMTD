using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>
/// A Redmine ticket linked to a knowledge base page, a location, a host or a sensor (docs/audit-redmine-zabbix-plan.md
/// §6.5): exactly one of them, removed with it. No foreign key to the ticket, which lives in Redmine.
/// </summary>
[Table("redmine_links")]
[Audited]
public class RedmineLinkEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Redmine's number.</summary>
    public int IssueId { get; set; }

    public Guid? PageId { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? HostId { get; set; }

    public Guid? SensorId { get; set; }

    /// <summary>Why it is linked: "the UPS replacement".</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [AuditIgnore]
    public Guid? CreatedById { get; set; }

    [AuditIgnore]
    [MaxLength(200)]
    public string? CreatedByName { get; set; }
}

/// <summary>
/// PNMT's copy of a linked ticket's header (§6.7): what panels, chips and the search show without asking Redmine. Never
/// the description, comments or attachments; deleted once no link names it.
/// </summary>
[Table("redmine_issues")]
[NotAudited("a copy of Redmine's tickets, refreshed by the sync")]
public class RedmineIssueEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int IssueId { get; set; }

    public int ProjectId { get; set; }

    [MaxLength(200)]
    public string ProjectName { get; set; } = "";

    [MaxLength(100)]
    public string? TrackerName { get; set; }

    [MaxLength(100)]
    public string StatusName { get; set; } = "";

    public bool IsClosed { get; set; }

    public int? PriorityId { get; set; }

    [MaxLength(100)]
    public string? PriorityName { get; set; }

    [MaxLength(255)]
    public string Subject { get; set; } = "";

    [MaxLength(200)]
    public string? AssignedToName { get; set; }

    public DateTime UpdatedOn { get; set; }

    [MaxLength(10)]
    public string? DueDate { get; set; }

    public DateTime FetchedAt { get; set; }

    /// <summary>Redmine no longer returns it: deleted, or out of PNMT's projects or the account's sight.</summary>
    public bool Missing { get; set; }
}
