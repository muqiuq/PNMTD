using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Locations;

namespace PNMTD.Services.Domain;

public sealed partial class SearchService(PnmtdDbContext db, LocationTree locations, IHttpContextAccessor accessor, IServiceProvider services)
{
    /// <summary>
    /// Hosts and sensors whose name contains q (hosts also by location text), locations by name (with their paths), and
    /// pages (docs/locations-knowledge-plan.md §6.7): the page of a number first, then by title, summary or tag. <c>#tag</c>
    /// searches pages by their tags only. At most <paramref name="limit"/> of each; archived pages never.
    /// </summary>
    public async Task<SearchResult> SearchAsync(string? q, int limit, CancellationToken ct)
    {
        var pattern = Text.Pattern(q);
        new ApiValidator()
            .Check(pattern is not null, "q", "What to search for.")
            .Check(limit is >= 1 and <= 100, "limit", "Between 1 and 100.")
            .ThrowIfInvalid();
        var term = q!.Trim();
        var tickets = await TicketsAsync(term, pattern!, limit, ct);
        if (term.StartsWith('#'))
        {
            return new SearchResult([], [], [], await PagesByTagAsync(term.TrimStart('#').Trim().ToLowerInvariant(), limit, ct), tickets, []);
        }
        var hosts = await HostsAsync(pattern!, limit, ct);
        var sensors = await db.Sensors.AsNoTracking()
            .Where(s => EF.Functions.ILike(s.Name, pattern!, @"\"))
            .OrderBy(s => s.Name).ThenBy(s => s.Parent.Name)
            .Take(limit)
            .Select(s => new SearchSensor(s.Id, s.Name, s.Type, s.ParentId, s.Parent.Name))
            .ToListAsync(ct);
        return new SearchResult(hosts, sensors, await LocationsAsync(pattern!, limit, ct), await PagesAsync(term, pattern!, limit, ct), tickets,
            await ZabbixHostsAsync(pattern!, limit, ct));
    }

    /// <summary>
    /// Hosts, locations and pages only, found as <see cref="SearchAsync"/> finds them: what a responsibility links
    /// (docs/responsibilities-plan.md §9.3), for those who manage responsibilities without Monitoring: View.
    /// </summary>
    public async Task<SearchResult> ObjectsAsync(string? q, int limit, CancellationToken ct)
    {
        var pattern = Text.Pattern(q);
        new ApiValidator()
            .Check(pattern is not null, "q", "What to search for.")
            .Check(limit is >= 1 and <= 100, "limit", "Between 1 and 100.")
            .ThrowIfInvalid();
        var term = q!.Trim();
        if (term.StartsWith('#'))
        {
            return new SearchResult([], [], [], await PagesByTagAsync(term.TrimStart('#').Trim().ToLowerInvariant(), limit, ct), [], []);
        }
        return new SearchResult(await HostsAsync(pattern!, limit, ct), [], await LocationsAsync(pattern!, limit, ct),
            await PagesAsync(term, pattern!, limit, ct), [], []);
    }

    private async Task<IReadOnlyList<SearchHost>> HostsAsync(string pattern, int limit, CancellationToken ct) =>
        await db.Hosts.AsNoTracking()
            .Where(h => EF.Functions.ILike(h.Name, pattern, @"\") || (h.Location != null && EF.Functions.ILike(h.Location, pattern, @"\")))
            .OrderBy(h => h.Name)
            .Take(limit)
            .Select(h => new SearchHost(h.Id, h.Name, h.Location, h.Enabled))
            .ToListAsync(ct);

    private async Task<IReadOnlyList<SearchLocation>> LocationsAsync(string pattern, int limit, CancellationToken ct)
    {
        var tree = await locations.GetAsync(ct);
        var found = await db.Locations.AsNoTracking()
            .Where(l => EF.Functions.ILike(l.Name, pattern, @"\"))
            .Select(l => new { l.Id, l.Name, l.Description })
            .ToListAsync(ct);
        return found
            .Select(l => new SearchLocation(l.Id, l.Name, tree.Path(l.Id) is { Length: > 0 } path ? path : l.Name, l.Description))
            .OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>Tickets, for Redmine's View: a number from PNMT's copy or Redmine itself, other text among the linked ones' subjects.</summary>
    private async Task<IReadOnlyList<SearchTicket>> TicketsAsync(string term, string pattern, int limit, CancellationToken ct)
    {
        if (accessor.HttpContext?.User is not { } user || !Auth.Rights.RightsExtensions.Has(user, Auth.Rights.Area.Redmine, Auth.Rights.Level.View)
            || services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Redmine.RedmineOptions>>().CurrentValue is not { Enabled: true })
        {
            return [];
        }
        if (int.TryParse(term.TrimStart('#'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            var copy = await db.RedmineIssues.AsNoTracking().Where(i => i.IssueId == number && !i.Missing)
                .Select(i => new SearchTicket(i.IssueId, i.Subject, i.StatusName, i.IsClosed)).FirstOrDefaultAsync(ct);
            if (copy is not null)
            {
                return [copy];
            }
            try
            {
                // At most 5 s for a number nobody looked up yet (docs/redmine-queue-outbound-plan.md §3.6); the worker keeps reading it.
                var found = await services.GetRequiredService<Redmine.RedmineService>()
                    .SearchAsync(new RedmineQuery { Search = number.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct, TimeSpan.FromSeconds(5));
                return found.Items.Select(i => new SearchTicket(i.Id, i.Subject, i.Status, i.IsClosed)).ToList();
            }
            catch (Redmine.RedmineException)
            {
                return [];
            }
            catch (Api.ApiException e) when (e.Status == StatusCodes.Status504GatewayTimeout)
            {
                return [];
            }
        }
        return await db.RedmineIssues.AsNoTracking().Where(i => !i.Missing && EF.Functions.ILike(i.Subject, pattern, @"\"))
            .OrderBy(i => i.IsClosed).ThenByDescending(i => i.UpdatedOn).Take(limit)
            .Select(i => new SearchTicket(i.IssueId, i.Subject, i.StatusName, i.IsClosed)).ToListAsync(ct);
    }

    /// <summary>Zabbix's hosts from the mirror, for Zabbix's View: by visible or technical name, or an address.</summary>
    private async Task<IReadOnlyList<SearchZabbixHost>> ZabbixHostsAsync(string pattern, int limit, CancellationToken ct)
    {
        if (accessor.HttpContext?.User is not { } user || !Auth.Rights.RightsExtensions.Has(user, Auth.Rights.Area.Zabbix, Auth.Rights.Level.View)
            || services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Zabbix.ZabbixOptions>>().CurrentValue is not { Enabled: true })
        {
            return [];
        }
        var found = await db.ZabbixHosts.AsNoTracking()
            .Where(h => h.GoneAt == null && (EF.Functions.ILike(h.VisibleName, pattern, @"\") || EF.Functions.ILike(h.TechnicalName, pattern, @"\")
                || h.Addresses.Any(a => EF.Functions.ILike(a, pattern, @"\"))))
            .OrderBy(h => h.VisibleName).Take(limit)
            .Select(h => new { h.HostId, h.VisibleName, h.TechnicalName, h.Addresses })
            .ToListAsync(ct);
        return found.Select(h => new SearchZabbixHost(h.HostId, h.VisibleName, h.TechnicalName, h.Addresses.FirstOrDefault())).ToList();
    }

    /// <summary><c>KB-42</c>, <c>kb42</c> or <c>42</c>.</summary>
    [GeneratedRegex(@"^(?:kb\s*-?\s*)?(\d{1,9})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex PageNumber();

    private async Task<IReadOnlyList<SearchPage>> PagesAsync(string term, string pattern, int limit, CancellationToken ct)
    {
        var number = PageNumber().Match(term) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : (int?)null;
        var lower = term.ToLowerInvariant();
        var pages = await db.KbPages.AsNoTracking()
            .Where(p => p.ArchivedAt == null && (p.Number == number
                || EF.Functions.ILike(p.Title, pattern, @"\")
                || (p.Summary != null && EF.Functions.ILike(p.Summary, pattern, @"\"))
                || p.Tags.Any(t => t.Tag!.NormalizedName == lower)))
            .OrderBy(p => p.Number == number ? 0 : 1).ThenByDescending(p => p.UpdatedAt)
            .Take(limit)
            .Select(p => new { p.Id, p.Number, p.Title, p.Summary, p.Status, p.Sensitive })
            .ToListAsync(ct);
        return await WithTagsAsync(pages.Select(p => new SearchPage(p.Id, p.Number, p.Title, p.Summary, [], p.Status, p.Sensitive)).ToList(), ct);
    }

    /// <summary><c>#backup</c>: pages with a tag starting so, the most recently updated first.</summary>
    private async Task<IReadOnlyList<SearchPage>> PagesByTagAsync(string tag, int limit, CancellationToken ct)
    {
        if (tag.Length == 0)
        {
            return [];
        }
        var pages = await db.KbPages.AsNoTracking()
            .Where(p => p.ArchivedAt == null && p.Tags.Any(t => t.Tag!.NormalizedName.StartsWith(tag)))
            .OrderByDescending(p => p.UpdatedAt)
            .Take(limit)
            .Select(p => new { p.Id, p.Number, p.Title, p.Summary, p.Status, p.Sensitive })
            .ToListAsync(ct);
        return await WithTagsAsync(pages.Select(p => new SearchPage(p.Id, p.Number, p.Title, p.Summary, [], p.Status, p.Sensitive)).ToList(), ct);
    }

    private async Task<IReadOnlyList<SearchPage>> WithTagsAsync(List<SearchPage> pages, CancellationToken ct)
    {
        if (pages.Count == 0)
        {
            return pages;
        }
        var ids = pages.Select(p => p.Id).ToList();
        var tags = (await db.KbPageTags.AsNoTracking()
                .Where(t => ids.Contains(t.PageId))
                .Select(t => new { t.PageId, t.Tag!.Name })
                .ToListAsync(ct))
            .ToLookup(t => t.PageId, t => t.Name);
        return pages.Select(p => p with { Tags = tags[p.Id].Order(StringComparer.Ordinal).ToList() }).ToList();
    }
}
