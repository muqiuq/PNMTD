using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.Mikrotik;

/// <summary>A release as the feed lists it: version, channel, date and the changelog's lines.</summary>
public sealed record FeedRelease(string Version, string Channel, DateTime? PublishedAt, string Changelog);

/// <summary>
/// MikroTik's release feed (docs/ai-operations-plan.md §10): every <c>Mikrotik:Advisories:IntervalMinutes</c> the stable
/// and long-term releases, each assessed once: by code (CVEs and security words), and with a hit and AI on by the AI,
/// whose "critical" the verification model must confirm. A release that fixes a very bad issue, with routers behind
/// it, writes 422 on the RouterOS advisories sensor; when every router has caught up, 220.
/// </summary>
public sealed partial class AdvisoryFeed(IDbContextFactory<PnmtdDbContext> dbs, IHttpClientFactory http, IOptionsMonitor<MikrotikOptions> options,
    IOptionsMonitor<AiOptions> ai, AiWorkQueue queue, SystemSensors sensors, ILogger<AdvisoryFeed> logger) : BackgroundService
{
    public const string Purpose = "routeros.rate";
    public const int MaxFeedBytes = 2 * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            if (o.Enabled && o.Advisories.Enabled)
            {
                try
                {
                    await RefreshAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The RouterOS release feed failed");
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, o.Advisories.IntervalMinutes)), stoppingToken).ConfigureAwait(false);
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var client = http.CreateClient(nameof(AdvisoryFeed));
        client.Timeout = TimeSpan.FromSeconds(30);
        using var response = await client.GetAsync(options.CurrentValue.Advisories.FeedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (limited.Length + read > MaxFeedBytes)
            {
                throw new InvalidOperationException($"The feed is larger than {MaxFeedBytes / 1024 / 1024} MiB.");
            }
            limited.Write(buffer, 0, read);
        }
        await ProcessAsync(System.Text.Encoding.UTF8.GetString(limited.ToArray()), ct);
    }

    /// <summary>New releases stored and assessed, then the routers behind evaluated. Public for the tests.</summary>
    public async Task ProcessAsync(string xml, CancellationToken ct)
    {
        var releases = Parse(xml);
        await using var db = await dbs.CreateDbContextAsync(ct);
        foreach (var release in releases)
        {
            if (await db.MikrotikReleases.AnyAsync(r => r.Version == release.Version && r.Channel == release.Channel, ct))
            {
                continue;
            }
            var code = Assess(release.Changelog);
            var entity = new MikrotikReleaseEntity
            {
                Id = Guid.NewGuid(), Version = release.Version, Channel = release.Channel, PublishedAt = release.PublishedAt, Changelog = release.Changelog,
                FirstSeenAt = Clock.UtcNow, Severity = code.Severity, ZeroDay = code.ZeroDay, Exploited = code.Exploited, Cves = code.Cves.ToList(),
                AssessedBy = "code", AssessedAt = Clock.UtcNow,
                Summary = code.Hits.Count > 0 ? "Security lines: " + string.Join(" · ", code.Hits.Take(3)) : null,
            };
            db.MikrotikReleases.Add(entity);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("New RouterOS release {Version} [{Channel}]: {Severity} by code", release.Version, release.Channel, code.Severity);
            if (code.Hits.Count > 0 && ai.CurrentValue.Usable)
            {
                entity.WorkId = await queue.EnqueueAsync(Purpose, "release", entity.Id, null, ct);
                await db.SaveChangesAsync(ct);
            }
        }
        var cutoff = Clock.UtcNow.AddYears(-2);
        await db.MikrotikReleases.Where(r => r.FirstSeenAt < cutoff).ExecuteDeleteAsync(ct);
        await EvaluateAsync(ct);
    }

    /// <summary>The feed's items: "RouterOS 7.24.6 [long-term]", the date, and the changelog as lines.</summary>
    public static IReadOnlyList<FeedRelease> Parse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFeedBytes };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var document = new System.Xml.Linq.XDocument();
        document = System.Xml.Linq.XDocument.Load(reader);
        System.Xml.Linq.XNamespace content = "http://purl.org/rss/1.0/modules/content/";
        var releases = new List<FeedRelease>();
        foreach (var item in document.Descendants("item"))
        {
            var title = item.Element("title")?.Value ?? "";
            var match = Title().Match(title);
            if (!match.Success)
            {
                continue;
            }
            var html = item.Element(content + "encoded")?.Value ?? item.Element("description")?.Value ?? "";
            var text = WebUtility.HtmlDecode(Break().Replace(html, "\n"));
            var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            DateTime? published = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var at) ? at.UtcDateTime : null;
            releases.Add(new FeedRelease(match.Groups[1].Value, match.Groups[2].Value, published, string.Join('\n', lines)));
        }
        return releases;
    }

    public sealed record CodeAssessment(ReleaseSeverity Severity, bool ZeroDay, bool Exploited, IReadOnlyList<string> Cves, IReadOnlyList<string> Hits);

    /// <summary>
    /// The code's assessment (§10.3): CVEs and security words per line. Critical: actively exploited or a zero-day, or a
    /// CVE with "exploit", "remote code" or "authentication bypass". Any other hit: Important. None: Normal.
    /// </summary>
    public static CodeAssessment Assess(string changelog)
    {
        var lines = changelog.Split('\n');
        var hits = lines.Where(l => Security().IsMatch(l) || Cve().IsMatch(l)).ToList();
        var cves = Cve().Matches(changelog).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
        var exploited = Exploited().IsMatch(changelog);
        var zeroDay = ZeroDay().IsMatch(changelog);
        var severe = cves.Count > 0 && Severe().IsMatch(changelog);
        var severity = exploited || zeroDay || severe ? ReleaseSeverity.Critical : hits.Count > 0 ? ReleaseSeverity.Important : ReleaseSeverity.Normal;
        return new CodeAssessment(severity, zeroDay, exploited, cves, hits);
    }

    /// <summary>
    /// The routers behind a critical release of their channel (§10.4): 422 naming them, or 220 when there are none. The
    /// routers' names stay in PNMT; no AI sees them.
    /// </summary>
    public async Task EvaluateAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Enabled || !o.Advisories.Enabled)
        {
            return;
        }
        var behind = await BehindAsync(ct);
        var critical = behind.Where(b => b.Release.Severity == ReleaseSeverity.Critical && b.Routers.Count > 0 && !AwaitingAi(b.Release)).ToList();
        if (critical.Count == 0)
        {
            await sensors.WriteAsync(SystemSensorKeys.RouterOsAdvisories, PNMTStatusCodes.SYSTEM_OK, "No critical RouterOS fix pending.", ct);
            return;
        }
        var message = string.Join("; ", critical.Select(c =>
            $"Critical RouterOS fix {c.Release.Version} [{c.Release.Channel}]" +
            (c.Release.Cves.Count > 0 ? $" ({string.Join(", ", c.Release.Cves.Take(3))})" : "") +
            $": {c.Routers.Count} router(s) behind: {string.Join(", ", c.Routers.Take(10).Select(r => r.Name))}{(c.Routers.Count > 10 ? ", …" : "")}"));
        await sensors.WriteAsync(SystemSensorKeys.RouterOsAdvisories, PNMTStatusCodes.ROUTEROS_SECURITY_UPDATE, message.Length > 1900 ? message[..1900] : message, ct);
    }

    /// <summary>How long the code's critical waits for the AI's rating before it alerts on its own.</summary>
    public static readonly TimeSpan AiGrace = TimeSpan.FromHours(6);

    /// <summary>
    /// The code's critical stands only without AI (§10.3): while the AI's rating is pending it does not alert, so that a
    /// rating of important does not follow an alert. After <see cref="AiGrace"/> it does, so a stuck queue hides nothing.
    /// </summary>
    public static bool AwaitingAi(MikrotikReleaseEntity release) =>
        release.AssessedBy == "code" && release.WorkId is not null && Clock.UtcNow - release.FirstSeenAt < AiGrace;

    public sealed record Behind(MikrotikReleaseEntity Release, IReadOnlyList<(Guid SensorId, string Name)> Routers);

    /// <summary>Every assessed release of the last 2 years with a security fix, and the enabled routers of its channel below it.</summary>
    public async Task<IReadOnlyList<Behind>> BehindAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var releases = await db.MikrotikReleases.AsNoTracking().Where(r => r.Severity != ReleaseSeverity.Normal).ToListAsync(ct);
        var routers = await db.MikrotikRouters.AsNoTracking().Where(r => r.Sensor.Enabled && r.Version != null)
            .Select(r => new { r.SensorId, r.Sensor.Name, r.Version, r.Channel }).ToListAsync(ct);
        return releases
            .OrderByDescending(r => r.PublishedAt ?? r.FirstSeenAt)
            .Select(release =>
            {
                var target = RouterOsVersion.TryParse(release.Version, out var v) ? v : null;
                var below = routers.Where(r => target is not null && (r.Channel ?? "stable") == release.Channel
                        && RouterOsVersion.TryParse(r.Version, out var installed) && installed < target)
                    .Select(r => (r.SensorId, r.Name)).OrderBy(r => r.Name).ToList();
                return new Behind(release, below);
            })
            .ToList();
    }

    [GeneratedRegex(@"RouterOS\s+(\S+)\s+\[([a-z-]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Title();

    [GeneratedRegex(@"<br\s*/?>|</p>|</li>", RegexOptions.IgnoreCase)]
    private static partial Regex Break();

    [GeneratedRegex(@"CVE-\d{4}-\d{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex Cve();

    [GeneratedRegex(@"\b(security|vulnerab\w*|exploit\w*|remote code|authentication bypass|privilege escalation|denial of service|zero-day|0-day)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Security();

    [GeneratedRegex(@"\b(actively exploited|exploited in the wild|in-the-wild)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Exploited();

    [GeneratedRegex(@"\b(zero-day|0-day)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroDay();

    [GeneratedRegex(@"\b(exploit\w*|remote code|authentication bypass)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Severe();
}

public enum ReleaseLevel
{
    Critical,
    Important,
    Normal,
}

/// <summary>The AI's rating of a release (§10.3).</summary>
public sealed record ReleaseRating(
    [property: Description("critical only for a very bad issue: exploited in the wild, a zero-day, or exploitable remotely without credentials")] ReleaseLevel Severity,
    bool ZeroDay,
    bool ExploitedInTheWild,
    [property: Description("Remote code execution, authentication bypass, or a reachable crash, without credentials")] bool RemoteWithoutCredentials,
    [property: Description("CVE ids exactly as in the changelog")] IReadOnlyList<string> Cves,
    IReadOnlyList<string> Areas,
    [property: Description("At most 300 characters")] string Summary,
    [property: Description("At most 600 characters")] string Reasoning);

public sealed record ReleaseInput(string Version, string Channel, string UntrustedChangelog);

/// <summary>How bad a RouterOS release's fixes are; critical only for a very bad issue (§10.3).</summary>
public sealed class RouterOsRateTask(int maxTokens) : AiTask<ReleaseInput, ReleaseRating>(maxTokens)
{
    public override string Purpose => AdvisoryFeed.Purpose;

    public override string PromptVersion => "routeros-rate-1";

    public override bool Verify => true;

    public override string VerifyQuestion => "Does the changelog support this severity? Critical needs a very bad issue: exploited, a zero-day, or remote without credentials.";

    protected override string Instructions =>
        "Rate a MikroTik RouterOS release's changelog for its security fixes. critical: it fixes something exploited in the wild, a zero-day, " +
        "or something exploitable remotely without credentials (remote code execution, authentication bypass, a crash that takes the router down " +
        "from the network). important: other security fixes (local issues, hardening, a CVE that needs a login). normal: no security fix. " +
        "cves: only ids that appear in the changelog.";

    public override string? Validate(ReleaseRating output, ReleaseInput input)
    {
        var missing = output.Cves.FirstOrDefault(c => !input.UntrustedChangelog.Contains(c, StringComparison.OrdinalIgnoreCase));
        return missing is not null ? $"{missing} is not in the changelog." : output.Summary.Length > 600 ? "summary too long." : null;
    }
}

/// <summary>
/// <c>routeros.rate</c>: the AI's rating of a release whose changelog had security lines. Critical counts only with the
/// verification model's agreement; otherwise it becomes Important, marked disputed. Then the routers are evaluated.
/// </summary>
/// <remarks>The feed is resolved when needed: it enqueues through the queue, whose worker holds the handlers.</remarks>
public sealed class RouterOsRateHandler(IDbContextFactory<PnmtdDbContext> dbs, AiPipeline pipeline, IOptionsMonitor<AiOptions> ai, IServiceProvider services)
    : IAiWorkHandler
{
    public string Purpose => AdvisoryFeed.Purpose;

    public async Task<string?> RunAsync(AiWorkEntity work, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var release = await db.MikrotikReleases.SingleOrDefaultAsync(r => r.Id == work.SubjectId, ct) ?? throw new AiWorkStaleException("The release is gone.");
        var result = await pipeline.RunAsync(new RouterOsRateTask(ai.CurrentValue.Tokens.RouterOsRate),
            new ReleaseInput(release.Version, release.Channel, release.Changelog), [release.Changelog], new AiCall(Purpose, work.Id, work.Attempts + 1), ct);
        if (result.Blocked)
        {
            release.Summary = $"The AI did not rate it (stopped by the {result.BlockedBy}); the code's assessment stands. " + release.Summary;
            await db.SaveChangesAsync(ct);
            return JsonSerializer.Serialize(new { blocked = result.BlockedBy });
        }
        var r = result.Output!;
        var agreed = result.Verdict?.Answer == VerdictKind.Agree;
        release.Severity = r.Severity switch
        {
            ReleaseLevel.Critical => agreed ? ReleaseSeverity.Critical : ReleaseSeverity.Important,
            ReleaseLevel.Important => ReleaseSeverity.Important,
            _ => ReleaseSeverity.Normal,
        };
        release.AssessedBy = r.Severity == ReleaseLevel.Critical ? agreed ? "verified" : "disputed" : "ai";
        release.ZeroDay = r.ZeroDay;
        release.Exploited = r.ExploitedInTheWild;
        release.RemoteWithoutCredentials = r.RemoteWithoutCredentials;
        release.Cves = r.Cves.Select(c => c.ToUpperInvariant()).Distinct().ToList();
        release.Summary = r.Summary.Length > 1000 ? r.Summary[..1000] : r.Summary;
        release.AssessedAt = Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await services.GetRequiredService<AdvisoryFeed>().EvaluateAsync(ct);
        return JsonSerializer.Serialize(new { severity = release.Severity.ToString(), release.AssessedBy });
    }
}
