using PNMTD.Agents;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using PNMTD.Ai;
using PNMTD.Lib.Agent;

namespace PNMTD.OsUpdates;

/// <summary>A fix as a source states it: in a distribution's source package, the CVE is fixed in this version.</summary>
public sealed record SourceFix(string Distribution, string SourcePackage, string Cve, string FixedVersion, string? Urgency, string? Scope);

/// <summary>A CISA KEV entry.</summary>
public sealed record KevEntry(string Cve, DateOnly? DateAdded, bool Ransomware, string? Name);

/// <summary>An EPSS score.</summary>
public sealed record EpssScore(string Cve, double Epss, double Percentile);

/// <summary>An advisory text from a feed: its guid, title, link, time, and the text cleaned as an AI would see it.</summary>
public sealed record FeedItem(string Guid, string Title, string? Link, DateTime? PublishedAt, string Text, string Hash);

/// <summary>An Ubuntu security notice for one release: its id, when, its CVEs, and the source packages with their fixed versions.</summary>
public sealed record UbuntuNotice(string Id, DateTime? PublishedAt, IReadOnlyList<string> Cves, IReadOnlyList<(string Source, string Version)> Sources);

/// <summary>
/// The parsers of the advisory sources (docs/linux-os-updates-plan.md §6.1), as pure functions over the formats seen in O0 (2026-11-21):
/// the Debian Security Tracker's JSON (read as a stream of tokens, only the releases in use kept), Ubuntu's notices JSON, CISA KEV's JSON,
/// FIRST's EPSS CSV, and RSS 2.0, RSS 1.0 (RDF) and Atom feeds (DTDs prohibited). Anything that doesn't parse is skipped and counted.
/// </summary>
public static partial class OsAdvisoryParsers
{
    [GeneratedRegex(@"^CVE-\d{4}-\d{4,}$")]
    private static partial Regex CveId();

