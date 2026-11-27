using PNMTD.Models.Db;

namespace PNMTD.Api.V1.Contracts;

// Licences & services (docs/licences-plan.md §10.1). Every endpoint answers 404 licenses_disabled while the feature is off (§13.2).
// Money is per currency, never converted (§6.6). Days are yyyy-MM-dd.

/// <summary>An amount in one currency.</summary>
public sealed record MoneyAmount(string Currency, decimal Amount);

// --- categories (§3.1) ---------------------------------------------------------------------------------------------------

public sealed record LicenseCategoryItem(Guid Id, string Name, string? Description, int SortOrder, LicenseType? DefaultFor, int Items, int Templates,
    IReadOnlyList<MoneyAmount> PerYear);

public sealed class LicenseCategoryRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public LicenseType? DefaultFor { get; init; }
}

public sealed record LicenseCategoryOrderRequest(IReadOnlyList<Guid>? Ids);

// --- templates (§3.2) ----------------------------------------------------------------------------------------------------

public sealed record LicenseTemplateItem(
    Guid Id, string Name, LicenseType Type, Guid CategoryId, string CategoryName, string? Manufacturer, string ProductName, string? Edition, string? Vendor,
    string? ProductUrl, LicenseKind? Kind, int? Seats, string? SeatUnit, bool? SeatsFromHosts, decimal? PriceAmount, string? Currency,
    LicensePriceBasis? PriceBasis, LicenseBilling? Billing, int? BillingMonths, int? TermMonths, LicenseRenewal? Renewal, int? WarnDays, int? NoticeDays,
    bool? WarnBeforeRenewal, int? ReviewMonths, string? SupportContact, string? SupportUrl, string? Sla, DateOnly? EndOfLifeOn, string? EndOfLifeNote,
    string? Note, int Items, int FollowEndOfLife, DateTime CreatedAt, string? CreatedBy, DateTime UpdatedAt);

public sealed class LicenseTemplateRequest
{
    public string? Name { get; init; }

    public LicenseType Type { get; init; }

    public Guid? CategoryId { get; init; }

    public string? Manufacturer { get; init; }

    public string? ProductName { get; init; }

    public string? Edition { get; init; }

    public string? Vendor { get; init; }

    public string? ProductUrl { get; init; }

    public LicenseKind? Kind { get; init; }

    public int? Seats { get; init; }

    public string? SeatUnit { get; init; }

    public bool? SeatsFromHosts { get; init; }

    public decimal? PriceAmount { get; init; }

    public string? Currency { get; init; }

    public LicensePriceBasis? PriceBasis { get; init; }

    public LicenseBilling? Billing { get; init; }

    public int? BillingMonths { get; init; }

    public int? TermMonths { get; init; }

    public LicenseRenewal? Renewal { get; init; }

    public int? WarnDays { get; init; }

    public int? NoticeDays { get; init; }

    public bool? WarnBeforeRenewal { get; init; }

    public int? ReviewMonths { get; init; }

    public string? SupportContact { get; init; }

    public string? SupportUrl { get; init; }

    public string? Sla { get; init; }

    public DateOnly? EndOfLifeOn { get; init; }

    public string? EndOfLifeNote { get; init; }

    public string? Note { get; init; }
}

// --- items (§3.3) --------------------------------------------------------------------------------------------------------

/// <summary>A finding of the verdict (§4), worst first.</summary>
public sealed record LicenseFindingItem(int Code, string Text, DateOnly? On);

/// <summary>One row of the lists (§8.3): what the columns, the search and the filters need.</summary>
public sealed record LicenseRow(
    Guid Id, int Number, string Display, LicenseType Type, string Name, LicenseKind Kind, LicenseStatus Status, Guid CategoryId, string CategoryName,
    Guid? TemplateId, string? Manufacturer, string? ProductName, string? Edition, string? Vendor, string? LicensedTo, string? Reference,
    string? CustomerNumber, string? CostCenter, Guid? LocationId, string? LocationName, Guid? OwnerId, string? OwnerName, int? Seats, string SeatUnit,
    bool SeatsFromHosts, int UsedSeats, int Hosts, IReadOnlyList<string> HostNames, decimal? PriceAmount, string Currency, LicensePriceBasis PriceBasis,
    LicenseBilling Billing, int? BillingMonths, decimal PerMonth, decimal PerYear, DateOnly? StartsOn, DateOnly? ExpiresOn, int? TermMonths,
    LicenseRenewal Renewal, int? NoticeDays, DateOnly? CancelBy, DateOnly? EndOfLifeOn, DateOnly? ReviewedOn, DateOnly? ReviewDueOn, bool ReviewDue,
    int? VerdictCode, string? VerdictMessage, IReadOnlyList<LicenseFindingItem> Findings, bool NobodyNotified, string? LicenseKeyHint,
    string? ProductKeyHint, DateTime UpdatedAt);

