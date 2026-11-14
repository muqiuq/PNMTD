using System.Diagnostics;
using System.Globalization;
using System.Management;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// WMI/CIM queries with a timeout each (docs/windows-sensor-plan.md §3.8, rule 2: 15 s), so a hung provider costs one
/// section, not the service. A missing class or namespace (an older Windows) is "not available", not a crash.
/// </summary>
internal static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string StandardCimv2 = @"root\StandardCimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string Defender = @"root\Microsoft\Windows\Defender";
    public const string BitLocker = @"root\CIMV2\Security\MicrosoftVolumeEncryption";
    public const string Tpm = @"root\CIMV2\Security\MicrosoftTpm";
    public const string WmiRoot = @"root\wmi";
    public const string Virtualization = @"root\virtualization\v2";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Every query, its rows and its time, and every failure with WMI's status, at debug level (the window's Debug logging).</summary>
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    public static List<ManagementBaseObject> Query(string scope, string wql, TimeSpan? timeout = null)
    {
        var options = new System.Management.EnumerationOptions { Timeout = timeout ?? Timeout, ReturnImmediately = true, Rewindable = false };
        var name = wql.Split(' ').SkipWhile(w => !w.Equals("FROM", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
            var rows = searcher.Get().Cast<ManagementBaseObject>().ToList();
            Logger.LogDebug("WMI {Scope} {Class}: {Count} objects in {Ms:0} ms", scope, name, rows.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return rows;
        }
        catch (ManagementException e) when (e.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace
            or ManagementStatus.NotSupported or ManagementStatus.InvalidQuery or ManagementStatus.ProviderNotCapable or ManagementStatus.NotAvailable)
        {
            Logger.LogDebug("WMI {Scope} {Class}: not available ({Status}: {Message}) after {Ms:0} ms; the query: {Wql}", scope, name, e.ErrorCode,
                e.Message.Trim(), Stopwatch.GetElapsedTime(started).TotalMilliseconds, wql);
            throw new NotAvailableException($"{scope}: {name} is not available here ({e.ErrorCode})");
        }
        catch (Exception e)
        {
            Logger.LogDebug(e, "WMI {Scope} {Class} failed after {Ms:0} ms: {Type} {Status}; the query: {Wql}", scope, name,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, e.GetType().Name, (e as ManagementException)?.ErrorCode, wql);
            throw;
        }
    }

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

/// <summary>A class, namespace or API this Windows doesn't have: the part is "not available" (§3.9).</summary>
internal sealed class NotAvailableException(string message) : Exception(message);
