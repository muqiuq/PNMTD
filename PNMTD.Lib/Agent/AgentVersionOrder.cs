using System.Text.RegularExpressions;

namespace PNMTD.Lib.Agent;

/// <summary>
/// The agents' versions in order (docs/agent-update-plan.md §3, §4, §6.2): by their numbers, and a release above its
/// pre-releases (<c>1.8.0</c> above <c>1.8.0-rc1</c>, as <c>sensor.yml</c> makes a tag with a dash a pre-release). PNMTD sorts and
/// keeps versions with it; the agents decide with it whether PNMT's version is newer.
/// </summary>
public static partial class AgentVersionOrder
{
    /// <summary>
    /// A version an agent can have: <c>1.8.0</c>, or a pre-release <c>1.8.0-rc1</c>. Also what may name a folder
    /// (<c>sensor-{version}</c>) and a URL: no slashes, no dots in a row.
    /// </summary>
    public static bool IsValid(string? version) => version is { Length: <= 40 } && Pattern().IsMatch(version) && !version.Contains("..", StringComparison.Ordinal);

    /// <summary>Whether this version is a pre-release: it has a dash.</summary>
    public static bool IsPreRelease(string version) => version.Split('+')[0].Contains('-');

    /// <summary>Negative when <paramref name="a"/> is older, positive when newer. Versions that can't be read compare as text.</summary>
    public static int Compare(string a, string b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y)
        {
            return string.CompareOrdinal(a, b);
        }
        var numbers = x.Number.CompareTo(y.Number);
        if (numbers != 0)
        {
            return numbers;
        }
        return (x.Pre, y.Pre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(x.Pre, y.Pre),
        };
    }

    /// <summary>Whether <paramref name="candidate"/> is newer than <paramref name="current"/>; versions that can't be read: whether they differ.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Parse(candidate) is not null && Parse(current) is not null ? Compare(candidate, current) > 0 : !string.Equals(candidate, current, StringComparison.Ordinal);

    private static (Version Number, string? Pre)? Parse(string text)
    {
        var main = text.Split('+')[0];
        var dash = main.IndexOf('-');
        return Version.TryParse(dash >= 0 ? main[..dash] : main, out var number) ? (number, dash >= 0 ? main[(dash + 1)..] : null) : null;
    }

    [GeneratedRegex(@"^\d{1,5}\.\d{1,5}\.\d{1,5}(-[0-9A-Za-z][0-9A-Za-z.-]{0,30})?$")]
    private static partial Regex Pattern();
}
