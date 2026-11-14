using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>An interface's availability in Zabbix: its worst one for a host.</summary>
public enum ZabbixAvailability { Unknown = 0, Available = 1, Unavailable = 2 }

/// <summary>
/// The mirror of a Zabbix host (docs/audit-redmine-zabbix-plan.md §7.3): what browsing, searching and matching need, so
/// thousands of hosts list fast and keep their names while Zabbix doesn't answer. A host Zabbix no longer returns is
/// marked gone, and removed a week later.
/// </summary>
[Table("zabbix_hosts")]
[NotAudited("a mirror of Zabbix's hosts, refreshed by the host sync")]
public class ZabbixHostEntity
{
    /// <summary>Zabbix's hostid.</summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long HostId { get; set; }

    /// <summary>Zabbix's "host", the technical name.</summary>
    [MaxLength(255)]
    public string TechnicalName { get; set; } = "";

    /// <summary>Zabbix's "name", the visible one.</summary>
    [MaxLength(255)]
    public string VisibleName { get; set; } = "";

    /// <summary>Status 0: monitored; 1 is disabled.</summary>
    public bool Monitored { get; set; }

    public bool InMaintenance { get; set; }

    /// <summary>The host groups' names.</summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>The interfaces' IPs and DNS names, lower case: for the search and the matching to PNMT's hosts.</summary>
    public List<string> Addresses { get; set; } = [];

    /// <summary>The interfaces as JSON: type, address, port, availability, error.</summary>
    [Column(TypeName = "jsonb")]
    public string Interfaces { get; set; } = "[]";

    /// <summary>The worst of its interfaces'.</summary>
    public ZabbixAvailability Availability { get; set; }

    public DateTime SyncedAt { get; set; }

    /// <summary>When Zabbix stopped returning it (deleted, or out of the token's groups).</summary>
    public DateTime? GoneAt { get; set; }
}
