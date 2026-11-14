using System.Text.RegularExpressions;

namespace PNMTD.Lib.Agent;

// WMI, transparent and adjustable (docs/windows-sensor-plan.md §16, "WMI"): every query the agent runs is reported with its
// last run, rows, time and status; PNMT switches built-in queries off or gives them another timeout, and adds read-only queries
// of its own, per server, in the configuration; a local administrator can switch any of them off in the agent's window.

/// <summary>PNMT → agent, in the configuration: built-in queries switched off or with another timeout, and the server's own queries.</summary>
public sealed record WmiConfig(IReadOnlyList<WmiQueryOverride> Overrides, IReadOnlyList<CustomWmiQuery> Custom)
{
    public static readonly WmiConfig Empty = new([], []);
}

/// <summary>A built-in query, by its id (<see cref="WmiQueries.Id"/>): on or off, and its timeout in seconds (null: the agent's).</summary>
public sealed record WmiQueryOverride(string Id, bool Enabled, int? TimeoutSeconds);

/// <summary>An own query: a read-only SELECT in a namespace, every so many minutes, its rows reported in the <c>wmi</c> section.</summary>
public sealed record CustomWmiQuery(Guid Id, string Name, string Namespace, string Wql, int IntervalMinutes, int TimeoutSeconds, bool Enabled);

/// <summary>Agent → PNMT: the <c>wmi</c> section, the built-in queries' states, the own queries' results, and what is off on the server.</summary>
public sealed record WmiSection(IReadOnlyList<WmiQueryState> Queries, IReadOnlyList<CustomWmiResult> Custom, IReadOnlyList<string> LocallyOff);

/// <summary>
/// A built-in query as it last ran: the section that ran it, on or off (and by whom: <c>pnmt</c> or <c>server</c>), its timeout,
/// when, how many rows in how long, and its status: <c>ok</c>, <c>not available</c> (this Windows has no such class or namespace),
/// <c>timed out</c>, <c>error</c>, <c>off</c>.
/// </summary>
public sealed record WmiQueryState(
    string Id,
    string Namespace,
    string Class,
    string Wql,
    string? Section,
    bool Enabled,
    string? OffBy,
    int TimeoutSeconds,
    DateTimeOffset? LastRunAt,
    int? Rows,
    double? Ms,
    string Status,
    string? Error);

/// <summary>An own query's last result: its columns and rows (at most <see cref="WmiQueries.MaxRows"/>, values as text), or why not.</summary>
public sealed record CustomWmiResult(
    Guid Id,
    string Name,
    string Namespace,
    string Wql,
    bool Enabled,
    string? OffBy,
    DateTimeOffset? LastRunAt,
    double? Ms,
    string Status,
    string? Error,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated);

/// <summary>The rules for WMI queries, the same in PNMT and on the agent.</summary>
public static partial class WmiQueries
{
    public const int MaxCustom = 20;
    public const int MaxRows = 200;
    public const int MaxColumns = 40;
    public const int MaxValueChars = 500;
    public const int MaxWqlChars = 2000;
    public const int DefaultTimeoutSeconds = 15;

    /// <summary>A built-in query's id: its namespace and class, <c>root\cimv2:Win32_Service</c>.</summary>
    public static string Id(string ns, string? className) => $"{ns.ToLowerInvariant()}:{className}";

    /// <summary>The class a WQL statement reads: the word after FROM.</summary>
    public static string? ClassOf(string wql) =>
        wql.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).SkipWhile(w => !w.Equals("FROM", StringComparison.OrdinalIgnoreCase))
            .Skip(1).FirstOrDefault();

    [GeneratedRegex(@"^root(\\[A-Za-z0-9_]+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex NamespacePattern();

    /// <summary>
    /// Why an own query is refused, or null: a namespace under root, and one SELECT (WQL can't change anything, and only SELECT is
    /// taken, so ASSOCIATORS OF and method calls stay out).
    /// </summary>
    public static string? Problem(string? ns, string? wql)
    {
        if (string.IsNullOrWhiteSpace(ns) || ns.Length > 200 || !NamespacePattern().IsMatch(ns.Trim()))
        {
            return @"The namespace: root\cimv2, root\StandardCimv2, … (letters, digits and _ under root).";
        }
        var text = wql?.Trim() ?? "";
        if (text.Length is 0 or > MaxWqlChars)
        {
            return $"The query: one SELECT of at most {MaxWqlChars} characters.";
        }
        if (!text.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase) || ClassOf(text) is null)
        {
            return "Only SELECT … FROM <class> [WHERE …] queries: WMI is only read.";
        }
        return text.Contains(';') ? "One query, without ;." : null;
    }
}
