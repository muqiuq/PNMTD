using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PNMTD.Agents;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Services.Events;

namespace PNMTD.OsUpdates;

/// <summary>
/// The high-risk check's sources (docs/linux-os-updates-plan.md §6.1): their defaults, their roles, and the URL rules. A source's kind is
/// one of the parsers (code); its URL is the Admin's: https, and no private or loopback address unless allowed.
/// </summary>
public static class OsAdvisorySourceDefaults
{
    public static readonly IReadOnlyList<(OsAdvisorySourceKind Kind, string Name, string Url, int Minutes, bool Enabled)> All =
    [
        (OsAdvisorySourceKind.DebianTracker, "Debian Security Tracker", "https://security-tracker.debian.org/tracker/data/json", 360, true),
        (OsAdvisorySourceKind.UbuntuNotices, "Ubuntu security notices", "https://ubuntu.com/security/notices.json", 720, true),
        (OsAdvisorySourceKind.CisaKev, "CISA KEV", "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json", 360, true),
        (OsAdvisorySourceKind.Epss, "EPSS (FIRST)", "https://epss.empiricalsecurity.com/epss_scores-current.csv.gz", 1440, true),
        (OsAdvisorySourceKind.Rss, "Debian security advisories (DSA)", "https://www.debian.org/security/dsa-long.rdf", 60, false),
        (OsAdvisorySourceKind.Rss, "Ubuntu security notices (RSS)", "https://ubuntu.com/security/notices/rss.xml", 60, false),
    ];

    /// <summary>What an update fixes, and how dangerous a CVE is (§6.1); RSS texts belong to neither (§7).</summary>
    public static string? Role(OsAdvisorySourceKind kind) => kind switch
    {
        OsAdvisorySourceKind.DebianTracker or OsAdvisorySourceKind.UbuntuNotices => "fixes",
        OsAdvisorySourceKind.CisaKev or OsAdvisorySourceKind.Epss => "risk",
        _ => null,
    };

    public static int DefaultMinutes(OsAdvisorySourceKind kind) => All.First(d => d.Kind == kind).Minutes;

    /// <summary>Why a URL can't be a source's, or null: absolute, https (http only with private addresses allowed), no user info.</summary>
    public static string? UrlProblem(string url, bool allowPrivate)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "Not an absolute URL.";
        }
        if (uri.Scheme != Uri.UriSchemeHttps && !(allowPrivate && uri.Scheme == Uri.UriSchemeHttp))
        {
            return "Only https URLs (http only for a local mirror, with OsUpdates__Advisories__AllowPrivateAddresses=true).";
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "No user name or password in the URL.";
        }
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IsPrivate(address) && !allowPrivate)
        {
            return "A private or loopback address (allowed only with OsUpdates__Advisories__AllowPrivateAddresses=true).";
        }
        return null;
    }

    /// <summary>Loopback, private (RFC 1918, unique local), link-local, carrier-grade NAT, unspecified, and IPv4 mapped into IPv6.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 0 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
    }
}

