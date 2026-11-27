using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PNMTD.Models.Db;

// Licences & services (docs/licences-plan.md §3): everything IT pays for, with its hosts, sensors, renewals, charges and documents.
// In code every item is a "license", told apart by its type (decision 2). The enums are stored by name.

/// <summary>A licence, a service, or a piece of hardware (§Words).</summary>
public enum LicenseType { License, Service, Hardware }

/// <summary>What an item is, per type (§3.3): each brings its defaults and its words.</summary>
public enum LicenseKind { Perpetual, Subscription, Contract, Support, Usage, OneOff, Purchase, Lease, Trial, Free, Other }

/// <summary>The amount is the whole item's, or each seat's (§6.1).</summary>
public enum LicensePriceBasis { Total, PerSeat }

/// <summary>Once, per billing period, or per billing period as an estimate (§3.3).</summary>
public enum LicenseBilling { OneTime, Recurring, Variable }

/// <summary>It ends, a person renews it, or the vendor renews it unless cancelled (§5.6).</summary>
public enum LicenseRenewal { None, Manual, Automatic }

/// <summary>Its life (§5.8): in use; cancelled, running to its expiry; retired, kept for the record.</summary>
public enum LicenseStatus { Active, Cancelled, Retired }

public enum LicenseRenewalKind { Renewal, AutomaticRenewal }

public enum LicenseChargeKind { Purchase, Period, Extra, Credit }

public enum LicenseChargeSource { Automatic, Person }

public enum LicenseDocumentKind { Invoice, Contract, Quote, LicenseFile, KeyFile, Warranty, Other }