/// <summary>Who hears about an item (§5.2, §8.7): never whether the owner turned the e-mails off.</summary>
public sealed record LicenseNotifying(bool HasOwner, bool OwnerHasEmail, int Hosts, int UnownedChoosers, bool Nobody);

/// <summary>An item in full (§8.7): everything but the keys, with their hints, the verdict, the findings and the costs in one line.</summary>
public sealed record LicenseDetail(
    Guid Id, int Number, string Display, LicenseType Type, string Name, Guid? TemplateId, string? TemplateName, Guid CategoryId, string CategoryName,
    string? Manufacturer, string? ProductName, string? Edition, string? Vendor, LicenseKind Kind, string? LicensedTo, string? Reference,
    string? CustomerNumber, string? PortalUrl, string? SupportContact, string? SupportUrl, string? Sla, string? CostCenter, Guid? LocationId,
    string? LocationName, Guid? OwnerId, string? OwnerName, bool HasLicenseKey, string? LicenseKeyHint, bool HasProductKey, string? ProductKeyHint,
    int? Seats, string SeatUnit, bool SeatsFromHosts, int UsedSeats, decimal? PriceAmount, string Currency, LicensePriceBasis PriceBasis,
    LicenseBilling Billing, int? BillingMonths, bool RecordCharges, DateOnly? ChargesFrom, DateOnly? StartsOn, DateOnly? ExpiresOn, int? TermMonths,
    LicenseRenewal Renewal, int WarnDays, int? NoticeDays, DateOnly? CancelBy, bool WarnBeforeRenewal, bool EndOfLifeFromTemplate,
    DateOnly? EndOfLifeOn, string? EndOfLifeNote, DateOnly? OwnEndOfLifeOn, string? OwnEndOfLifeNote, int EolWarnDays, DateOnly? ReviewedOn,
    string? ReviewedBy, string? ReviewNote, int? ReviewMonths, DateOnly? ReviewDueOn, bool ReviewDue, LicenseStatus Status, DateTime? StatusChangedAt,
    string? StatusChangedBy, string? Note, int? VerdictCode, string? VerdictMessage, DateTime? VerdictAt, IReadOnlyList<LicenseFindingItem> Findings,
    decimal? UnitPrice, decimal PerMonth, decimal PerYear, decimal SpentThisYear, int Hosts, int Links, int Renewals, int Charges, int Documents,
    LicenseNotifying Notifying, DateTime CreatedAt, string? CreatedBy, DateTime UpdatedAt);

/// <summary>
/// The whole form (§3.3). Keys: null keeps, <c>""</c> clears. <c>RecordPastCharges</c> records the billing periods since the start at
/// this price (§6.2); <c>AcknowledgeCredentials</c> saves a note that looks like it holds a key anyway (422 <c>looks_like_credential</c>).
/// </summary>
public sealed class LicenseRequest
{
    public LicenseType Type { get; init; }

    public string? Name { get; init; }

    public Guid? TemplateId { get; init; }

    public Guid? CategoryId { get; init; }

    public string? Manufacturer { get; init; }

    public string? ProductName { get; init; }

    public string? Edition { get; init; }

    public string? Vendor { get; init; }

    public LicenseKind Kind { get; init; } = LicenseKind.Other;

    public string? LicensedTo { get; init; }

    public string? Reference { get; init; }

    public string? CustomerNumber { get; init; }

    public string? PortalUrl { get; init; }

    public string? SupportContact { get; init; }

    public string? SupportUrl { get; init; }

    public string? Sla { get; init; }

    public string? CostCenter { get; init; }

    public Guid? LocationId { get; init; }

    public Guid? OwnerId { get; init; }

