using PNMTD.Models.Db;

namespace PNMTD.Licenses;

/// <summary>What the cost maths needs of an item (§6.1).</summary>
public sealed record CostItem(
    Guid Id,
    LicenseStatus Status,
    decimal? PriceAmount,
    string Currency,
    LicensePriceBasis PriceBasis,
    int? Seats,
    LicenseBilling Billing,
    int? BillingMonths,
    DateOnly? StartsOn,
    DateOnly CreatedOn,
    DateOnly? ExpiresOn,
    int? TermMonths,
    LicenseRenewal Renewal,
    DateOnly? RetiredOn)
{
    public static CostItem From(LicenseEntity l) => new(l.Id, l.Status, l.PriceAmount, l.Currency, l.PriceBasis, l.Seats, l.Billing, l.BillingMonths,
        l.StartsOn, DateOnly.FromDateTime(Lib.Helper.Clock.ToLocal(l.CreatedAt)), l.ExpiresOn, l.TermMonths, l.Renewal,
        l.Status == LicenseStatus.Retired && l.StatusChangedAt is { } at ? DateOnly.FromDateTime(Lib.Helper.Clock.ToLocal(at)) : null);

    public DateOnly Start => StartsOn ?? CreatedOn;

    public bool Periodic => Billing != LicenseBilling.OneTime && BillingMonths is > 0;
}

/// <summary>A cost to come (§6.4): the item, the day, the amount in its currency, and whether it depends on a manual renewal.</summary>
public sealed record ForecastEntry(Guid ItemId, DateOnly Day, decimal Amount, string Currency, bool IfRenewed);

/// <summary>A billing period PNMT records (§6.2).</summary>
public sealed record DueCharge(DateOnly Day, DateOnly PeriodTo, decimal Amount);

/// <summary>
/// The only place costs are computed (docs/licences-plan.md §6), pure: the unit price, the running cost per month and year, the billing
/// days, the charges PNMT records, the forecast, and shares that add up to the cent.
/// </summary>
public static class CostMath
{
    /// <summary>The price per billing period (or once): the amount, times the seats for a price per seat; null when unknown.</summary>
    public static decimal? UnitPrice(CostItem i) => i.PriceAmount is not { } amount ? null
        : i.PriceBasis == LicensePriceBasis.PerSeat ? i.Seats is { } seats ? amount * seats : null
        : amount;

    /// <summary>
    /// What counts as running today (§6.1): not retired, and not past its expiry unless it renews by itself. An expired licence that
    /// waits for a manual renewal isn't being paid.
    /// </summary>
    public static bool Counts(CostItem i, DateOnly today) =>
        i.Status != LicenseStatus.Retired
        && (i.ExpiresOn is not { } expires || today <= expires || (i.Status == LicenseStatus.Active && i.Renewal == LicenseRenewal.Automatic));

    /// <summary>The running cost per month: a recurring or variable price over its billing period; nothing for a one-time price.</summary>
    public static decimal PerMonth(CostItem i, DateOnly today) =>
        Counts(i, today) && i.Periodic && UnitPrice(i) is { } unit ? unit / i.BillingMonths!.Value : 0m;

    public static decimal PerYear(CostItem i, DateOnly today) => PerMonth(i, today) * 12m;

    /// <summary>
    /// The day the billing periods step from (§6.1): the current term's first day with an expiry and a term, else the start. Steps are
    /// counted from it, never from the last step, so 31 Jan falls on 28 Feb and then on 31 Mar.
    /// </summary>
    public static DateOnly Anchor(CostItem i) =>
        i.ExpiresOn is { } expires && i.TermMonths is > 0 ? expires.AddDays(1).AddMonths(-i.TermMonths.Value) : i.Start;

    /// <summary>The billing days from <paramref name="from"/> to <paramref name="to"/>, both included, whatever the term.</summary>
    public static IEnumerable<DateOnly> BillingDays(CostItem i, DateOnly from, DateOnly to)
    {
        if (!i.Periodic || to < from)
        {
            yield break;
        }
        var step = i.BillingMonths!.Value;
        var anchor = Anchor(i);
        var months = (from.Year - anchor.Year) * 12 + from.Month - anchor.Month;
        var k = (int)Math.Floor(months / (double)step) - 1;
        while (true)
        {
            var day = Step(anchor, k, step);
            if (day > to)
            {
                yield break;
            }
            if (day >= from)
            {
                yield return day;
            }
            k++;
        }
    }