/// <summary>
/// Fetches the enabled sources on their intervals, or at once when asked (<see cref="FetchNow"/>), and stores what they say (§6.1): fixes
/// replaced per source in one transaction (Ubuntu's notices added incrementally), KEV and EPSS into the CVE risks, RSS items once. A failed
/// fetch keeps the last good data and marks the source. Afterwards the evaluator looks again, and the system sensor says whether the check
/// is blind (434). Nothing about the fleet leaves PNMT: only whole public files are downloaded, never per package or CVE.
/// </summary>
public sealed class OsAdvisoryFetcher(IDbContextFactory<PnmtdDbContext> dbs, IHttpClientFactory http, IOptionsMonitor<OsUpdatesOptions> options,
    IOptionsMonitor<AiOptions> ai, OsAdvisoryNudge nudge, SystemSensors sensors, IServiceProvider services, ILogger<OsAdvisoryFetcher> logger) : BackgroundService
{
    public const string HttpClientName = "OsAdvisories";

    /// <summary>A feed's text at most (the RSS sources).</summary>
    private const int MaxFeedChars = 8 * 1024 * 1024;

    private readonly ConcurrentQueue<Guid> requested = new();
    private readonly SemaphoreSlim wake = new(0);

    /// <summary>Fetches a source on the next pass (<i>Fetch now</i>).</summary>
    public void FetchNow(Guid sourceId)
    {
        requested.Enqueue(sourceId);
        wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CurrentValue is { Enabled: true, Advisories.Enabled: true })
                {
                    await SeedAsync(stoppingToken);
                    await PassAsync(stoppingToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "The OS advisory sources failed");
            }
            await wake.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>The defaults, once: when the check is first turned on and there are no sources.</summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (await db.OsAdvisorySources.AnyAsync(ct))
        {
            return;
        }
        foreach (var (kind, name, url, minutes, enabled) in OsAdvisorySourceDefaults.All)
        {
            db.OsAdvisorySources.Add(new OsAdvisorySourceEntity
            {
                Id = Guid.NewGuid(), Kind = kind, Name = name, Url = url, IntervalMinutes = minutes, Enabled = enabled, CreatedAt = Clock.UtcNow,
            });
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("OS advisories: the default sources were created");
    }

    /// <summary>One pass: every enabled source that is due (or asked for), then the system sensor.</summary>
    public async Task PassAsync(CancellationToken ct)
    {
        var asked = new HashSet<Guid>();
        while (requested.TryDequeue(out var id))
        {
            asked.Add(id);
        }
        List<OsAdvisorySourceEntity> due;
        await using (var db = await dbs.CreateDbContextAsync(ct))
        {
            var now = Clock.UtcNow;
            due = (await db.OsAdvisorySources.AsNoTracking().Where(s => s.Enabled || asked.Contains(s.Id)).ToListAsync(ct))
                .Where(s => asked.Contains(s.Id) || s.LastFetchAt is null || now - s.LastFetchAt >= TimeSpan.FromMinutes(Math.Max(5, s.IntervalMinutes)))
                // What an update fixes first, then the risks (EPSS keeps only the CVEs the fixes name), then texts.
                .OrderBy(s => s.Kind switch { OsAdvisorySourceKind.DebianTracker or OsAdvisorySourceKind.UbuntuNotices => 0, OsAdvisorySourceKind.CisaKev => 1, OsAdvisorySourceKind.Epss => 2, _ => 3 })
                .ToList();
        }
        var changed = false;
        foreach (var source in due)
        {
            changed |= await FetchAsync(source.Id, ct);
        }
        if (changed)
        {
            nudge.All();
        }
        await StateAsync(ct);
    }

    /// <summary>Fetches one source; answers whether its data changed. A failure is stored on the source, never thrown.</summary>
    public async Task<bool> FetchAsync(Guid sourceId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var source = await db.OsAdvisorySources.SingleOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null)
        {
            return false;
        }
        var o = options.CurrentValue.Advisories;
        source.LastFetchAt = Clock.UtcNow;
        try
        {
            if (OsAdvisorySourceDefaults.UrlProblem(source.Url, o.AllowPrivateAddresses) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }
            var entries = source.Kind switch
            {
                OsAdvisorySourceKind.DebianTracker => await DebianTrackerAsync(db, source, ct),
                OsAdvisorySourceKind.UbuntuNotices => await UbuntuAsync(db, source, ct),
                OsAdvisorySourceKind.CisaKev => await KevAsync(db, source, ct),
                OsAdvisorySourceKind.Epss => await EpssAsync(db, source, ct),
                OsAdvisorySourceKind.Rss => await RssAsync(db, source, ct),
                _ => null,
            };
            source.LastSuccessAt = Clock.UtcNow;
            source.Error = null;
            if (entries is { } count)
            {
                source.Entries = count;
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("OS advisories: {Source} fetched ({Entries})", source.Name, entries is null ? "unchanged" : $"{entries} entries");
            return entries is not null;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("OS advisories: {Source} failed: {Message}", source.Name, e.Message);
            db.ChangeTracker.Clear();
            await db.OsAdvisorySources.Where(s => s.Id == sourceId).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.LastFetchAt, Clock.UtcNow)
                .SetProperty(s => s.Error, Cuts.Cut(Short(e), 1000)), ct);
            return false;
        }
    }

    private static string Short(Exception e) => e switch
    {
        HttpRequestException { StatusCode: { } code } => $"{(int)code} {code}",
        TaskCanceledException => "It didn't answer in time.",
        JsonException or System.Xml.XmlException => $"Its content didn't parse: {e.Message.Split('\n')[0]}",
        _ => e.Message.Split('\n')[0],
    };

    // --- downloading -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The body, at most <c>MaxDownloadBytes</c>, with the source's ETag and Last-Modified sent back (null: 304, unchanged); a gzip body that
    /// isn't marked as compressed (EPSS's .csv.gz) is decompressed too, within the same limit.
    /// </summary>
    private async Task<byte[]?> DownloadAsync(OsAdvisorySourceEntity source, string url, CancellationToken ct, bool conditional = true)
    {
        var client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"PNMT/{ServerInfo.Version} (OS advisories)");
        if (conditional && source.ETag is { } etag && EntityTagHeaderValueTryParse(etag) is { } tag)
        {
            request.Headers.IfNoneMatch.Add(tag);
        }
        if (conditional && source.LastModified is { } modified && DateTimeOffset.TryParse(modified, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var since))
        {
            request.Headers.IfModifiedSince = since;
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var max = options.CurrentValue.Advisories.MaxDownloadBytes;
        var bytes = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(ct), max, ct);
        if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            await using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            bytes = await ReadLimitedAsync(gzip, max, ct);
        }
        if (conditional)
        {
            source.ETag = response.Headers.ETag?.ToString() is { } e ? Cuts.Cut(e, 300) : null;
            source.LastModified = response.Content.Headers.LastModified?.ToString("R");
        }
        return bytes;
    }

    private static System.Net.Http.Headers.EntityTagHeaderValue? EntityTagHeaderValueTryParse(string text) =>
        System.Net.Http.Headers.EntityTagHeaderValue.TryParse(text, out var tag) ? tag : null;

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new InvalidOperationException($"It is larger than {max / 1024 / 1024} MiB (OsUpdates__Advisories__MaxDownloadBytes).");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // --- the kinds -----------------------------------------------------------------------------------------------------------

    /// <summary>The Debian codenames and Ubuntu codenames the fleet runs (from the servers' os-updates sections); Debian's supported ones without any.</summary>
    private static async Task<(HashSet<string> Debian, HashSet<string> Ubuntu)> DistributionsAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var used = await db.Agents.AsNoTracking().Where(a => a.OsDistribution != null).Select(a => a.OsDistribution!).Distinct().ToListAsync(ct);
        var debian = used.Where(d => d.StartsWith("debian:", StringComparison.Ordinal)).Select(d => d[7..]).ToHashSet(StringComparer.Ordinal);
        var ubuntu = used.Where(d => d.StartsWith("ubuntu:", StringComparison.Ordinal)).Select(d => d[7..]).ToHashSet(StringComparer.Ordinal);
        if (debian.Count == 0)
        {
            debian.UnionWith(["bookworm", "trixie"]);
        }
        return (debian, ubuntu);
    }

    private async Task<int?> DebianTrackerAsync(PnmtdDbContext db, OsAdvisorySourceEntity source, CancellationToken ct)
    {
        var (codenames, _) = await DistributionsAsync(db, ct);
        // The fleet's codenames changed since the last fetch: fetch in full, not conditionally.
        var known = await db.OsFixes.Where(f => f.SourceId == source.Id).Select(f => f.Distribution).Distinct().ToListAsync(ct);
        var complete = codenames.All(c => known.Contains("debian:" + c));
        var bytes = await DownloadAsync(source, source.Url, ct, conditional: complete);
        if (bytes is null)
        {
            return null;
        }
        var (fixes, _) = OsAdvisoryParsers.DebianTracker(bytes, codenames);
        await ReplaceFixesAsync(db, source.Id, fixes, ct);
        return fixes.Count;
    }

    /// <summary>
    /// Ubuntu's notices per release in use, newest first, 100 a page, until a page holds the newest notice of the last fetch (the cursor); the
    /// first fetch reads all (at most 100 pages). A CVE's fix in a source package keeps the newest notice's version.
    /// </summary>
    private async Task<int?> UbuntuAsync(PnmtdDbContext db, OsAdvisorySourceEntity source, CancellationToken ct)
    {
        var (_, releases) = await DistributionsAsync(db, ct);
        var cursor = string.IsNullOrEmpty(source.Cursor) ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(source.Cursor) ?? [];
        var changed = false;
        foreach (var release in releases)
        {
            var seen = new HashSet<(string, string)>();
            var rows = new List<SourceFix>();
            string? newest = null;
            for (var page = 0; page < 100; page++)
            {
                var separator = source.Url.Contains('?') ? '&' : '?';
                var url = $"{source.Url}{separator}release={Uri.EscapeDataString(release)}&limit=100&offset={page * 100}&order=newest";
                var bytes = await DownloadAsync(source, url, ct, conditional: false);
                var (notices, total) = OsAdvisoryParsers.UbuntuNotices(Encoding.UTF8.GetString(bytes!), release);
                newest ??= notices.FirstOrDefault()?.Id;
                var reachedCursor = false;
                foreach (var notice in notices)
                {
                    if (cursor.GetValueOrDefault(release) == notice.Id)
                    {
                        reachedCursor = true;
                        break;
                    }
                    foreach (var (package, version) in notice.Sources)
                    {
                        foreach (var cve in notice.Cves)
                        {
                            if (seen.Add((package, cve)))
                            {
                                rows.Add(new SourceFix($"ubuntu:{release}", package, cve, version, notice.Id, null));
                            }
                        }
                    }
                }
                if (reachedCursor || notices.Count < 100 || (page + 1) * 100 >= total)
                {
                    break;
                }
            }
            if (rows.Count > 0)
            {
                await UpsertFixesAsync(db, source.Id, rows, ct);
                changed = true;
            }
            if (newest is not null)
            {
                cursor[release] = newest;
            }
        }
        source.Cursor = JsonSerializer.Serialize(cursor);
        source.Entries = await db.OsFixes.CountAsync(f => f.SourceId == source.Id, ct);
        return changed ? source.Entries : null;
    }

    private async Task<int?> KevAsync(PnmtdDbContext db, OsAdvisorySourceEntity source, CancellationToken ct)
    {
        var bytes = await DownloadAsync(source, source.Url, ct);
        if (bytes is null)
        {
            return null;
        }
        var entries = OsAdvisoryParsers.CisaKev(Encoding.UTF8.GetString(bytes));
        var now = Clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var listed = entries.Select(e => e.Cve).ToList();
        await db.OsCveRisks.Where(r => r.Kev && !listed.Contains(r.Cve))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Kev, false).SetProperty(r => r.UpdatedAt, now), ct);
        var existing = await db.OsCveRisks.Where(r => listed.Contains(r.Cve)).ToDictionaryAsync(r => r.Cve, ct);
        foreach (var entry in entries)
        {
            if (!existing.TryGetValue(entry.Cve, out var row))
            {
                row = new OsCveRiskEntity { Cve = entry.Cve };
                db.OsCveRisks.Add(row);
            }
            row.Kev = true;
            row.KevAddedAt = entry.DateAdded;
            row.KevRansomware = entry.Ransomware;
            row.KevName = entry.Name;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return entries.Count;
    }

    /// <summary>EPSS for the CVEs the fixes, changelogs and texts name (about a hundred thousand of its 400,000), through a temporary table.</summary>
    private async Task<int?> EpssAsync(PnmtdDbContext db, OsAdvisorySourceEntity source, CancellationToken ct)
    {
        var bytes = await DownloadAsync(source, source.Url, ct);
        if (bytes is null)
        {
            return null;
        }
        var wanted = (await db.OsFixes.AsNoTracking().Select(f => f.Cve).Distinct().ToListAsync(ct))
            .Concat(await db.OsPackageChangelogs.AsNoTracking().SelectMany(c => c.Cves).Distinct().ToListAsync(ct))
            .Concat(await db.OsAdvisoryItems.AsNoTracking().SelectMany(i => i.Cves).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var (scores, date) = OsAdvisoryParsers.Epss(new StringReader(Encoding.UTF8.GetString(bytes)), wanted);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE epss_import (cve text, epss double precision, percentile double precision) ON COMMIT DROP", ct);
        await using (var import = await connection.BeginBinaryImportAsync("COPY epss_import (cve, epss, percentile) FROM STDIN (FORMAT BINARY)", ct))
        {
            foreach (var score in scores)
            {
                await import.StartRowAsync(ct);
                await import.WriteAsync(score.Cve, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await import.WriteAsync(score.Epss, NpgsqlTypes.NpgsqlDbType.Double, ct);
                await import.WriteAsync(score.Percentile, NpgsqlTypes.NpgsqlDbType.Double, ct);
            }
            await import.CompleteAsync(ct);
        }
        var day = (date ?? DateOnly.FromDateTime(Clock.UtcNow)).ToDateTime(TimeOnly.MinValue);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO os_cve_risks ("Cve", "Kev", "KevRansomware", "Epss", "EpssPercentile", "EpssDate", "UpdatedAt")
            SELECT cve, false, false, epss, percentile, {DateOnly.FromDateTime(day)}, {Clock.UtcNow} FROM epss_import
            ON CONFLICT ("Cve") DO UPDATE SET "Epss" = EXCLUDED."Epss", "EpssPercentile" = EXCLUDED."EpssPercentile", "EpssDate" = EXCLUDED."EpssDate",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);
        await transaction.CommitAsync(ct);
        return scores.Count;
    }

    /// <summary>A feed's new items (by guid and text): stored with their CVEs and keywords; with AI on, those with both go to the AI (§7.1).</summary>
    private async Task<int?> RssAsync(PnmtdDbContext db, OsAdvisorySourceEntity source, CancellationToken ct)
    {
        var bytes = await DownloadAsync(source, source.Url, ct);
        if (bytes is null)
        {
            return null;
        }
        var items = OsAdvisoryParsers.Feed(Encoding.UTF8.GetString(bytes), MaxFeedChars, Math.Clamp(ai.CurrentValue.MaxInputChars, 2000, 200_000));
        var guids = items.Select(i => i.Guid).ToList();
        var known = await db.OsAdvisoryItems.Where(i => i.SourceId == source.Id && guids.Contains(i.Guid)).Select(i => new { i.Guid, i.Hash }).ToListAsync(ct);
        var added = new List<OsAdvisoryItemEntity>();
        foreach (var item in items.Where(i => !known.Any(k => k.Guid == i.Guid && k.Hash == i.Hash)).DistinctBy(i => (i.Guid, i.Hash)))
        {
            var row = new OsAdvisoryItemEntity
            {
                Id = Guid.NewGuid(), SourceId = source.Id, Guid = item.Guid, Hash = item.Hash, Title = item.Title, Link = item.Link, PublishedAt = item.PublishedAt,
                FirstSeenAt = Clock.UtcNow, Text = item.Text, Cves = OsAdvisoryTexts.Cves(item.Text).Take(200).ToList(), Keywords = OsAdvisoryTexts.KeywordsIn(item.Text),
                AssessedBy = "code",
            };
            db.OsAdvisoryItems.Add(row);
            added.Add(row);
        }
        await db.SaveChangesAsync(ct);
        var cutoff = Clock.UtcNow.AddDays(-365);
        await db.OsAdvisoryItems.Where(i => i.SourceId == source.Id && i.FirstSeenAt < cutoff).ExecuteDeleteAsync(ct);
        if (added.Count > 0)
        {
            await (services.GetService<OsAdvisoryRating>()?.ConsiderAsync(added.Select(a => a.Id).ToList(), ct) ?? Task.CompletedTask);
        }
        return await db.OsAdvisoryItems.CountAsync(i => i.SourceId == source.Id, ct);
    }

    // --- storing fixes -------------------------------------------------------------------------------------------------------

    /// <summary>A source's fixes replaced in one transaction, through COPY (the tracker has about 150,000 for two releases).</summary>
    private static async Task ReplaceFixesAsync(PnmtdDbContext db, Guid sourceId, IReadOnlyList<SourceFix> fixes, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.OsFixes.Where(f => f.SourceId == sourceId).ExecuteDeleteAsync(ct);
        await CopyFixesAsync(db, "os_fixes", sourceId, fixes, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Fixes added or updated (Ubuntu's notices, which come incrementally), through a temporary table.</summary>
    private static async Task UpsertFixesAsync(PnmtdDbContext db, Guid sourceId, IReadOnlyList<SourceFix> fixes, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE fixes_import (LIKE os_fixes) ON COMMIT DROP", ct);
        await CopyFixesAsync(db, "fixes_import", sourceId, fixes, ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO os_fixes SELECT * FROM fixes_import
            ON CONFLICT ("SourceId", "Distribution", "SourcePackage", "Cve") DO UPDATE SET "FixedVersion" = EXCLUDED."FixedVersion", "Urgency" = EXCLUDED."Urgency"
            """, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task CopyFixesAsync(PnmtdDbContext db, string table, Guid sourceId, IReadOnlyList<SourceFix> fixes, CancellationToken ct)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var import = await connection.BeginBinaryImportAsync(
            $"COPY {table} (\"SourceId\", \"Distribution\", \"SourcePackage\", \"Cve\", \"FixedVersion\", \"Urgency\", \"Scope\") FROM STDIN (FORMAT BINARY)", ct);
        foreach (var fix in fixes.DistinctBy(f => (f.Distribution, f.SourcePackage, f.Cve)))
        {
            await import.StartRowAsync(ct);
            await import.WriteAsync(sourceId, NpgsqlTypes.NpgsqlDbType.Uuid, ct);
            await import.WriteAsync(Cuts.Cut(fix.Distribution, 60), NpgsqlTypes.NpgsqlDbType.Varchar, ct);
            await import.WriteAsync(Cuts.Cut(fix.SourcePackage, 200), NpgsqlTypes.NpgsqlDbType.Varchar, ct);
            await import.WriteAsync(Cuts.Cut(fix.Cve, 40), NpgsqlTypes.NpgsqlDbType.Varchar, ct);
            await import.WriteAsync(Cuts.Cut(fix.FixedVersion, 200), NpgsqlTypes.NpgsqlDbType.Varchar, ct);
            await WriteNullableAsync(import, fix.Urgency is null ? null : Cuts.Cut(fix.Urgency, 60), ct);
            await WriteNullableAsync(import, fix.Scope is null ? null : Cuts.Cut(fix.Scope, 20), ct);
        }
        await import.CompleteAsync(ct);
    }

    private static async Task WriteNullableAsync(NpgsqlBinaryImporter import, string? value, CancellationToken ct)
    {
        if (value is null)
        {
            await import.WriteNullAsync(ct);
        }
        else
        {
            await import.WriteAsync(value, NpgsqlTypes.NpgsqlDbType.Varchar, ct);
        }
    }

    // --- the system sensor (§5.4) -----------------------------------------------------------------------------------------------

    /// <summary>The check's state: 220 when every enabled source is fresh and both roles have one; 434 naming what is stale or missing.</summary>
    public async Task StateAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var sources = await db.OsAdvisorySources.AsNoTracking().Where(s => s.Enabled).ToListAsync(ct);
        var (message, ok) = State(sources, Clock.UtcNow, TimeSpan.FromHours(Math.Clamp(options.CurrentValue.Advisories.StaleHours, 1, 24 * 30)));
        await sensors.WriteAsync(SystemSensorKeys.OsAdvisories, ok ? PNMTStatusCodes.SYSTEM_OK : PNMTStatusCodes.OS_ADVISORIES_FAILING, message, ct);
    }

    /// <summary>
    /// A source is stale when its last success is older than <paramref name="stale"/> (or it never succeeded and was created that long ago, or its
    /// first fetch failed); a role is blind when none of its enabled sources is fresh.
    /// </summary>
    public static (string Message, bool Ok) State(IReadOnlyList<OsAdvisorySourceEntity> enabled, DateTime now, TimeSpan stale)
    {
        bool Fresh(OsAdvisorySourceEntity s) => s.LastSuccessAt is { } at ? now - at <= stale : s.Error is null && now - s.CreatedAt <= stale;
        var problems = new List<string>();
        foreach (var (role, text) in new[] { ("fixes", "what an update fixes"), ("risk", "how dangerous a CVE is") })
        {
            if (!enabled.Any(s => OsAdvisorySourceDefaults.Role(s.Kind) == role && Fresh(s)))
            {
                problems.Add($"No working source for {text}: the check can't see {(role == "risk" ? "exploited vulnerabilities" : "which updates fix what")}");
            }
        }
        foreach (var source in enabled.Where(s => !Fresh(s)))
        {
            problems.Add(source.LastSuccessAt is { } at
                ? $"{source.Name} not fetched since {at:yyyy-MM-dd HH:mm} UTC{(source.Error is { } e ? $" ({e})" : "")}"
                : $"{source.Name} never fetched{(source.Error is { } e2 ? $" ({e2})" : "")}");
        }
        return problems.Count == 0 ? ("Every OS advisory source is fresh.", true) : (Cuts.Cut(string.Join("; ", problems), 1900), false);
    }

    /// <summary>The named client's handler: decompresses, follows up to 5 redirects, and refuses private addresses at connect time (DNS rebinding).</summary>
    public static SocketsHttpHandler Handler(IServiceProvider services) => new()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (context, ct) =>
        {
            var allow = services.GetRequiredService<IOptionsMonitor<OsUpdatesOptions>>().CurrentValue.Advisories.AllowPrivateAddresses;
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var usable = addresses.Where(a => allow || !OsAdvisorySourceDefaults.IsPrivate(a)).ToArray();
            if (usable.Length == 0)
            {
                throw new HttpRequestException($"{context.DnsEndPoint.Host} resolves only to private addresses, which sources may not use.");
            }
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(usable, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
