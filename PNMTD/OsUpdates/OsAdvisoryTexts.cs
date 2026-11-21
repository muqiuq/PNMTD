using System.Text.Json;
using System.Text.RegularExpressions;

namespace PNMTD.OsUpdates;

/// <summary>
/// Code's reading of advisory texts (docs/linux-os-updates-plan.md §7.1, step 1): the CVEs in a text, and its keywords. Only a text with a
/// CVE and an exploit keyword can reach the AI; without AI, such a hit makes a finding important, never high risk (D5).
/// </summary>
public static partial class OsAdvisoryTexts
{
    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Cve();

    /// <summary>The keywords, as they are searched (case-insensitive, whole words).</summary>
    public static readonly IReadOnlyList<string> Keywords =
    [
        "exploited", "in the wild", "zero-day", "0-day", "zero day", "actively", "proof of concept", "remote code execution", "unauthenticated",
    ];

    /// <summary>The keywords that say a vulnerability is attacked, not only that it is bad.</summary>
    private static readonly IReadOnlySet<string> ExploitKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "exploited", "in the wild", "zero-day", "0-day", "zero day", "actively",
    };

    public static bool IsExploitKeyword(string keyword) => ExploitKeywords.Contains(keyword);

    public static List<string> Cves(string text) => Cve().Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToList();

    public static List<string> KeywordsIn(string text) => Keywords
        .Where(k => Regex.IsMatch(text, $@"(?<![\w-]){Regex.Escape(k)}(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        .ToList();
}

/// <summary>One claim of the AI's answer (§7.1, step 4): about a CVE of the text, with a quote that occurs in it word for word.</summary>
public sealed record OsAdvisoryClaim(string Cve, IReadOnlyList<string> Packages, bool ExploitedInTheWild, bool ZeroDay, bool RemoteWithoutCredentials, string Quote);

/// <summary>
/// What is stored of an item's assessment (<c>os_advisory_items.Assessment</c>): the claims that passed validation, the summary, the
/// verification model's verdict per CVE (<c>agree</c>, <c>disagree</c>, <c>unsure</c>), the claims code dropped and why, and the guard's
/// probability.
/// </summary>
public sealed record OsAdvisoryAssessment(
    IReadOnlyList<OsAdvisoryClaim> Claims,
    string? Summary,
    IReadOnlyDictionary<string, string> Verdicts,
    IReadOnlyList<string> Dropped,
    double? InjectionProbability)
{
    public static OsAdvisoryAssessment? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<OsAdvisoryAssessment>(json, OsAdvisoryEvaluator.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, OsAdvisoryEvaluator.Json);
}
