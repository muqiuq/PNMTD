using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.State;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// <c>wmi</c> (docs/windows-sensor-plan.md §16, "WMI"): every built-in query as it last ran, and the own queries PNMT configured,
/// each run on its own interval, with their rows (at most 200, values as text). Every minute; the configuration and the window
/// ask for it at once after a change. The window reads the same in <c>state\wmi-status.json</c>.
/// </summary>
internal sealed class WmiCollector(ILogger<WmiCollector> logger) : Collector
{
    public const string StatusFile = "wmi-status.json";

    public override string Name => SectionNames.Wmi;

    public override TimeSpan Interval => TimeSpan.FromMinutes(1);

    /// <summary>The own queries one after the other, each with its own timeout (at most 10 minutes).</summary>
    public override TimeSpan Timeout => TimeSpan.FromMinutes(15);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "lastRunAt", "ms" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    private readonly ConcurrentDictionary<Guid, (CustomWmiQuery Query, CustomWmiResult Result)> results = new();

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var custom = new List<CustomWmiResult>();
        foreach (var query in WmiSettings.Pnmt.Custom.Take(WmiQueries.MaxCustom))
        {
            ct.ThrowIfCancellationRequested();
            custom.Add(Custom(query, ct));
        }
        foreach (var gone in results.Keys.Except(custom.Select(c => c.Id)).ToList())
        {
            results.TryRemove(gone, out _);
        }
        var section = new WmiSection(Wmi.QueryStates(), custom.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            WmiSettings.LocallyOff().Order(StringComparer.OrdinalIgnoreCase).ToList());
        try
        {
            AgentFiles.WriteState(StatusFile, section);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("{File}: {Message}", StatusFile, e.Message);
        }
        return section;
    }

    /// <summary>An own query: run when due (its interval since the last run, or changed), otherwise its last result.</summary>
    private CustomWmiResult Custom(CustomWmiQuery query, CancellationToken ct)
    {
        var id = query.Id.ToString();
        var (enabled, offBy, _) = WmiSettings.For(id);
        offBy = !query.Enabled ? "pnmt" : offBy;
        var off = !query.Enabled || !enabled;
        CustomWmiResult Result(string status, string? error, double? ms, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string?>> rows, bool truncated,
            DateTimeOffset? at) => new(query.Id, query.Name, query.Namespace, query.Wql, !off, off ? offBy : null, at, ms, status, error, columns, rows, truncated);
        if (off)
        {
            results.TryRemove(query.Id, out _);
            return Result("off", null, null, [], [], false, null);
        }
        if (results.TryGetValue(query.Id, out var last) && last.Query == query && last.Result.LastRunAt is { } ran
            && DateTimeOffset.UtcNow - ran < TimeSpan.FromMinutes(Math.Clamp(query.IntervalMinutes, 1, 1440)))
        {
            return last.Result;
        }
        if (WmiQueries.Problem(query.Namespace, query.Wql) is { } refused)
        {
            return Store(Result("refused", refused, null, [], [], false, DateTimeOffset.UtcNow));
        }
        var started = Stopwatch.GetTimestamp();
        double Ms() => Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        try
        {
            var rows = Wmi.Rows(query.Namespace.Trim(), query.Wql.Trim(), TimeSpan.FromSeconds(Math.Clamp(query.TimeoutSeconds, 1, 600)));
            var columns = new List<string>();
            foreach (var row in rows.Take(WmiQueries.MaxRows))
            {
                foreach (PropertyData property in row.Properties)
                {
                    if (columns.Count < WmiQueries.MaxColumns && !columns.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        columns.Add(property.Name);
                    }
                }
            }
            var table = rows.Take(WmiQueries.MaxRows).Select(row => (IReadOnlyList<string?>)columns.Select(c => Text(row, c)).ToList()).ToList();
            logger.LogDebug("WMI own query {Name}: {Rows} rows, {Columns} columns in {Ms:0} ms", query.Name, rows.Count, columns.Count, Ms());
            return Store(Result("ok", null, Ms(), columns, table, rows.Count > WmiQueries.MaxRows, DateTimeOffset.UtcNow));
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(e, "WMI own query {Name} failed", query.Name);
            var status = e is NotAvailableException ? "not available" : e is ManagementException { ErrorCode: ManagementStatus.Timedout } ? "timed out" : "error";
            return Store(Result(status, $"{e.GetType().Name}: {e.Message.Trim()}", Ms(), [], [], false, DateTimeOffset.UtcNow));
        }

        CustomWmiResult Store(CustomWmiResult result)
        {
            results[query.Id] = (query, result);
            return result;
        }
    }

    /// <summary>A property as text: arrays joined, CIM dates as ISO 8601, at most <see cref="WmiQueries.MaxValueChars"/> characters.</summary>
    private static string? Text(ManagementBaseObject row, string column)
    {
        try
        {
            var property = row.Properties[column];
            var text = property.Value switch
            {
                null => null,
                string s when property.Type == CimType.DateTime && s.Length >= 14 => Date(s),
                string s => s,
                Array array => string.Join(", ", array.Cast<object?>().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))),
                var v => Convert.ToString(v, CultureInfo.InvariantCulture),
            };
            return text is { Length: > WmiQueries.MaxValueChars } ? text[..WmiQueries.MaxValueChars] + "…" : text;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static string Date(string cim)
    {
        try
        {
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(cim)).ToString("O", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return cim;
        }
    }
}
