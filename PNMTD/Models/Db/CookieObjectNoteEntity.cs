using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>
/// A note of Cookie's on a host or a sensor (docs/cookie-object-notes-plan.md §3.1): one row per note, exactly one of
/// <see cref="HostId"/> and <see cref="SensorId"/> set, gone with its object. Shown on the object's page to readers with the
/// note's areas; removed by a person with <i>Monitoring: Edit</i>. Never the people's notes.
/// </summary>
[Table("cookie_object_notes")]
[NotAudited("Cookie's own; adding and removing are in the audit log as CookieNoteAdded and CookieNoteRemoved")]
public class CookieObjectNoteEntity
{
    [Key]
    public long Id { get; set; }

    public Guid? HostId { get; set; }

    public Guid? SensorId { get; set; }

    public DateTime At { get; set; }

    /// <summary>As written, unmasked; without a date prefix, which the row carries.</summary>
    [MaxLength(1500)]
    public string Text { get; set; } = "";

    public Guid? CaseId { get; set; }

    /// <summary>Its row in the decision log; null once the log is pruned.</summary>
    public long? DecisionId { get; set; }

    /// <summary>The case's areas when it was written (docs/cookie-plan.md §9.2): a reader needs them all.</summary>
    public List<string> Areas { get; set; } = [];
}
