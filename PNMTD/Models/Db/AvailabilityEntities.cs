using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

/// <summary>What an availability type means (docs/cookie-plan.md §8.2): checkboxes, several at once, never checked against each other.</summary>
public enum AvailabilityFlag { Vacation, Holiday, OnSite, WorkTravel, HomeOffice, Compensation }

/// <summary>
/// A type of availability (docs/cookie-plan.md §8.2): <i>ANWE</i>, <i>HOME</i>, <i>VACA</i> … with what it means as checkboxes.
/// Code reads the checkboxes, never the names. A type in use is retired, not deleted.
/// </summary>
[Table("availability_types")]
[NotAudited("the plan's settings; each save is in the audit log as AvailabilitySettingsChanged")]
public class AvailabilityTypeEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(60)]
    public string Name { get; set; } = "";

    /// <summary>Up to 4 letters, unique among the types in use.</summary>
    [MaxLength(4)]
    public string Code { get; set; } = "";

    /// <summary><c>#rrggbb</c>.</summary>
    [MaxLength(7)]
    public string Color { get; set; } = "#d9f2d9";

    public bool Vacation { get; set; }

    public bool Holiday { get; set; }

    public bool OnSite { get; set; }

    public bool WorkTravel { get; set; }

    public bool HomeOffice { get; set; }

    public bool Compensation { get; set; }

    /// <summary>The fill writes it on the days a person works (one type).</summary>
    public bool IsDefault { get; set; }

    /// <summary>The fill writes it on the days a person doesn't work (one type).</summary>
    public bool IsDayOff { get; set; }

    public int Order { get; set; }

    public bool Retired { get; set; }

    public bool Has(AvailabilityFlag flag) => flag switch
    {
        AvailabilityFlag.Vacation => Vacation,
        AvailabilityFlag.Holiday => Holiday,
        AvailabilityFlag.OnSite => OnSite,
        AvailabilityFlag.WorkTravel => WorkTravel,
        AvailabilityFlag.HomeOffice => HomeOffice,
        AvailabilityFlag.Compensation => Compensation,
        _ => false,
    };

    /// <summary>
    /// A person works on a day of this type (§8.2): on site, travelling or at home, and not on vacation, a holiday or
    /// compensation.
    /// </summary>
    public bool Works => (OnSite || WorkTravel || HomeOffice) && !(Vacation || Holiday || Compensation);
}

/// <summary>A person's availability on one day (docs/cookie-plan.md §8.2): one row per person and day.</summary>
[Table("availability_entries")]
[NotAudited("one cell of the plan; each save is in the audit log as AvailabilityChanged or AvailabilityFilled")]
public class AvailabilityEntryEntity
{
    public Guid UserId { get; set; }

    public DateOnly Date { get; set; }

    public Guid TypeId { get; set; }

    /// <summary>The fill wrote it: <i>Fill again</i> may replace it, a person's entry it never touches.</summary>
    public bool Filled { get; set; }

    public DateTime SetAt { get; set; }

    [MaxLength(200)]
    public string SetBy { get; set; } = "";
}

/// <summary>A day of the plan with something set (docs/cookie-plan.md §8.2): the responsible, the fallback, a comment.</summary>
[Table("availability_days")]
[NotAudited("one row of the plan; each save is in the audit log as AvailabilityChanged")]
public class AvailabilityDayEntity
{
    [Key]
    public DateOnly Date { get; set; }

    public Guid? ResponsibleId { get; set; }

    public Guid? FallbackId { get; set; }

    [MaxLength(500)]
    public string? Comment { get; set; }

    public DateTime ChangedAt { get; set; }

    [MaxLength(200)]
    public string ChangedBy { get; set; } = "";
}

/// <summary>The plan's settings (docs/cookie-plan.md §8.2, §9.4) as JSON, in one row: count columns, hours, holidays, the global fallback.</summary>
[Table("availability_settings")]
[NotAudited("the plan's settings; each save is in the audit log as AvailabilitySettingsChanged")]
public class AvailabilitySettingsEntity
{
    public const int Id = 1;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int SingletonId { get; set; } = Id;

    public string Json { get; set; } = "{}";

    public DateTime ChangedAt { get; set; }

    [MaxLength(200)]
    public string ChangedBy { get; set; } = "";
}