    public string? LicenseKey { get; init; }

    public string? ProductKey { get; init; }

    public int? Seats { get; init; }

    public string? SeatUnit { get; init; }

    public bool SeatsFromHosts { get; init; }

    public decimal? PriceAmount { get; init; }

    public string? Currency { get; init; }

    public LicensePriceBasis PriceBasis { get; init; }

    public LicenseBilling Billing { get; init; }

    public int? BillingMonths { get; init; }

    public bool? RecordCharges { get; init; }

    public DateOnly? StartsOn { get; init; }

    public DateOnly? ExpiresOn { get; init; }

    public int? TermMonths { get; init; }

    public LicenseRenewal Renewal { get; init; }

    public int? WarnDays { get; init; }

    public int? NoticeDays { get; init; }

    public bool WarnBeforeRenewal { get; init; }

    public bool EndOfLifeFromTemplate { get; init; }

    public DateOnly? EndOfLifeOn { get; init; }

    public string? EndOfLifeNote { get; init; }

    public int? EolWarnDays { get; init; }

    public DateOnly? ReviewedOn { get; init; }

    public int? ReviewMonths { get; init; }

    public string? Note { get; init; }

    public bool RecordPastCharges { get; init; }

    public bool AcknowledgeCredentials { get; init; }
}

public sealed record LicenseKeys(string? LicenseKey, string? ProductKey);

public sealed record LicenseReviewRequest(DateOnly? On, string? Note, IReadOnlyList<Guid>? Ids);

public sealed record LicenseCancelRequest(DateOnly? EndsOn);

public sealed record LicenseRetireRequest(bool Confirmed);

public sealed record LicenseBulkResult(int Changed);

// --- hosts and linked sensors (§5.1, §5.4) --------------------------------------------------------------------------------

public sealed record LicenseAssignmentItem(Guid HostId, string HostName, bool HostEnabled, Guid? LocationId, string? LocationName, Guid? SensorId,
    bool? SensorEnabled, bool? SensorIgnore, int? LastCode, string? LastMessage, int Seats, string? Note, DateTime AssignedAt, string? AssignedBy);

public sealed record LicenseAssignRequest(IReadOnlyList<Guid>? HostIds, int? Seats, string? Note);

public sealed record LicenseAssignmentRequest(int? Seats, string? Note);

public sealed record LicenseUnassignRequest(IReadOnlyList<Guid>? HostIds);

public sealed record LicenseSensorLinkItem(Guid SensorId, string SensorName, Guid HostId, string HostName, int? LastCode, string? LastMessage, string? Note,
    DateTime CreatedAt, string? CreatedBy);

public sealed record LicenseLinkRequest(IReadOnlyList<Guid>? SensorIds, string? Note);

/// <summary>A host or a sensor to assign or link (§10.1), found without Monitoring: View.</summary>
public sealed record LicenseTarget(Guid Id, string Name, string? HostName, string? LocationName, bool Assigned);

/// <summary>An item on a host's or a location's panel (§8.8).</summary>
public sealed record LicenseLinkedItem(Guid Id, int Number, string Display, LicenseType Type, string Name, int? VerdictCode, string? VerdictMessage,
    DateOnly? ExpiresOn, LicenseStatus Status, IReadOnlyList<MoneyAmount> PerMonth, string Via);

/// <summary>An item sensor's summary (§5.3), for Monitoring: View: no price, no keys, no note, no documents.</summary>
public sealed record LicenseSensorSummary(Guid Id, int Number, string Display, LicenseType Type, string Name, LicenseKind Kind, LicenseStatus Status,
    string? Manufacturer, string? ProductName, DateOnly? ExpiresOn, DateOnly? EndOfLifeOn, int UsedSeats, int? Seats, string SeatUnit, int AssignmentSeats);

/// <summary>An item linked to a sensor (§5.4): the support details where the outage is handled; no price, keys, note, reference or documents.</summary>
public sealed record LinkedItemSummary(Guid Id, int Number, string Display, LicenseType Type, string Name, string? Provider, string? SupportContact,
    string? SupportUrl, string? CustomerNumber, string? Sla, int? VerdictCode, string? Note);

// --- renewals, charges, documents (§3.7–§3.9) -----------------------------------------------------------------------------

