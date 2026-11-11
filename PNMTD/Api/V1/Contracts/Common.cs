using System.Text.Json.Serialization;

namespace PNMTD.Api.V1.Contracts;

// The v1 contracts (docs/webui-migration-plan.md §3.7). Conventions:
// - Responses are records built for the API, never EF entities. Requests carry the writable fields
//   only: ids, tokens, timestamps and computed state cannot be set by a client. Where create and
//   update take the same fields, one request type serves both.
// - Enums are strings ("HEARTBEAT"), timestamps UTC ISO 8601 ("2026-11-11T09:15:00Z"), per-day
//   values carry dates in the configured TZ (/system/info reports it).
// - Uptime is a fraction from 0 to 1; null means no data for that period.

/// <summary>A page of a list: <c>?page=1&amp;pageSize=50</c>, at most 500 per page.</summary>
public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public class PageQuery
{
    public const int MaxPageSize = 500;

    /// <summary>1-based.</summary>
    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;

    /// <summary>A field name, "-" first for descending (<c>-created</c>). Each list documents its fields.</summary>
    public string? Sort { get; set; }

    /// <summary>Case-insensitive substring match.</summary>
    public string? Search { get; set; }
}

/// <summary>The id and name of a related resource.</summary>
public sealed record Ref(Guid Id, string Name);

/// <summary>
/// A sensor's state, in this order of precedence: Disabled (it, its host or its ENCAPSULADED parent
/// is disabled), Ignored, then NoData (no event yet), Failing (the last event's code is above 399)
/// or Ok. Timespan sensors count only their evaluations (280/281/480/481); an ENCAPSULADED parent
/// takes the worst state of its children.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SensorState>))]
public enum SensorState { Ok, Failing, NoData, Ignored, Disabled }

/// <summary>Disabled; Failing when any active sensor fails; Ok when one reports OK; otherwise NoData.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HostState>))]
public enum HostState { Ok, Failing, NoData, Disabled }

/// <summary>Sensors by state. ENCAPSULADED parents are not counted; their children are.</summary>
public sealed record SensorCounts(int Ok, int Failing, int NoData, int Ignored, int Disabled)
{
    public int Total => Ok + Failing + NoData + Ignored + Disabled;
}

/// <summary>One value per day from <see cref="Start"/> up to and including today (TZ), null where there is no data.</summary>
public sealed record UptimeSeries(DateOnly Start, IReadOnlyList<double?> Values);
