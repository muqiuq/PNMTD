using System.Diagnostics;
using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// WMI/CIM queries with a timeout each (docs/windows-sensor-plan.md §3.8, rule 2: 15 s), so a hung provider costs one
/// section, not the service. A missing class or namespace (an older Windows) is "not available", not a crash.
/// </summary>
internal static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string StandardCimv2 = @"root\StandardCimv2";
    public const string Smb = @"root\Microsoft\Windows\SMB";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string Defender = @"root\Microsoft\Windows\Defender";
    public const string BitLocker = @"root\CIMV2\Security\MicrosoftVolumeEncryption";
    public const string Tpm = @"root\CIMV2\Security\MicrosoftTpm";
    public const string WmiRoot = @"root\wmi";
    public const string Virtualization = @"root\virtualization\v2";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Every query, its rows and its time, and every failure with WMI's status, at debug level (the window's Debug logging).</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>The section whose collector runs on this thread, for the WMI tabs: which section a query belongs to.</summary>
    [ThreadStatic]
    public static string? CurrentSection;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WmiQueryState> States = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A built-in query, as PNMT and the window set it (<see cref="WmiSettings"/>): switched off, it answers no rows (the part
    /// stays empty, as for a class this Windows doesn't have); its timeout is PNMT's when set. Each run is recorded for the WMI tabs.
    /// </summary>
    public static List<ManagementBaseObject> Query(string scope, string wql, TimeSpan? timeout = null)
    {
        var name = WmiQueries.ClassOf(wql);
        var id = WmiQueries.Id(scope, name);
        var (enabled, offBy, configured) = WmiSettings.For(id);
        var limit = configured ?? timeout ?? Timeout;
        var section = CurrentSection;
        if (!enabled)
        {
            Logger.LogDebug("WMI {Scope} {Class}: switched off ({OffBy})", scope, name, offBy);
            Record(new WmiQueryState(id, scope, name ?? "?", wql, section, false, offBy, (int)limit.TotalSeconds, DateTimeOffset.UtcNow, null, null, "off", null));
            return [];
        }
        var started = Stopwatch.GetTimestamp();
        void Done(int? rows, string status, string? error) => Record(new WmiQueryState(id, scope, name ?? "?", wql, section, true, null, (int)limit.TotalSeconds,
            DateTimeOffset.UtcNow, rows, Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1), status, error));
        try
        {
            var rows = Rows(scope, wql, limit);
            Logger.LogDebug("WMI {Scope} {Class}: {Count} objects in {Ms:0} ms", scope, name, rows.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Done(rows.Count, "ok", null);
            return rows;
        }
        catch (NotAvailableException e)
        {
            Logger.LogDebug("WMI {Scope} {Class}: {Message} after {Ms:0} ms; the query: {Wql}", scope, name, e.Message,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, wql);
            Done(null, "not available", e.Message);
            throw;
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "WMI {Scope} {Class} failed after {Ms:0} ms: {Type} {Status}; the query: {Wql}", scope, name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, e.GetType().Name, (e as ManagementException)?.ErrorCode, wql);
            Done(null, e is ManagementException { ErrorCode: ManagementStatus.Timedout } ? "timed out" : "error", $"{e.GetType().Name}: {e.Message.Trim()}");
            throw;
        }
    }

    /// <summary>
    /// The rows of a query, unrecorded and without the settings (the own queries, which have their own). A class or namespace this
    /// Windows doesn't have is <see cref="NotAvailableException"/>.
    /// </summary>
    public static List<ManagementBaseObject> Rows(string scope, string wql, TimeSpan timeout)
    {
        var options = new System.Management.EnumerationOptions { Timeout = timeout, ReturnImmediately = true, Rewindable = false };
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
            // A forward-only result is read once, by its own enumerator. Never through LINQ: .NET 10's Cast().ToList() asks an
            // ICollection for its Count first, and ManagementObjectCollection.Count reads the whole result to count it, so the
            // list came back empty, without an error (every query, on every Windows: no services, no domain, no activation).
            var rows = new List<ManagementBaseObject>();
            foreach (ManagementBaseObject row in searcher.Get())
            {
                rows.Add(row);
            }
            return rows;
        }
        catch (ManagementException e) when (e.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace
            or ManagementStatus.NotSupported or ManagementStatus.InvalidQuery or ManagementStatus.ProviderNotCapable or ManagementStatus.NotAvailable)
        {
            throw new NotAvailableException($"{scope}: {WmiQueries.ClassOf(wql)} is not available here ({e.ErrorCode})");
        }
    }

    private static void Record(WmiQueryState state) => States[state.Id] = state;

    /// <summary>The built-in queries as they last ran, with today's settings (a switch takes effect at the next run, and shows at once).</summary>
    public static IReadOnlyList<WmiQueryState> QueryStates() => States.Values
        .Select(s =>
        {
            var (enabled, offBy, configured) = WmiSettings.For(s.Id);
            return s with { Enabled = enabled, OffBy = offBy, TimeoutSeconds = configured is { } t ? (int)t.TotalSeconds : s.TimeoutSeconds };
        })
        .OrderBy(s => s.Section ?? "~").ThenBy(s => s.Class, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static ManagementBaseObject? First(string scope, string wql) => Query(scope, wql).FirstOrDefault();

    public static string? Str(this ManagementBaseObject o, string name) => Value(o, name) switch
    {
        null => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        var v => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    public static int? Int(this ManagementBaseObject o, string name) => Value(o, name) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;

    public static long? Long(this ManagementBaseObject o, string name) => Value(o, name) is { } v ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : null;

    public static bool? Bool(this ManagementBaseObject o, string name) => Value(o, name) is { } v ? Convert.ToBoolean(v, CultureInfo.InvariantCulture) : null;

    public static string[] Strings(this ManagementBaseObject o, string name) => Value(o, name) switch
    {
        string[] a => a,
        ushort[] u => u.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray(),
        _ => [],
    };

    /// <summary>A CIM datetime ("20261113091500.000000+060") as an instant.</summary>
    public static DateTimeOffset? Date(this ManagementBaseObject o, string name)
    {
        if (Value(o, name) is not string text || text.Length < 14)
        {
            return null;
        }
        try
        {
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(text));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static object? Value(ManagementBaseObject o, string name)
    {
        try
        {
            return o[name];
        }
        catch (ManagementException e)
        {
            Logger.LogDebug("WMI {Class}.{Property}: {Status}", o.ClassPath?.ClassName, name, e.ErrorCode);
            return null;
        }
    }
}
