namespace PNMTD.Lib.Helper;

/// <summary>
/// Time for the code that predates an injected <see cref="TimeProvider"/> (docs/webui-migration-plan.md §3.4).
/// <list type="bullet">
/// <item>Instants are UTC: everything stored, every age and every comparison.</item>
/// <item>"Local" is the configured time zone (<c>TZ</c>, an IANA name; default UTC), never the
///   host's own zone. Only rules about the wall clock use it: uptime per day, and interval
///   descriptors such as "daily at 8".</item>
/// </list>
/// Tests replace <see cref="Provider"/> and <see cref="TimeZone"/>.
/// </summary>
public static class Clock
{
    public static TimeProvider Provider { get; set; } = TimeProvider.System;

    public static TimeZoneInfo TimeZone { get; set; } = FromEnvironment();

    /// <summary>The "never" of the code before 1.0 (DateTime.MinValue), as a UTC value PostgreSQL accepts.</summary>
    public static readonly DateTime Never = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

    public static DateTime UtcNow => Provider.GetUtcNow().UtcDateTime;

    /// <summary>The wall-clock time in <see cref="TimeZone"/> (Kind Unspecified).</summary>
    public static DateTime LocalNow => ToLocal(UtcNow);

    public static DateTime ToLocal(DateTime instant) =>
        TimeZoneInfo.ConvertTimeFromUtc(AsUtc(instant), TimeZone);

    /// <summary>Today in <see cref="TimeZone"/>: the day an item expiring yesterday counts as expired on (docs/licences-plan.md §5.5).</summary>
    public static DateOnly Today => DateOnly.FromDateTime(LocalNow);

    /// <summary>A day as messages write it, <c>d MMM yyyy</c> in the invariant culture: <c>31 Dec 2026</c> (docs/licences-plan.md §5.5).</summary>
    public static string FormatDay(DateOnly day) => day.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A wall-clock time in <see cref="TimeZone"/> as a UTC instant. A time that a DST switch
    /// skips is taken an hour later.</summary>
    public static DateTime ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZone);
    }

    /// <summary>Unspecified is taken as UTC; Local (the process's zone) is converted.</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static TimeZoneInfo FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return TimeZoneInfo.Utc;
        }
        // glibc accepts ":Europe/Zurich" as well.
        return TimeZoneInfo.FindSystemTimeZoneById(name.Trim().TrimStart(':'));
    }

    private static TimeZoneInfo FromEnvironment()
    {
        try
        {
            return FromName(Environment.GetEnvironmentVariable("TZ"));
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
