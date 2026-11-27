using System.Text.Json;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Licenses;
using PNMTD.Models.Db;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// Licences &amp; services for MCP and Cookie (docs/licences-plan.md §11), read-only and only while the feature is on: the items, one item,
/// and the costs. Never the keys (nor their hints), the documents' contents or the exports; the note and the support contact without
/// what looks like a credential.
/// </summary>
public static class LicenseReadTools
{
    private const string Meaning = "Licences & services is what IT pays for: licences, services (cloud, hosting, internet lines, phones, "
        + "support contracts, domains) and hardware, with their hosts, dates, seats and costs. Amounts are per currency, never converted.";

    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("licenses_list", "Licences & services",
            $"{Meaning} The items with their number (LIC-12, SVC-31, HW-40), type, name, product, kind, status, state (245/246 OK; 445 about to "
            + "expire or its cancellation deadline near, 446 expired, 447 end of life near, 448 past its end of life, 449 over-assigned), expiry, "
            + "cancellation deadline, end of life, seats, hosts, price and the cost per month and year. Filters narrow them. Paged.",
            Schema([
                Choice("type", "License, Service or Hardware", ["License", "Service", "Hardware"]),
                Choice("state", "attention (445, 447, 449), failing (446, 448) or ok", ["attention", "failing", "ok"]),
                Choices("status", "Active and Cancelled unless given", ["Active", "Cancelled", "Retired"]),
                Text("category", "A category's name"),
                Text("costCenter", "A cost centre"),
                Id("locationId", "Items whose location is this one (or below it), or that are on its hosts", required: false),
                Id("ownerId", "Items this user owns", required: false),
                Int("expiresWithinDays", "Only those expiring within so many days"),
                Id("hostId", "Only those on this host", required: false),
                Text("manufacturer", "A manufacturer"),
                Text("vendor", "A vendor or provider"),
                Text("search", "Words in the number, name, product, manufacturer, vendor, reference, customer number or host names"),
                .. PageArgs,
            ]),
            Area.Licenses, Level.View, ToolKind.Read, ListAsync) { ForCookie = true, Available = On },
        new("license_get", "A licence or service",
            $"{Meaning} One item in full, by id or number: its product, dates, seats, price and costs, its hosts, linked sensors and their "
            + "states, its renewals, its charges of the last 12 months, its documents' names, and its support details (provider, contact, URL, "
            + "customer number, SLA). Never its keys or documents' contents.",
            Schema(Id("id", "The item's id", required: false), Text("number", "Its number: LIC-12, SVC-31, HW-40 or 12")),
            Area.Licenses, Level.View, ToolKind.Read, GetAsync) { ForCookie = true, Available = On },
        new("licenses_costs", "What licences & services cost",
            $"{Meaning} The costs by group (category, type, provider, manufacturer, costCenter, location, host, owner or item), from one month "
            + "(yyyy-MM) to another, spent (the recorded charges), forecast (today's prices ahead) or both, in one currency, with the part that "
            + "depends on a manual renewal (ifRenewed) and the other currencies' totals. Items on several hosts are shared by their seats.",
            Schema(
                Choice("group", "category (the default), type, provider, manufacturer, costCenter, location, host, owner or item", LicenseQueries.Groups),
                Text("from", "The first month, yyyy-MM (this January when left out)"),
                Text("to", "The last month, yyyy-MM (this December when left out)"),
                Choice("show", "spent, forecast or both (the default)", ["spent", "forecast", "both"]),
                Text("currency", "EUR, CHF, …: one currency (the main one when left out)"),
                Bool("byMonth", "true: each group's value per month too")),
            Area.Licenses, Level.View, ToolKind.Read, CostsAsync) { ForCookie = true, Available = On },
    ];

    private static bool On(IServiceProvider services) => LicenseSetup.Enabled(services.GetRequiredService<IConfiguration>());

    private static async Task<object> ListAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var paging = Paging.From(args);
        var queries = ctx.Services.GetRequiredService<LicenseQueries>();
        var statuses = OptionalEnums<LicenseStatus>(args, "status");
        var rows = await queries.ListAsync(OptionalEnum<LicenseType>(args, "type"), statuses, null, ct);
        IEnumerable<LicenseRow> items = rows;
        switch (OptionalText(args, "state", 20))
        {
            case "attention":
                items = items.Where(r => r.VerdictCode is 445 or 447 or 449);
                break;
            case "failing":
                items = items.Where(r => r.VerdictCode is 446 or 448);
                break;
            case "ok":
                items = items.Where(r => r.VerdictCode is < 400);
                break;
        }
        if (OptionalText(args, "category", 60) is { } category)
        {
            items = items.Where(r => string.Equals(r.CategoryName, category, StringComparison.OrdinalIgnoreCase));
        }
        if (OptionalText(args, "costCenter", 100) is { } center)
        {
            items = items.Where(r => string.Equals(r.CostCenter, center, StringComparison.OrdinalIgnoreCase));
        }
        if (OptionalId(args, "ownerId") is { } owner)
        {
            items = items.Where(r => r.OwnerId == owner);
        }
        if (Has(args, "expiresWithinDays"))
        {
            var until = Lib.Helper.Clock.Today.AddDays(IntOf(args, "expiresWithinDays"));
            items = items.Where(r => r.ExpiresOn is { } e && e <= until);
        }
        if (OptionalId(args, "hostId") is { } host)
        {
            var on = (await queries.LinkedAsync(host, null, ct)).Select(i => i.Id).ToHashSet();
            items = items.Where(r => on.Contains(r.Id));
        }
        if (OptionalId(args, "locationId") is { } location)
        {
            var at = (await queries.LinkedAsync(null, location, ct)).Select(i => i.Id).ToHashSet();
            items = items.Where(r => at.Contains(r.Id));
        }
        if (OptionalText(args, "manufacturer", 100) is { } manufacturer)
        {
            items = items.Where(r => string.Equals(r.Manufacturer, manufacturer, StringComparison.OrdinalIgnoreCase));
        }
        if (OptionalText(args, "vendor", 100) is { } vendor)
        {
            items = items.Where(r => string.Equals(r.Vendor, vendor, StringComparison.OrdinalIgnoreCase));
        }
        if (OptionalText(args, "search", 200) is { } search)
        {
            items = items.Where(r => new[] { r.Display, r.Name, r.ProductName, r.Manufacturer, r.Vendor, r.Reference, r.CustomerNumber }
                .Concat(r.HostNames).Any(x => x?.Contains(search, StringComparison.OrdinalIgnoreCase) == true));
        }
        var list = items.ToList();
        var slice = paging.Slice(list);
        ctx.Summary = $"{slice.Items.Count} of {list.Count} licences and services";
        return new
        {
            Items = slice.Items.Select(r => new
            {
                r.Id, Number = r.Display, Type = r.Type.ToString(), Name = ctx.Untrusted("item name", r.Name, 200),
                Product = ctx.UntrustedOrNull("product", string.Join(" ", new[] { r.Manufacturer, r.ProductName, r.Edition }.Where(x => x is not null)), 300),
                Kind = r.Kind.ToString(), Status = r.Status.ToString(), State = r.VerdictCode, StateMessage = ctx.UntrustedOrNull("state", r.VerdictMessage, 500),
                r.ExpiresOn, r.CancelBy, r.EndOfLifeOn, Renewal = r.Renewal.ToString(), r.Seats, r.SeatUnit, r.UsedSeats, Hosts = r.Hosts,
                Category = r.CategoryName, r.CostCenter, Vendor = ctx.UntrustedOrNull("vendor", r.Vendor, 100), Owner = r.OwnerName,
                Price = r.PriceAmount, r.Currency, Billing = r.Billing.ToString(), r.BillingMonths, r.PerMonth, r.PerYear, r.ReviewDue,
                Url = ctx.Link($"licences/{r.Number}"),
            }),
            slice.NextCursor, slice.Total,
        };
    }

    private static async Task<object> GetAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var queries = ctx.Services.GetRequiredService<LicenseQueries>();
        LicenseDetail item;
        if (OptionalId(args, "id") is { } id)
        {
            item = await queries.GetAsync(id, ct);
        }
        else if (LicenseWords.ParseNumber(OptionalText(args, "number", 20)) is { } number)
        {
            item = await queries.GetByNumberAsync(number, ct);
        }
        else
        {
            throw ToolException.Invalid("An id or a number.");
        }
        var hosts = await queries.AssignmentsAsync(item.Id, ct);
        var links = await queries.SensorLinksAsync(item.Id, ct);
        var renewals = await queries.RenewalsAsync(item.Id, ct);
        var since = Lib.Helper.Clock.Today.AddMonths(-12);
        var charges = (await queries.ItemChargesAsync(item.Id, ct)).Where(c => c.On >= since).ToList();
        var documents = await queries.DocumentsAsync(item.Id, ct);
        ctx.Summary = $"{item.Display} {item.Name}";
        return new
        {
            item.Id, Number = item.Display, Type = item.Type.ToString(), Name = ctx.Untrusted("item name", item.Name, 200), Kind = item.Kind.ToString(),
            Status = item.Status.ToString(), State = item.VerdictCode, StateMessage = ctx.UntrustedOrNull("state", item.VerdictMessage, 500),
            Findings = item.Findings.Select(f => new { f.Code, Text = ctx.Untrusted("finding", f.Text, 300), f.On }),
            Template = item.TemplateName, Category = item.CategoryName, Manufacturer = ctx.UntrustedOrNull("manufacturer", item.Manufacturer, 100),
            Product = ctx.UntrustedOrNull("product", item.ProductName, 200), Edition = ctx.UntrustedOrNull("edition", item.Edition, 100),
            Vendor = ctx.UntrustedOrNull("vendor", item.Vendor, 100), LicensedTo = ctx.UntrustedOrNull("licensed to", item.LicensedTo, 200),
            Reference = ctx.UntrustedOrNull("reference", item.Reference, 200), item.CustomerNumber, item.CostCenter, Location = item.LocationName,
            Owner = item.OwnerName, item.Seats, item.SeatUnit, item.SeatsFromHosts, item.UsedSeats,
            Price = item.PriceAmount, item.Currency, PriceBasis = item.PriceBasis.ToString(), Billing = item.Billing.ToString(), item.BillingMonths,
            item.PerMonth, item.PerYear, item.SpentThisYear, item.StartsOn, item.ExpiresOn, item.TermMonths, Renewal = item.Renewal.ToString(),
            item.NoticeDays, item.CancelBy, item.EndOfLifeOn, EndOfLifeNote = ctx.UntrustedOrNull("end of life", item.EndOfLifeNote, 200), item.ReviewedOn,
            item.ReviewDueOn,
            Support = new
            {
                Provider = ctx.UntrustedOrNull("provider", item.Vendor, 100),
                Contact = ctx.UntrustedOrNull("support contact", ToolSafety.WithoutCredentials(item.SupportContact), 500),
                Url = item.SupportUrl, item.Sla, item.CustomerNumber,
            },
            Note = ctx.UntrustedOrNull("note", ToolSafety.WithoutCredentials(item.Note), 4000),
            Hosts = hosts.Select(h => new { h.HostId, Host = h.HostName, h.Seats, Sensor = h.SensorId, State = h.LastCode }),
            LinkedSensors = links.Select(l => new { l.SensorId, Sensor = l.SensorName, Host = l.HostName, State = l.LastCode }),
            Renewals = renewals.Select(r => new { Kind = r.Kind.ToString(), r.On, r.PreviousExpiresOn, r.NewExpiresOn, Price = r.PriceAmount, r.Currency }),
            Charges = charges.Select(c => new { c.On, Kind = c.Kind.ToString(), c.PeriodFrom, c.PeriodTo, c.Amount, c.Currency, c.Estimated }),
            Documents = documents.Select(d => new { Name = ctx.Untrusted("document name", d.Name, 200), Kind = d.Kind.ToString(), d.AddedAt }),
            Url = ctx.Link($"licences/{item.Number}"),
        };
    }

    private static async Task<object> CostsAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var queries = ctx.Services.GetRequiredService<LicenseQueries>();
        var costs = await queries.CostsAsync(new LicenseCostsQuery
        {
            Group = OptionalText(args, "group", 20), From = OptionalText(args, "from", 7), To = OptionalText(args, "to", 7),
            Show = OptionalText(args, "show", 10), Currency = OptionalText(args, "currency", 3),
        }, ct);
        var byMonth = OptionalBool(args, "byMonth") == true;
        ctx.Summary = $"the costs by {costs.Group}";
        return new
        {
            costs.Currency, costs.Group, costs.Show, Months = byMonth ? costs.Columns : null, costs.Total, costs.PerYearNow, costs.IfRenewed,
            costs.OtherCurrencies, costs.SharedByHosts,
            Groups = costs.Groups.Select(g => new
            {
                Name = ctx.Untrusted("group", g.Label, 200), g.Total, g.PerYearNow, Months = byMonth ? g.Values : null,
                Items = g.Items.Take(50).Select(i => new { Number = i.Display, Name = ctx.Untrusted("item name", i.Label, 200), i.Total, i.PerYearNow }),
            }),
        };
    }
}