    /// <summary>
    /// The Debian Security Tracker (<c>/tracker/data/json</c>): <c>{ source: { CVE: { scope, releases: { codename: { status, fixed_version,
    /// urgency } } } } }</c>. Kept: CVE ids, releases in <paramref name="codenames"/>, status <c>resolved</c> with a fixed version (not "0",
    /// which means never affected).
    /// </summary>
    public static (List<SourceFix> Fixes, int Skipped) DebianTracker(ReadOnlySpan<byte> json, IReadOnlySet<string> codenames)
    {
        var fixes = new List<SourceFix>();
        var skipped = 0;
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        Expect(ref reader, JsonTokenType.StartObject);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var source = reader.GetString()!;
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                skipped++;
                continue;
            }
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var cve = reader.GetString()!;
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartObject || !CveId().IsMatch(cve))
                {
                    reader.Skip();
                    continue;
                }
                string? scope = null;
                var releases = new List<(string Release, string? Status, string? Fixed, string? Urgency)>();
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString();
                    reader.Read();
                    if (name == "scope" && reader.TokenType == JsonTokenType.String)
                    {
                        scope = reader.GetString();
                    }
                    else if (name == "releases" && reader.TokenType == JsonTokenType.StartObject)
                    {
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var release = reader.GetString()!;
                            reader.Read();
                            if (reader.TokenType != JsonTokenType.StartObject || !codenames.Contains(release))
                            {
                                reader.Skip();
                                continue;
                            }
                            string? status = null, fixedVersion = null, urgency = null;
                            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                            {
                                var field = reader.GetString();
                                reader.Read();
                                if (reader.TokenType == JsonTokenType.String)
                                {
                                    switch (field)
                                    {
                                        case "status":
                                            status = reader.GetString();
                                            break;
                                        case "fixed_version":
                                            fixedVersion = reader.GetString();
                                            break;
                                        case "urgency":
                                            urgency = reader.GetString();
                                            break;
                                    }
                                }
                                else
                                {
                                    reader.Skip();
                                }
                            }
                            releases.Add((release, status, fixedVersion, urgency));
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                foreach (var (release, status, fixedVersion, urgency) in releases)
                {
                    if (status == "resolved" && fixedVersion is { Length: > 0 } version && version != "0")
                    {
                        fixes.Add(new SourceFix($"debian:{release}", source, cve, version, urgency, scope));
                    }
                }
            }
        }
        return (fixes, skipped);
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (!reader.Read() || reader.TokenType != type)
        {
            throw new JsonException($"Expected {type} at the start.");
        }
    }

    /// <summary>
    /// Ubuntu's notices (<c>notices.json?release=…</c>): <c>{ notices: [ { id, published, cves_ids, release_packages: { codename: [ { name,
    /// version, is_source } ] } } ], total_results }</c>. The source packages of <paramref name="codename"/>; a notice without any listed as
    /// source names its binaries, which are kept as they are.
    /// </summary>
    public static (List<UbuntuNotice> Notices, int Total) UbuntuNotices(string json, string codename)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        var total = root.TryGetProperty("total_results", out var t) && t.TryGetInt32(out var n) ? n : 0;
        var notices = new List<UbuntuNotice>();
        if (!root.TryGetProperty("notices", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return (notices, total);
        }
        foreach (var notice in list.EnumerateArray())
        {
            var id = notice.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            if (id is null)
            {
                continue;
            }
            DateTime? published = notice.TryGetProperty("published", out var p) && p.ValueKind == JsonValueKind.String
                && DateTime.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : null;
            var cves = notice.TryGetProperty("cves_ids", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => CveId().IsMatch(x)).Distinct().ToList()
                : [];
            var sources = new List<(string, string)>();
            if (notice.TryGetProperty("release_packages", out var packages) && packages.ValueKind == JsonValueKind.Object
                && packages.TryGetProperty(codename, out var release) && release.ValueKind == JsonValueKind.Array)
            {
                var all = release.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                    .Select(x => (Name: Text(x, "name"), Version: Text(x, "version"), Source: x.TryGetProperty("is_source", out var s) && s.ValueKind == JsonValueKind.True))
                    .Where(x => x.Name is not null && x.Version is not null).ToList();
                var chosen = all.Any(x => x.Source) ? all.Where(x => x.Source) : all;
                sources.AddRange(chosen.Select(x => (x.Name!, x.Version!)).Distinct());
            }
            notices.Add(new UbuntuNotice(id, published, cves, sources));
        }
        return (notices, total);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>CISA's Known Exploited Vulnerabilities: <c>{ vulnerabilities: [ { cveID, dateAdded, knownRansomwareCampaignUse, vulnerabilityName } ] }</c>.</summary>
    public static List<KevEntry> CisaKev(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (!document.RootElement.TryGetProperty("vulnerabilities", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("No vulnerabilities in the catalogue.");
        }
        var entries = new List<KevEntry>();
        foreach (var item in list.EnumerateArray())
        {
            var cve = Text(item, "cveID")?.Trim().ToUpperInvariant();
            if (cve is null || !CveId().IsMatch(cve))
            {
                continue;
            }
            DateOnly? added = DateOnly.TryParseExact(Text(item, "dateAdded"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            var ransomware = string.Equals(Text(item, "knownRansomwareCampaignUse"), "Known", StringComparison.OrdinalIgnoreCase);
            var name = Text(item, "vulnerabilityName");
            entries.Add(new KevEntry(cve, added, ransomware, name is null ? null : Cuts.Cut(name, 300)));
        }
        return entries.DistinctBy(e => e.Cve).ToList();
    }

    /// <summary>
    /// FIRST's EPSS CSV: a comment line <c>#model_version:…,score_date:2026-11-20T12:03:05Z</c>, the header <c>cve,epss,percentile</c>, then a
    /// line per CVE. Only the CVEs <paramref name="wanted"/> are kept (all, when it is null).
    /// </summary>
    public static (List<EpssScore> Scores, DateOnly? Date) Epss(TextReader reader, IReadOnlySet<string>? wanted)
    {
        var scores = new List<EpssScore>();
        DateOnly? date = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.StartsWith('#'))
            {
                var at = line.IndexOf("score_date:", StringComparison.Ordinal);
                if (at >= 0 && DateOnly.TryParseExact(line.AsSpan(at + 11, Math.Min(10, line.Length - at - 11)), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d))
                {
                    date = d;
                }
                continue;
            }
            var parts = line.Split(',');
            if (parts.Length < 3 || !parts[0].StartsWith("CVE-", StringComparison.Ordinal) || (wanted is not null && !wanted.Contains(parts[0])))
            {
                continue;
            }
            if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var epss)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var percentile))
            {
                scores.Add(new EpssScore(parts[0], Math.Clamp(epss, 0, 1), Math.Clamp(percentile, 0, 1)));
            }
        }
        return (scores, date);
    }

    /// <summary>
    /// A feed's items (RSS 2.0, RSS 1.0/RDF as Debian's DSA list is, Atom): DTDs prohibited, at most <paramref name="maxChars"/> characters; each
    /// item's text cleaned (<see cref="InputSanitizer"/>: HTML to text, invisible characters removed, shortened to <paramref name="maxText"/>).
    /// </summary>
    public static List<FeedItem> Feed(string xml, int maxChars, int maxText)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maxChars };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var document = XDocument.Load(reader);
        XNamespace rss1 = "http://purl.org/rss/1.0/";
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        var items = new List<FeedItem>();
        foreach (var item in document.Descendants().Where(e => e.Name == "item" || e.Name == rss1 + "item" || e.Name == atom + "entry"))
        {
            string? Value(params XName[] names) => names.Select(n => item.Element(n)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
            var title = Value("title", rss1 + "title", atom + "title") ?? "";
            var link = Value("link", rss1 + "link") ?? item.Element(atom + "link")?.Attribute("href")?.Value;
            var guid = Value("guid", atom + "id") ?? item.Attribute(rdf + "about")?.Value ?? link ?? title;
            var body = Value(content + "encoded", "description", rss1 + "description", atom + "content", atom + "summary") ?? "";
            var date = Value("pubDate", dc + "date", atom + "updated", atom + "published");
            DateTime? published = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.UtcDateTime : null;
            if (title.Length == 0 && body.Length == 0)
            {
                continue;
            }
            var text = InputSanitizer.Clean($"{title}\n\n{body}", maxText).Text;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            items.Add(new FeedItem(Cuts.Cut(guid, 500), Cuts.Cut(InputSanitizer.Clean(title, 500).Text, 500), link is null ? null : Cuts.Cut(link, 1000), published, text, hash));
        }
        return items;
    }
}