    /// <summary>The billing day after <paramref name="day"/>, for a period's last day.</summary>
    public static DateOnly NextBillingDay(CostItem i, DateOnly day)
    {
        var step = i.BillingMonths ?? 1;
        var anchor = Anchor(i);
        var months = (day.Year - anchor.Year) * 12 + day.Month - anchor.Month;
        var k = (int)Math.Floor(months / (double)step) - 1;
        while (Step(anchor, k, step) <= day)
        {
            k++;
        }
        return Step(anchor, k, step);
    }

    /// <summary>A billing day is in the term (§6.1): from the start, to the expiry if any, before the retirement.</summary>
    public static bool InTerm(CostItem i, DateOnly day) =>
        day >= i.Start && (i.ExpiresOn is not { } expires || day <= expires) && (i.RetiredOn is not { } retired || day < retired);

    /// <summary>
    /// The billing periods PNMT records now (§6.2): the billing days in the term from <paramref name="from"/> to today, at the price of
    /// today, at most <paramref name="max"/>. Recurring and variable prices only.
    /// </summary>
    public static IReadOnlyList<DueCharge> DueCharges(CostItem i, DateOnly from, DateOnly today, int max = 120)
    {
        if (!i.Periodic || UnitPrice(i) is not { } unit || i.Status == LicenseStatus.Retired)
        {
            return [];
        }
        return BillingDays(i, from, today).Where(d => InTerm(i, d)).Take(max)
            .Select(d => new DueCharge(d, NextBillingDay(i, d).AddDays(-1), unit)).ToList();
    }

    /// <summary>
    /// What today's prices will cost from <paramref name="from"/> to <paramref name="to"/> (§6.4), never before tomorrow: each billing
    /// day within the term is due; beyond the expiry it is due for an active item that renews by itself, <i>if renewed</i> for one renewed
    /// by hand, and nothing otherwise. A one-time purchase still to start counts on its day. Retired items cost nothing.
    /// </summary>
    public static IReadOnlyList<ForecastEntry> Forecast(IEnumerable<CostItem> items, DateOnly from, DateOnly to, DateOnly today)
    {
        var start = from > today ? from : today.AddDays(1);
        var result = new List<ForecastEntry>();
        foreach (var i in items)
        {
            if (i.Status == LicenseStatus.Retired || UnitPrice(i) is not { } unit)
            {
                continue;
            }
            if (!i.Periodic)
            {
                if (i.Billing == LicenseBilling.OneTime && i.StartsOn is { } starts && starts >= start && starts <= to)
                {
                    result.Add(new ForecastEntry(i.Id, starts, unit, i.Currency, false));
                }
                continue;
            }
            foreach (var day in BillingDays(i, start, to))
            {
                if (day < i.Start)
                {
                    continue;
                }
                if (i.ExpiresOn is not { } expires || day <= expires)
                {
                    result.Add(new ForecastEntry(i.Id, day, unit, i.Currency, false));
                }
                else if (i.Status == LicenseStatus.Active && i.Renewal == LicenseRenewal.Automatic)
                {
                    result.Add(new ForecastEntry(i.Id, day, unit, i.Currency, false));
                }
                else if (i.Status == LicenseStatus.Active && i.Renewal == LicenseRenewal.Manual)
                {
                    result.Add(new ForecastEntry(i.Id, day, unit, i.Currency, true));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// An amount shared by weights, rounded to cents by the largest remainder (§6.5), so the parts add up to the whole. Weights of 0 get
    /// nothing; all weights 0 share equally.
    /// </summary>
    public static IReadOnlyList<decimal> Share(decimal amount, IReadOnlyList<int> weights)
    {
        if (weights.Count == 0)
        {
            return [];
        }
        var w = weights.All(x => x <= 0) ? weights.Select(_ => 1).ToList() : weights.Select(x => Math.Max(0, x)).ToList();
        var total = w.Sum();
        var cents = (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
        var raw = w.Select(x => (decimal)cents * x / total).ToList();
        var floors = raw.Select(r => (long)Math.Floor(r)).ToList();
        var left = cents - floors.Sum();
        foreach (var index in raw.Select((r, ix) => (Remainder: r - Math.Floor(r), ix)).OrderByDescending(x => x.Remainder).ThenBy(x => x.ix)
                     .Take((int)Math.Max(0, left)).Select(x => x.ix))
        {
            floors[index]++;
        }
        return floors.Select(c => c / 100m).ToList();
    }

    public static decimal Cents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static DateOnly Step(DateOnly anchor, int k, int step) => anchor.AddMonths(k * step);
}