/// <summary>What the money is for (§3.1): a short list an Admin keeps; every item and template is in one.</summary>
[Table("license_categories")]
[Audited]
public class LicenseCategoryEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(60)]
    public string Name { get; set; } = "";

    [MaxLength(60)]
    [AuditIgnore]
    public string NormalizedName { get; set; } = "";

    [MaxLength(200)]
    public string? Description { get; set; }

    public int SortOrder { get; set; }

    /// <summary>The category a new item of this type starts in without a template; unique where set.</summary>
    public LicenseType? DefaultFor { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [AuditIgnore]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A product or service as PNMT knows it (§3.2): what a new item copies, and the end of life its items follow.</summary>
[Table("license_templates")]
[Audited]
public class LicenseTemplateEntity
{
    [Key]
    public Guid Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(200)]
    [AuditIgnore]
    public string NormalizedName { get; set; } = "";

    public LicenseType Type { get; set; }

    public Guid CategoryId { get; set; }

    [MaxLength(100)]
    public string? Manufacturer { get; set; }

    [MaxLength(200)]
    public string ProductName { get; set; } = "";

    [MaxLength(100)]
    public string? Edition { get; set; }

    [MaxLength(100)]
    public string? Vendor { get; set; }

    [MaxLength(500)]
    public string? ProductUrl { get; set; }

    // --- the defaults a new item starts with (null: no default) --------------------------------------------------------

    public LicenseKind? Kind { get; set; }

    public int? Seats { get; set; }

    [MaxLength(30)]
    public string? SeatUnit { get; set; }

    public bool? SeatsFromHosts { get; set; }

    [Column(TypeName = "numeric(12,2)")]
    public decimal? PriceAmount { get; set; }

    [MaxLength(3)]
    public string? Currency { get; set; }

    public LicensePriceBasis? PriceBasis { get; set; }

    public LicenseBilling? Billing { get; set; }

    public int? BillingMonths { get; set; }

    public int? TermMonths { get; set; }

    public LicenseRenewal? Renewal { get; set; }

    public int? WarnDays { get; set; }

    public int? NoticeDays { get; set; }

    public bool? WarnBeforeRenewal { get; set; }

    public int? ReviewMonths { get; set; }

    [MaxLength(500)]
    public string? SupportContact { get; set; }

    [MaxLength(500)]
    public string? SupportUrl { get; set; }

    [MaxLength(200)]
    public string? Sla { get; set; }

    // --- the end of life, followed by its items -------------------------------------------------------------------------

    public DateOnly? EndOfLifeOn { get; set; }

    [MaxLength(200)]
    public string? EndOfLifeNote { get; set; }

    [MaxLength(2000)]
    public string? Note { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedBy { get; set; }

    [AuditIgnore]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>An item (§3.3): a licence, a service or hardware, on any number of hosts or on none.</summary>
[Table("licenses")]
[Audited]
public class LicenseEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>LIC-12, SVC-31, HW-40: one identity column for all three types, never reused (§3.3).</summary>
    [AuditIgnore]
    public int Number { get; set; }

    public LicenseType Type { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    public Guid? TemplateId { get; set; }

    public Guid CategoryId { get; set; }

    // --- the product or service ------------------------------------------------------------------------------------------

    [MaxLength(100)]
    public string? Manufacturer { get; set; }

    [MaxLength(200)]
    public string? ProductName { get; set; }

    [MaxLength(100)]
    public string? Edition { get; set; }

    [MaxLength(100)]
    public string? Vendor { get; set; }

    public LicenseKind Kind { get; set; } = LicenseKind.Other;

    [MaxLength(200)]
    public string? LicensedTo { get; set; }

    [MaxLength(200)]
    public string? Reference { get; set; }

    [MaxLength(100)]
    public string? CustomerNumber { get; set; }

    [MaxLength(500)]
    public string? PortalUrl { get; set; }

    // --- the support -----------------------------------------------------------------------------------------------------

    [MaxLength(500)]
    public string? SupportContact { get; set; }

    [MaxLength(500)]
    public string? SupportUrl { get; set; }

    [MaxLength(200)]
    public string? Sla { get; set; }

    // --- where it belongs and who looks after it -----------------------------------------------------------------------

    [MaxLength(100)]
    public string? CostCenter { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? OwnerId { get; set; }

    // --- the keys (§3.4): encrypted, never logged ------------------------------------------------------------------------

    [AuditSecret]
    public string? LicenseKeyProtected { get; set; }

    [MaxLength(5)]
    [AuditPlain("the last five characters, to find a key and match a host's activation")]
    public string? LicenseKeyHint { get; set; }

    [AuditSecret]
    public string? ProductKeyProtected { get; set; }

    [MaxLength(5)]
    [AuditPlain("the last five characters, to find a key and match a host's activation")]
    public string? ProductKeyHint { get; set; }

    // --- seats -----------------------------------------------------------------------------------------------------------

    public int? Seats { get; set; }

    [MaxLength(30)]
    public string SeatUnit { get; set; } = "devices";

    public bool SeatsFromHosts { get; set; }

    // --- the price (§6) ----------------------------------------------------------------------------------------------------

    [Column(TypeName = "numeric(12,2)")]
    public decimal? PriceAmount { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    public LicensePriceBasis PriceBasis { get; set; }

    public LicenseBilling Billing { get; set; }

    public int? BillingMonths { get; set; }

    public bool RecordCharges { get; set; } = true;

    public DateOnly? ChargesFrom { get; set; }

    // --- the term ----------------------------------------------------------------------------------------------------------

    public DateOnly? StartsOn { get; set; }

    public DateOnly? ExpiresOn { get; set; }

    public int? TermMonths { get; set; }

    public LicenseRenewal Renewal { get; set; }

    public int WarnDays { get; set; } = 30;

    public int? NoticeDays { get; set; }

    public bool WarnBeforeRenewal { get; set; }

    // --- the end of life ---------------------------------------------------------------------------------------------------

    public bool EndOfLifeFromTemplate { get; set; }

    public DateOnly? EndOfLifeOn { get; set; }

    [MaxLength(200)]
    public string? EndOfLifeNote { get; set; }

    public int EolWarnDays { get; set; } = 90;

    // --- the review --------------------------------------------------------------------------------------------------------

    public DateOnly? ReviewedOn { get; set; }

    [MaxLength(200)]
    public string? ReviewedBy { get; set; }

    [MaxLength(500)]
    public string? ReviewNote { get; set; }

    public int? ReviewMonths { get; set; }

    // --- its life ----------------------------------------------------------------------------------------------------------

    public LicenseStatus Status { get; set; }

    [AuditIgnore]
    public DateTime? StatusChangedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? StatusChangedBy { get; set; }

    [MaxLength(10000)]
    [AuditText]
    public string? Note { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedBy { get; set; }

    [AuditIgnore]
    public DateTime UpdatedAt { get; set; }

    // --- its state, which the evaluation keeps (§3.3, §5.5) -------------------------------------------------------------

    [AuditIgnore]
    public int? VerdictCode { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? VerdictKey { get; set; }

    [MaxLength(1000)]
    [AuditIgnore]
    public string? VerdictMessage { get; set; }

    [AuditIgnore]
    public DateTime? VerdictAt { get; set; }

    [AuditIgnore]
    public DateOnly? AnnouncedExpiresOn { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? NoticeKey { get; set; }

    [AuditIgnore]
    public DateOnly? ReviewNoticeOn { get; set; }

    [AuditIgnore]
    public DateOnly? AutoRenewedThrough { get; set; }
}

/// <summary>An item on a host (§3.5), with its host sensor; the sensor is null only while the feature is off (§13.3).</summary>
[Table("license_assignments")]
[Audited]
public class LicenseAssignmentEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }

    public Guid HostId { get; set; }

    public Guid? SensorId { get; set; }

    public int Seats { get; set; } = 1;

    [MaxLength(500)]
    public string? Note { get; set; }

    [AuditIgnore]
    public DateTime AssignedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? AssignedBy { get; set; }

    /// <summary>The verdict key the sensor last showed: an event when it differs from the item's.</summary>
    [MaxLength(200)]
    [AuditIgnore]
    public string? SensorKey { get; set; }
}

/// <summary>An existing sensor that watches a service (§3.6): the support details show on its page.</summary>
[Table("license_sensor_links")]
[Audited]
public class LicenseSensorLinkEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }

    public Guid SensorId { get; set; }

    [MaxLength(200)]
    public string? Note { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedBy { get; set; }
}

/// <summary>One renewal (§3.7): the term's history; the money is in the charges.</summary>
[Table("license_renewals")]
[Audited]
public class LicenseRenewalEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }

    public LicenseRenewalKind Kind { get; set; }

    public DateOnly On { get; set; }

    public DateOnly? PreviousExpiresOn { get; set; }

    public DateOnly NewExpiresOn { get; set; }

    [Column(TypeName = "numeric(12,2)")]
    public decimal? PriceAmount { get; set; }

    [MaxLength(3)]
    public string? Currency { get; set; }

    [MaxLength(200)]
    public string? Reference { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedBy { get; set; }
}

/// <summary>One recorded cost (§3.8): what <i>spent</i> adds up. PNMT's own aren't audited one by one (§6.2).</summary>
[Table("license_charges")]
[Audited]
public class LicenseChargeEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }

    public LicenseChargeKind Kind { get; set; }

    public LicenseChargeSource Source { get; set; }

    public DateOnly On { get; set; }

    public DateOnly? PeriodFrom { get; set; }

    public DateOnly? PeriodTo { get; set; }

    /// <summary>Signed: a credit is negative.</summary>
    [Column(TypeName = "numeric(12,2)")]
    public decimal Amount { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "EUR";

    public bool Estimated { get; set; }

    [MaxLength(200)]
    public string? Reference { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }

    [AuditIgnore]
    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? CreatedBy { get; set; }

    [AuditIgnore]
    public DateTime? UpdatedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? UpdatedBy { get; set; }
}

/// <summary>A document of an item (§3.9): a file in the files folder, served only through the item (§7.2).</summary>
[Table("license_documents")]
[Audited]
public class LicenseDocumentEntity
{
    [Key]
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }

    public Guid FileId { get; set; }

    public LicenseDocumentKind Kind { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }

    public Guid? ChargeId { get; set; }

    public Guid? RenewalId { get; set; }

    [AuditIgnore]
    public DateTime AddedAt { get; set; }

    [MaxLength(200)]
    [AuditIgnore]
    public string? AddedBy { get; set; }
}
