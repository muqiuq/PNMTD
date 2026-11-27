using PNMTD.Licenses;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Licenses;

/// <summary>
/// The cost maths (docs/licences-plan.md §6), pure: per month and per year, what counts, the billing days stepped from the anchor, the
/// charges PNMT records, the forecast, and shares that add up to the cent.
/// </summary>
[TestClass]
public sealed class CostMathTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    private static CostItem Item(decimal? price = 120m, LicenseBilling billing = LicenseBilling.Recurring, int? billingMonths = 12, DateOnly? starts = null,
        DateOnly? expires = null, int? term = null, LicenseRenewal renewal = LicenseRenewal.None, LicenseStatus status = LicenseStatus.Active,
        LicensePriceBasis basis = LicensePriceBasis.Total, int? seats = null, DateOnly? retired = null, string currency = "EUR") =>
        new(Guid.NewGuid(), status, price, currency, basis, seats, billing, billingMonths, starts ?? new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1),
            expires, term, renewal, retired);

    [TestMethod]
    public void PerMonthAndPerYearForEveryBilling()
    {
        Assert.AreEqual(10m, CostMath.PerMonth(Item(120m, billingMonths: 12), Today));
        Assert.AreEqual(120m, CostMath.PerYear(Item(120m, billingMonths: 12), Today));
        Assert.AreEqual(312.5m, CostMath.PerMonth(Item(12.5m, billingMonths: 1, basis: LicensePriceBasis.PerSeat, seats: 25), Today), "25 users at 12.50");
        Assert.AreEqual(0m, CostMath.PerMonth(Item(900m, LicenseBilling.OneTime, null), Today), "a one-time price runs nothing");
        Assert.AreEqual(1100m, CostMath.PerMonth(Item(1100m, LicenseBilling.Variable, 1), Today), "a variable price is its estimate");
        Assert.IsNull(CostMath.UnitPrice(Item(10m, basis: LicensePriceBasis.PerSeat, seats: null)), "per seat without seats is unknown");
    }

    [TestMethod]
    public void WhatCounts()
    {
        Assert.AreEqual(0m, CostMath.PerMonth(Item(expires: Today.AddDays(-1), renewal: LicenseRenewal.Manual), Today), "expired, waiting for a renewal by hand");
        Assert.AreEqual(10m, CostMath.PerMonth(Item(expires: Today.AddDays(-1), term: 12, renewal: LicenseRenewal.Automatic), Today), "renews by itself");
        Assert.AreEqual(0m, CostMath.PerMonth(Item(status: LicenseStatus.Retired), Today));
        Assert.AreEqual(10m, CostMath.PerMonth(Item(expires: Today.AddDays(10), status: LicenseStatus.Cancelled), Today), "cancelled runs to its end");
    }

    [TestMethod]
    public void TheBillingDaysStepFromTheTermsFirstDay()
    {
        // A 36-month contract ending on 30 Jun 2029, billed monthly: billed on the 1st.
        var contract = Item(990m, billingMonths: 1, starts: new DateOnly(2026, 7, 1), expires: new DateOnly(2029, 6, 30), term: 36);
        Assert.AreEqual(new DateOnly(2026, 7, 1), CostMath.Anchor(contract));
        CollectionAssert.AreEqual(new[] { new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 1) },
            CostMath.BillingDays(contract, new DateOnly(2026, 10, 15), new DateOnly(2027, 1, 15)).ToArray());
        // From 31 Jan: 28 Feb, then 31 Mar, never the 28th for ever after.
        var monthEnd = Item(10m, billingMonths: 1, starts: new DateOnly(2026, 1, 31));
        CollectionAssert.AreEqual(new[] { new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 30) },
            CostMath.BillingDays(monthEnd, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 30)).ToArray());
        var leap = Item(10m, billingMonths: 12, starts: new DateOnly(2024, 2, 29));
        CollectionAssert.AreEqual(new[] { new DateOnly(2025, 2, 28), new DateOnly(2026, 2, 28), new DateOnly(2028, 2, 29) },
            CostMath.BillingDays(leap, new DateOnly(2025, 1, 1), new DateOnly(2028, 12, 31)).Where(d => d.Year != 2027).ToArray());
        Assert.AreEqual(new DateOnly(2026, 12, 1), CostMath.NextBillingDay(contract, new DateOnly(2026, 11, 1)));
    }

    [TestMethod]
    public void TheChargesPnmtRecords()
    {
        var monthly = Item(100m, billingMonths: 1, starts: new DateOnly(2026, 9, 15));
        var due = CostMath.DueCharges(monthly, new DateOnly(2026, 9, 15), Today);
        CollectionAssert.AreEqual(new[] { new DateOnly(2026, 9, 15), new DateOnly(2026, 10, 15), new DateOnly(2026, 11, 15) }, due.Select(d => d.Day).ToArray());
        Assert.AreEqual(new DateOnly(2026, 10, 14), due[0].PeriodTo);
        Assert.AreEqual(2, CostMath.DueCharges(monthly, new DateOnly(2026, 9, 15), Today, max: 2).Count, "at most so many per run");
        var ended = Item(100m, billingMonths: 1, starts: new DateOnly(2026, 9, 15), expires: new DateOnly(2026, 10, 31));
        Assert.AreEqual(2, CostMath.DueCharges(ended, new DateOnly(2026, 9, 1), Today).Count, "none after the expiry");
        var retired = Item(100m, billingMonths: 1, starts: new DateOnly(2026, 9, 15), retired: new DateOnly(2026, 10, 15));
        Assert.AreEqual(1, CostMath.DueCharges(retired, new DateOnly(2026, 9, 1), Today).Count, "none from the retirement on");
        Assert.AreEqual(0, CostMath.DueCharges(Item(900m, LicenseBilling.OneTime, null), new DateOnly(2026, 1, 1), Today).Count);
    }

    [TestMethod]
    public void TheForecast()
    {
        var auto = Item(100m, billingMonths: 1, starts: new DateOnly(2026, 1, 1), expires: new DateOnly(2026, 12, 31), term: 12, renewal: LicenseRenewal.Automatic);
        var manual = auto with { Id = Guid.NewGuid(), Renewal = LicenseRenewal.Manual };
        var none = auto with { Id = Guid.NewGuid(), Renewal = LicenseRenewal.None };
        var later = Item(500m, LicenseBilling.OneTime, null, starts: new DateOnly(2027, 2, 1));
        var gone = Item(100m, billingMonths: 1, status: LicenseStatus.Retired);
        var entries = CostMath.Forecast([auto, manual, none, later, gone], Today, new DateOnly(2027, 2, 28), Today);
        Assert.IsTrue(entries.All(e => e.Day > Today), "never today or before");
        Assert.AreEqual(3, entries.Count(e => e.ItemId == auto.Id && !e.IfRenewed), "1 Dec, 1 Jan, 1 Feb");
        Assert.AreEqual(1, entries.Count(e => e.ItemId == manual.Id && !e.IfRenewed), "December, within the term");
        Assert.AreEqual(2, entries.Count(e => e.ItemId == manual.Id && e.IfRenewed), "January and February, if renewed");
        Assert.AreEqual(1, entries.Count(e => e.ItemId == none.Id), "nothing beyond an expiry that doesn't renew");
        Assert.AreEqual(500m, entries.Single(e => e.ItemId == later.Id).Amount, "a purchase still to come");
        Assert.IsFalse(entries.Any(e => e.ItemId == gone.Id), "nothing for a retired item");
    }

    [TestMethod]
    public void SharesAddUpToTheCent()
    {
        CollectionAssert.AreEqual(new[] { 3.34m, 3.33m, 3.33m }, CostMath.Share(10m, [1, 1, 1]).ToArray());
        CollectionAssert.AreEqual(new[] { 16m, 4m }, CostMath.Share(20m, [16, 4]).ToArray(), "by seats");
        CollectionAssert.AreEqual(new[] { 0.5m, 0.5m }, CostMath.Share(1m, [0, 0]).ToArray(), "all weights 0 share equally");
        var credit = CostMath.Share(-10m, [1, 1, 1]);
        Assert.AreEqual(-10m, credit.Sum(), "a credit adds up too");
        foreach (var amount in new[] { 0.01m, 99.99m, 1234.57m, 7m })
        {
            Assert.AreEqual(amount, CostMath.Share(amount, [3, 5, 7, 11]).Sum(), amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