public sealed record LicenseRenewalItem(Guid Id, LicenseRenewalKind Kind, DateOnly On, DateOnly? PreviousExpiresOn, DateOnly NewExpiresOn,
    decimal? PriceAmount, string? Currency, string? Reference, string? Note, DateTime CreatedAt, string? CreatedBy, IReadOnlyList<Guid> DocumentIds);

public sealed record LicenseRenewRequest(DateOnly? NewExpiresOn, decimal? PriceAmount, string? Currency, string? Reference, string? Note,
    bool Reactivate, decimal? ChargeAmount, DateOnly? ChargeOn);

public sealed record LicenseRenewalChangeRequest(string? Reference, string? Note);

public sealed record LicenseChargeItem(Guid Id, Guid LicenseId, int Number, string Display, LicenseType Type, string Name, LicenseChargeKind Kind,
    LicenseChargeSource Source, DateOnly On, DateOnly? PeriodFrom, DateOnly? PeriodTo, decimal Amount, string Currency, bool Estimated,
    string? Reference, string? Note, DateTime CreatedAt, string? CreatedBy, DateTime? UpdatedAt, string? UpdatedBy, Guid? InvoiceFileId,
    string? InvoiceName);

/// <summary>A charge by hand, or a correction (§6.3): a credit's amount is given positive and stored negative.</summary>
public sealed class LicenseChargeRequest
{
    public LicenseChargeKind Kind { get; init; } = LicenseChargeKind.Extra;

    public DateOnly? On { get; init; }

    public DateOnly? PeriodFrom { get; init; }

    public DateOnly? PeriodTo { get; init; }

    public decimal? Amount { get; init; }

    public string? Currency { get; init; }

    public string? Reference { get; init; }

    public string? Note { get; init; }

    public bool? Estimated { get; init; }
}

public sealed class LicenseChargesQuery
{
    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public LicenseType? Type { get; init; }

    public Guid? Category { get; init; }

    public Guid? License { get; init; }

    public LicenseChargeKind? Kind { get; init; }

    public LicenseChargeSource? Source { get; init; }

    public bool? Estimated { get; init; }

    public string? Currency { get; init; }
}

public sealed record LicenseDocumentItem(Guid FileId, string Name, LicenseDocumentKind Kind, string? Description, long Size, Guid? ChargeId,
    Guid? RenewalId, DateTime AddedAt, string? AddedBy);

public sealed record LicenseDocumentChangeRequest(LicenseDocumentKind? Kind, string? Description, Guid? ChargeId, Guid? RenewalId);

// --- names (§8.6) ----------------------------------------------------------------------------------------------------------

public sealed record LicenseNameUse(string Name, int Items, int Templates);

public sealed record LicenseNames(IReadOnlyList<LicenseNameUse> Manufacturers, IReadOnlyList<LicenseNameUse> Vendors,
    IReadOnlyList<LicenseNameUse> CostCenters, IReadOnlyList<LicenseNameUse> SeatUnits);

public sealed record LicenseRenameRequest(string? Field, string? From, string? To);

public sealed record LicenseRenameResult(int Items, int Templates);

public sealed record LicensePerson(Guid Id, string Name, string Username);

// --- the overview, the costs, upcoming (§8.2, §8.4, §8.5) -----------------------------------------------------------------

/// <summary>The filters every money view shares (§8.2): kept in the URL.</summary>
public sealed class LicenseMoneyFilter
{
    public string? Currency { get; init; }

    public LicenseType? Type { get; init; }

    public Guid? Category { get; init; }

    public string? CostCenter { get; init; }

    public Guid? Location { get; init; }

    public Guid? Owner { get; init; }
}

public sealed record LicenseMonth(string Month, decimal Spent, decimal Forecast, decimal IfRenewed, IReadOnlyList<MoneyAmount> TopCategories);

public sealed record LicenseBar(string Key, string Label, decimal Amount, int Items);

public sealed record LicenseAttention(Guid Id, int Number, string Display, LicenseType Type, string Name, int Code, string Text, DateOnly? On);

public sealed record LicenseSeatMeter(Guid Id, int Number, string Display, string Name, int Used, int Seats, string Unit);

