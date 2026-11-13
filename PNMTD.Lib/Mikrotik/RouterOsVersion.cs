using System.Globalization;
using System.Text.RegularExpressions;

namespace PNMTD.Lib.Mikrotik;

public enum RouterOsStage
{
    Beta = 0,
    ReleaseCandidate = 1,
    Release = 2,
}

/// <summary>
/// A RouterOS version as the router and MikroTik's update server write it (docs/mikrotik-plan.md §4.1,
/// §5.4): <c>7.16.1</c>, <c>7.13</c>, <c>7.17beta2</c>, <c>7.15rc3</c>, and in <c>/system resource</c>
/// with the channel, <c>7.16.1 (stable)</c>. Ordered as releases are: 7.17beta2 &lt; 7.17rc1 &lt; 7.17 &lt; 7.17.1.
/// </summary>
public sealed partial record RouterOsVersion(int Major, int Minor, int Patch, RouterOsStage Stage = RouterOsStage.Release, int StageNumber = 0)
    : IComparable<RouterOsVersion>
{
    /// <summary>The lowest version PNMT logs in to (§4.1): <c>:serialize to=json</c> exists from 7.13 on.</summary>
    public static readonly RouterOsVersion Floor = new(7, 13, 0);

    [GeneratedRegex(@"^\s*(\d{1,3})\.(\d{1,3})(?:\.(\d{1,3}))?(?:(beta|rc)(\d{1,3}))?\s*(?:\((?<channel>[a-z-]+)\))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static bool TryParse(string? text, out RouterOsVersion version) => TryParse(text, out version, out _);

    /// <summary>Also takes the channel suffix of <c>/system resource</c>'s version ("7.16.1 (stable)").</summary>
    public static bool TryParse(string? text, out RouterOsVersion version, out string? channel)
    {
        version = Floor;
        channel = null;
        var match = text is null ? null : Pattern().Match(text);
        if (match is not { Success: true })
        {
            return false;
        }
        var stage = match.Groups[4].Success
            ? match.Groups[4].Value.Equals("beta", StringComparison.OrdinalIgnoreCase) ? RouterOsStage.Beta : RouterOsStage.ReleaseCandidate
            : RouterOsStage.Release;
        version = new RouterOsVersion(
            Number(match.Groups[1].Value),
            Number(match.Groups[2].Value),
            match.Groups[3].Success ? Number(match.Groups[3].Value) : 0,
            stage,
            match.Groups[5].Success ? Number(match.Groups[5].Value) : 0);
        channel = match.Groups["channel"].Success ? match.Groups["channel"].Value.ToLowerInvariant() : null;
        return true;
    }

    public static RouterOsVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"Not a RouterOS version: {text}");

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    public bool IsBelowFloor => CompareTo(Floor) < 0;

    public bool IsPreRelease => Stage != RouterOsStage.Release;

    public int CompareTo(RouterOsVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c == 0) c = Stage.CompareTo(other.Stage);
        if (c == 0) c = StageNumber.CompareTo(other.StageNumber);
        return c;
    }

    public static bool operator <(RouterOsVersion a, RouterOsVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(RouterOsVersion a, RouterOsVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(RouterOsVersion a, RouterOsVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(RouterOsVersion a, RouterOsVersion b) => a.CompareTo(b) >= 0;

    /// <summary>As RouterOS writes it: 7.13 (no ".0"), 7.16.1, 7.17beta2, 7.15rc3.</summary>
    public override string ToString()
    {
        var text = Patch == 0 ? $"{Major}.{Minor}" : $"{Major}.{Minor}.{Patch}";
        return Stage switch
        {
            RouterOsStage.Beta => $"{text}beta{StageNumber}",
            RouterOsStage.ReleaseCandidate => $"{text}rc{StageNumber}",
            _ => text,
        };
    }
}
