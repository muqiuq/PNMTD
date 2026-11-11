using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;

namespace PNMTD.Services.Domain;

public sealed class SearchService(PnmtdDbContext db)
{
    /// <summary>Hosts and sensors whose name contains q (hosts also by location), at most <paramref name="limit"/> of each.</summary>
    public async Task<SearchResult> SearchAsync(string? q, int limit, CancellationToken ct)
    {
        var pattern = Text.Pattern(q);
        new ApiValidator()
            .Check(pattern is not null, "q", "What to search for.")
            .Check(limit is >= 1 and <= 100, "limit", "Between 1 and 100.")
            .ThrowIfInvalid();
        var hosts = await db.Hosts.AsNoTracking()
            .Where(h => EF.Functions.ILike(h.Name, pattern!, @"\") || (h.Location != null && EF.Functions.ILike(h.Location, pattern!, @"\")))
            .OrderBy(h => h.Name)
            .Take(limit)
            .Select(h => new SearchHost(h.Id, h.Name, h.Location, h.Enabled))
            .ToListAsync(ct);
        var sensors = await db.Sensors.AsNoTracking()
            .Where(s => EF.Functions.ILike(s.Name, pattern!, @"\"))
            .OrderBy(s => s.Name).ThenBy(s => s.Parent.Name)
            .Take(limit)
            .Select(s => new SearchSensor(s.Id, s.Name, s.Type, s.ParentId, s.Parent.Name))
            .ToListAsync(ct);
        return new SearchResult(hosts, sensors);
    }
}