public sealed record LicenseWorthALook(int UnusedSeats, decimal UnusedSeatsPerYear, int UnusedSeatItems, int PaidPastEndOfLife, int NobodyNotified,
    int EstimatesWithoutInvoice, int WithoutPrice, int ReviewsOverdue);

public sealed record LicenseEndOfLife(Guid Id, int Number, string Display, string Name, DateOnly On, int Hosts);

public sealed record LicenseOverview(
    string Currency, IReadOnlyList<string> Currencies, IReadOnlyList<MoneyAmount> PerMonth, IReadOnlyList<MoneyAmount> PerYear,
    IReadOnlyList<MoneyAmount> Next12Months, IReadOnlyList<MoneyAmount> Next12MonthsIfRenewed, IReadOnlyList<MoneyAmount> SpentThisYear,
    IReadOnlyList<MoneyAmount> SpentLastYear, int Year, int NeedsAttention, int Failing, int CancelWithin90Days, DateOnly? NextCancelBy,
    IReadOnlyList<LicenseMonth> Months, IReadOnlyList<LicenseBar> ByCategory, IReadOnlyList<LicenseBar> ByProvider,
    IReadOnlyList<LicenseAttention> Attention, int AttentionTotal, IReadOnlyList<LicenseSeatMeter> Seats, LicenseWorthALook WorthALook,
    IReadOnlyList<LicenseEndOfLife> EndsOfLife, int Items);

public sealed class LicenseCostsQuery
{
    /// <summary>The first month, <c>yyyy-MM</c>; with <c>To</c>, the window. Default: this year.</summary>
    public string? From { get; init; }

    public string? To { get; init; }

    /// <summary>category, type, provider, manufacturer, costCenter, location, host, owner or item.</summary>
    public string? Group { get; init; }

    /// <summary>spent, forecast or both (the default).</summary>
    public string? Show { get; init; }

    /// <summary>The last five years as columns instead of months.</summary>
    public bool Years { get; init; }

    public string? Currency { get; init; }

    public LicenseType? Type { get; init; }

    public Guid? Category { get; init; }

    public string? CostCenter { get; init; }

    public Guid? Location { get; init; }

    public Guid? Owner { get; init; }
}

public sealed record LicenseCostLine(string Key, string Label, Guid? ItemId, string? Display, IReadOnlyList<decimal> Values, decimal Total, decimal PerYearNow,
    IReadOnlyList<LicenseCostLine> Items);

public sealed record LicenseCosts(string Currency, IReadOnlyList<string> Currencies, string Group, string Show, IReadOnlyList<string> Columns,
    IReadOnlyList<bool> Forecast, IReadOnlyList<LicenseCostLine> Groups, IReadOnlyList<decimal> Totals, decimal Total, decimal PerYearNow,
    decimal IfRenewed, IReadOnlyList<MoneyAmount> OtherCurrencies, bool SharedByHosts);

public sealed record LicenseUpcomingEntry(Guid Id, int Number, string Display, LicenseType Type, string Name, DateOnly Day, string What,
    LicenseRenewal Renewal, decimal? Price, string? Currency, int Hosts, string? OwnerName, bool NobodyNotified, int? VerdictCode);

public sealed record LicenseUpcomingMonth(string Month, IReadOnlyList<MoneyAmount> Cost, IReadOnlyList<LicenseUpcomingEntry> Entries);

public sealed record LicenseUpcoming(int Months, IReadOnlyList<MoneyAmount> Cost, IReadOnlyList<MoneyAmount> IfRenewed,
    IReadOnlyList<LicenseUpcomingEntry> Overdue, IReadOnlyList<LicenseUpcomingMonth> ByMonth);

/// <summary>
/// A Windows host's activation matched to the items (§8.11): its key's last five characters and channel, and the items with that hint.
/// A volume key for KMS (GVLK) is the same on every machine, so it matches nothing.
/// </summary>
public sealed record LicenseActivation(Guid HostId, string HostName, string? PartialKey, string? Channel, bool KmsClientKey, bool Assigned,
    IReadOnlyList<LicenseActivationMatch> Matches);

public sealed record LicenseActivationMatch(Guid Id, int Number, string Display, string Name);

/// <summary>A licence in the global search (§8.10).</summary>
public sealed record SearchLicense(Guid Id, int Number, string Display, LicenseType Type, string Name, string? Product, int? VerdictCode);
