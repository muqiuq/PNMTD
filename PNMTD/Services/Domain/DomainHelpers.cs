using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Helper;

namespace PNMTD.Services.Domain;

internal static class Paging
{
    public static (int Page, int PageSize) Normalize(PageQuery query) =>
        (Math.Max(1, query.Page), Math.Clamp(query.PageSize, 1, PageQuery.MaxPageSize));

    public static async Task<Paged<T>> PageAsync<T>(IQueryable<T> query, PageQuery page, CancellationToken ct)
    {
        var (number, size) = Normalize(page);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Paged<T>(items, number, size, total);
    }

    public static Paged<T> Page<T>(IEnumerable<T> items, PageQuery page)
    {
        var (number, size) = Normalize(page);
        var list = items as IReadOnlyCollection<T> ?? items.ToList();
        return new Paged<T>(list.Skip((number - 1) * size).Take(size).ToList(), number, size, list.Count);
    }

    /// <summary>("name", false) for "name", ("name", true) for "-name"; the default when empty.</summary>
    public static (string Field, bool Descending) Sort(string? sort, string defaultSort, params string[] allowed)
    {
        var value = string.IsNullOrWhiteSpace(sort) ? defaultSort : sort.Trim();
        var descending = value.StartsWith('-');
        var field = value.TrimStart('-', '+').ToLowerInvariant();
        if (!allowed.Contains(field))
        {
            throw ApiException.Invalid("sort", $"Sort by one of: {string.Join(", ", allowed)} (\"-\" first for descending).");
        }
        return (field, descending);
    }
}

internal static class Text
{
    /// <summary>A case-insensitive substring pattern for ILIKE, with % and _ taken literally.</summary>
    public static string? Pattern(string? search) =>
        string.IsNullOrWhiteSpace(search)
            ? null
            : "%" + search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

    public static bool Contains(string? value, string? search) =>
        string.IsNullOrWhiteSpace(search) || (value?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class Instants
{
    /// <summary>v0.12's "never" (DateTime.MinValue) as null.</summary>
    public static DateTime? OrNull(DateTime value) => value.Year <= 1 ? null : Clock.AsUtc(value);

    public static DateTime Utc(DateTime value) => Clock.AsUtc(value);
}

/// <summary>The address sensors report to, for the URLs the UI shows: PublicBaseUrl, else the request's.</summary>
public sealed class PublicUrls(IConfiguration configuration, IHttpContextAccessor accessor)
{
    public string? Configured => Text.Trimmed(configuration["PublicBaseUrl"])?.TrimEnd('/');

    public string Base
    {
        get
        {
            if (Configured is { } configured)
            {
                return configured;
            }
            var request = accessor.HttpContext?.Request;
            return request is null ? "" : $"{request.Scheme}://{request.Host}{request.PathBase}";
        }
    }

    public string WriteUrl(string token) => $"{Base}/event/{token}";

    public string ReadUrl(string token) => $"{Base}/read/{token}/json";

    /// <summary>Where an ADVANCED sensor's app POSTs its report, the token in the header (docs/advanced-sensor-plan.md §4.1).</summary>
    public string ReportUrl => $"{Base}/report";
}
