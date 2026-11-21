using PNMTD.Agents;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.OsUpdates;

/// <summary>
/// A reason of a finding (§6.4): its kind (<c>kev</c>, <c>epss</c>, <c>urgency</c>, <c>changelog</c>, <c>ai</c>, <c>text</c>), whether it
/// makes the finding high risk, the source that said it, the detail, and a link to check it.
/// </summary>
public sealed record OsFindingReason(string Kind, bool High, string Source, string Detail, string? Link);

/// <summary>An AI claim about a CVE (§7), as the evaluator reads it: verified (the second model agreed) or not.</summary>
public sealed record OsAdvisoryClaimFact(string Cve, bool Verified, bool Exploited, bool ZeroDay, string SourceName, string Quote, string? Link);

/// <summary>A pending source update, as the rating sees it: the source package, its installed and candidate versions, its binaries, security or not.</summary>
public sealed record OsPendingSource(string Source, string? Installed, string Candidate, IReadOnlyList<string> Packages, bool Security);

/// <summary>What the sources say about the CVEs of a distribution's source packages: fixes, changelog parts, risks, AI claims and keyword hits.</summary>
public sealed record OsAdvisoryFacts(
    IReadOnlyList<(string Source, string SourcePackage, string Cve, string FixedVersion, string? Urgency, string? Scope, OsAdvisorySourceKind Kind)> Fixes,
    IReadOnlyList<OsPackageChangelogEntity> Changelogs,
    IReadOnlyDictionary<string, OsCveRiskEntity> Risks,
    IReadOnlyList<OsAdvisoryClaimFact> Claims,
    IReadOnlyList<(string Cve, string SourceName, string Keyword, string? Link)> KeywordHits);

/// <summary>A rated finding before it is stored.</summary>
public sealed record OsRatedFinding(string SourcePackage, string Cve, IReadOnlyList<string> Packages, string? Installed, string Candidate, OsFindingRating Rating,
    IReadOnlyList<OsFindingReason> Reasons);

/// <summary>
/// The high-risk check's code (docs/linux-os-updates-plan.md §6.3–§6.5): which CVEs each pending update fixes (the trackers' fixed versions
/// between installed and candidate, and the changelog's part since the installed version), how dangerous each is (KEV, EPSS percentile, the
/// distribution's urgency, a verified AI claim), the findings with their acknowledgements, and 466's message on the agent row. Sources only
/// add: a finding needs one source to name the fix and one to call it dangerous, and no source can take away what another says. Runs when a
/// server's section or a source changes, and every hour (acknowledgements run out).
/// </summary>
public sealed class OsAdvisoryEvaluator(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<OsUpdatesOptions> options, OsAdvisoryNudge nudge,
    ILogger<OsAdvisoryEvaluator> logger) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>An acknowledgement that ended is named in 466's message for this long.</summary>
    public static readonly TimeSpan EndedNoteFor = TimeSpan.FromHours(24);

    private static readonly string[] HighUrgencies = ["emergency", "critical"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        var nextAll = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var sensors = new HashSet<Guid>();
            var all = Clock.UtcNow >= nextAll;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                wait.CancelAfter(TimeSpan.FromMinutes(5));
                try
                {
                    if (!all && await nudge.Reader.WaitToReadAsync(wait.Token))
                    {
                        // A burst of sections settles for a moment, and their transactions commit.
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }
            }
            while (nudge.Reader.TryRead(out var item))
            {
                if (item is { } id)
                {
                    sensors.Add(id);
                }
                else
                {
                    all = true;
                }
            }
            try
            {
                if (all)
                {
                    nextAll = Clock.UtcNow.AddHours(1);
                    await EvaluateAllAsync(stoppingToken);
                }
                else
                {
                    foreach (var id in sensors)
                    {
                        await EvaluateAsync(id, stoppingToken);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "The OS updates' high-risk check failed");
            }
        }
    }

    public async Task EvaluateAllAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (!Active())
        {
            await db.Agents.Where(a => a.HighRiskMessage != null).ExecuteUpdateAsync(u => u.SetProperty(a => a.HighRiskMessage, (string?)null), ct);
            return;
        }
        var ids = await db.Agents.AsNoTracking().Where(a => a.Platform == AgentPlatform.Linux).Select(a => a.SensorId).ToListAsync(ct);
        foreach (var id in ids)
        {
            await EvaluateAsync(id, ct);
        }
    }

    private bool Active() => options.CurrentValue is { Enabled: true, Advisories.Enabled: true };

    /// <summary>One server: its findings rated, stored and resolved; acknowledgements checked; 466's message kept.</summary>
    public async Task EvaluateAsync(Guid sensorId, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var agent = await db.Agents.Include(a => a.Sensor).ThenInclude(s => s.Parent).SingleOrDefaultAsync(a => a.SensorId == sensorId, ct);
        if (agent is null || agent.Platform != AgentPlatform.Linux)
        {
            return;
        }
        if (!Active())
        {
            agent.HighRiskMessage = null;
            await db.SaveChangesAsync(ct);
            return;
        }
        var o = options.CurrentValue.Advisories;
        var now = Clock.UtcNow;
        var pending = await PendingAsync(db, sensorId, ct);
        var rated = agent.OsDistribution is { } distribution && pending.Count > 0
            ? Rate(pending, await FactsAsync(db, distribution, pending, ct), o.HighRiskEpssPercentile, o.ImportantEpssPercentile)
            : [];
        var open = await db.OsUpdateFindings.Where(f => f.SensorId == sensorId && f.ResolvedAt == null).ToListAsync(ct);
        var byKey = open.ToDictionary(f => (f.SourcePackage, f.Cve));
        var name = $"{agent.Sensor.Parent.Name} / {agent.Sensor.Name}";
        foreach (var finding in rated)
        {
            if (!byKey.Remove((finding.SourcePackage, finding.Cve), out var row))
            {
                row = new OsUpdateFindingEntity { Id = Guid.NewGuid(), SensorId = sensorId, SourcePackage = finding.SourcePackage, Cve = finding.Cve, FirstSeenAt = now };
                db.OsUpdateFindings.Add(row);
            }
            row.Packages = Cuts.Cut(string.Join(", ", finding.Packages), 1000);
            row.Installed = Cuts.CutOrNull(finding.Installed, 200);
            row.Candidate = Cuts.Cut(finding.Candidate, 200);
            row.Rating = finding.Rating;
            row.Reasons = JsonSerializer.Serialize(finding.Reasons, Json);
            EndAcknowledgementIfNeeded(db, agent, name, row, finding, now);
        }
        // No longer pending, or no longer rated: resolved, and its acknowledgement with it.
        foreach (var gone in byKey.Values)
        {
            gone.ResolvedAt = now;
        }
        agent.HighRiskMessage = Message(agent.Hostname, open.Where(f => f.ResolvedAt is null).Concat(db.OsUpdateFindings.Local.Where(f => f.ResolvedAt is null))
            .DistinctBy(f => f.Id).ToList(), now, o.HighRiskEpssPercentile);
        await db.SaveChangesAsync(ct);
        var cutoff = now.AddDays(-Math.Clamp(options.CurrentValue.KeepFindingsDays, 1, 3650));
        await db.OsUpdateFindings.Where(f => f.SensorId == sensorId && f.ResolvedAt != null && f.ResolvedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// §6.5: an acknowledgement ends when its time ran out, or when a high reason of a kind it didn't have arrived (the person accepted the
    /// risk they saw, not a worse one). Each end is in the audit log, by the system.
    /// </summary>
    private static void EndAcknowledgementIfNeeded(PnmtdDbContext db, AgentEntity agent, string name, OsUpdateFindingEntity row, OsRatedFinding finding, DateTime now)
    {
        if (row.AcknowledgedAt is null)
        {
            return;
        }
        string? why = null;
        if (row.AcknowledgedUntil is not { } until || until <= now)
        {
            why = "it ran out";
        }
        else
        {
            var stronger = finding.Reasons.Where(r => r.High).Select(r => r.Kind).Distinct().Except(row.AcknowledgedKinds).ToList();
            if (stronger.Count > 0)
            {
                var reason = finding.Reasons.First(r => r.High && r.Kind == stronger[0]);
                why = stronger[0] switch
                {
                    "kev" => "now in CISA KEV",
                    "epss" => "now likely to be exploited (EPSS)",
                    "ai" => $"a verified advisory says it is exploited ({reason.Source})",
                    _ => $"stronger evidence: {reason.Detail}",
                };
            }
        }
        if (why is null)
        {
            return;
        }
        AuditLog.Record(db, AuditEvents.OsUpdateFindingAcknowledgementEnded,
            $"{name}: {row.Cve} in {row.SourcePackage}: the acknowledgement by {row.AcknowledgedByName ?? "someone"} ended: {why}", AuditWho.System,
            AuditTarget.Sensor(agent.SensorId, name, agent.Sensor.ParentId));
        row.AcknowledgedAt = null;
        row.AcknowledgedById = null;
        row.AcknowledgedByName = null;
        row.AcknowledgedReason = null;
        row.AcknowledgedUntil = null;
        row.AcknowledgedKinds = [];
        row.AcknowledgementEndedAt = now;
        row.AcknowledgementEndedReason = Cuts.Cut(why, 300);
    }

    // --- loading -------------------------------------------------------------------------------------------------------------

    /// <summary>A server's pending source updates: per source package and candidate, with its binaries; new packages fix nothing here.</summary>
    public static async Task<List<OsPendingSource>> PendingAsync(PnmtdDbContext db, Guid sensorId, CancellationToken ct)
    {
        var rows = await db.OsPendingPackages.AsNoTracking().Where(p => p.SensorId == sensorId && !p.Phased && p.Installed != null).ToListAsync(ct);
        return rows.GroupBy(p => (p.Source, p.SourceVersion))
            .Select(g => new OsPendingSource(g.Key.Source, g.Select(p => p.InstalledSourceVersion).Where(v => v is not null).OrderBy(v => v, DebianVersionComparer.Instance).FirstOrDefault(),
                g.Key.SourceVersion, g.Select(p => p.Name).Distinct().Order(StringComparer.Ordinal).ToList(), g.Any(p => p.Security)))
            .ToList();
    }

    public static async Task<OsAdvisoryFacts> FactsAsync(PnmtdDbContext db, string distribution, IReadOnlyList<OsPendingSource> pending, CancellationToken ct)
    {
        var sources = pending.Select(p => p.Source).Distinct().ToList();
        var fixes = await db.OsFixes.AsNoTracking().Where(f => f.Distribution == distribution && sources.Contains(f.SourcePackage))
            .Join(db.OsAdvisorySources.Where(s => s.Enabled), f => f.SourceId, s => s.Id, (f, s) => new { f.SourcePackage, f.Cve, f.FixedVersion, f.Urgency, f.Scope, s.Name, s.Kind })
            .ToListAsync(ct);
        var changelogs = await db.OsPackageChangelogs.AsNoTracking().Where(c => c.Distribution == distribution && sources.Contains(c.SourcePackage)).ToListAsync(ct);
        var cves = fixes.Select(f => f.Cve).Concat(changelogs.SelectMany(c => c.Cves)).Distinct().ToList();
        var risks = await db.OsCveRisks.AsNoTracking().Where(r => cves.Contains(r.Cve)).ToDictionaryAsync(r => r.Cve, ct);
        var items = await db.OsAdvisoryItems.AsNoTracking().Where(i => i.Cves.Any(c => cves.Contains(c)))
            .Join(db.OsAdvisorySources.Where(s => s.Enabled), i => i.SourceId, s => s.Id, (i, s) => new { Item = i, s.Name }).ToListAsync(ct);
        var claims = new List<OsAdvisoryClaimFact>();
        var keywords = new List<(string, string, string, string?)>();
        foreach (var (item, sourceName) in items.Select(x => (x.Item, x.Name)))
        {
            var assessment = OsAdvisoryAssessment.Read(item.Assessment);
            if (assessment is not null && item.AssessedBy is "verified" or "disputed" or "ai")
            {
                claims.AddRange(assessment.Claims.Where(c => c.ExploitedInTheWild || c.ZeroDay).Select(c => new OsAdvisoryClaimFact(c.Cve, item.AssessedBy == "verified"
                    && assessment.Verdicts.GetValueOrDefault(c.Cve) == "agree", c.ExploitedInTheWild, c.ZeroDay, sourceName, c.Quote, item.Link)));
            }
            else if (item.Keywords.Any(OsAdvisoryTexts.IsExploitKeyword))
            {
                var keyword = item.Keywords.First(OsAdvisoryTexts.IsExploitKeyword);
                keywords.AddRange(item.Cves.Select(c => (c, sourceName, keyword, item.Link)));
            }
        }
        return new OsAdvisoryFacts(fixes.Select(f => (f.Name, f.SourcePackage, f.Cve, f.FixedVersion, f.Urgency, f.Scope, f.Kind)).ToList(), changelogs, risks, claims, keywords);
    }

    // --- rating (pure) ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// §6.3, §6.4: each pending source update's CVEs (a tracker's fix with installed &lt; fixed ≤ candidate, or named in the changelog since the
    /// installed version) rated high risk (KEV, EPSS percentile ≥ the high threshold, the tracker's urgency high and not local, a changelog
    /// urgency of emergency or critical, a verified AI claim) or important (from a security archive with EPSS ≥ the important threshold or the
    /// tracker's urgency high or medium or a changelog urgency of high; a keyword in an advisory text; an unverified AI claim). Unrated CVEs
    /// are no finding.
    /// </summary>
    public static List<OsRatedFinding> Rate(IReadOnlyList<OsPendingSource> pending, OsAdvisoryFacts facts, double highPercentile, double importantPercentile)
    {
        var findings = new List<OsRatedFinding>();
        foreach (var update in pending.Where(p => p.Installed is not null))
        {
            var cves = new Dictionary<string, List<OsFindingReason>>(StringComparer.Ordinal);
            var trackerFacts = new Dictionary<string, List<(string Source, string? Urgency, string? Scope)>>(StringComparer.Ordinal);
            foreach (var fix in facts.Fixes.Where(f => f.SourcePackage == update.Source))
            {
                if (fix.FixedVersion is "0" or "" || !DebianVersion.IsOlder(update.Installed, fix.FixedVersion) || DebianVersion.IsNewer(fix.FixedVersion, update.Candidate))
                {
                    continue;
                }
                cves.TryAdd(fix.Cve, []);
                (trackerFacts.TryGetValue(fix.Cve, out var list) ? list : trackerFacts[fix.Cve] = []).Add((fix.Source, fix.Kind == OsAdvisorySourceKind.DebianTracker ? fix.Urgency : null, fix.Scope));
            }
            var changelogUrgencies = new List<string>();
            foreach (var changelog in facts.Changelogs.Where(c => c.SourcePackage == update.Source && c.Version == update.Candidate))
            {
                var (_, named, urgencies) = changelog.InstalledVersion == update.Installed
                    ? (changelog.Text, changelog.Cves, changelog.Urgencies)
                    : DebianChangelog.Since(changelog.Text, update.Installed);
                foreach (var cve in named)
                {
                    cves.TryAdd(cve, []);
                }
                changelogUrgencies.AddRange(urgencies);
            }
            foreach (var (cve, reasons) in cves)
            {
                if (facts.Risks.TryGetValue(cve, out var risk))
                {
                    if (risk.Kev)
                    {
                        reasons.Add(new OsFindingReason("kev", true, "CISA KEV",
                            $"exploited in the wild (CISA KEV since {risk.KevAddedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?"}{(risk.KevRansomware ? ", used by ransomware" : "")})",
                            "https://www.cisa.gov/known-exploited-vulnerabilities-catalog?search_api_fulltext=" + cve));
                    }
                    if (risk.EpssPercentile is { } p && risk.Epss is { } score)
                    {
                        var detail = $"EPSS {score.ToString("0.00", CultureInfo.InvariantCulture)}, top {Math.Max(0.1, (1 - p) * 100).ToString("0.#", CultureInfo.InvariantCulture)} %";
                        if (p >= highPercentile)
                        {
                            reasons.Add(new OsFindingReason("epss", true, "EPSS", detail, "https://www.first.org/epss/"));
                        }
                        else if (p >= importantPercentile && update.Security)
                        {
                            reasons.Add(new OsFindingReason("epss", false, "EPSS", detail, "https://www.first.org/epss/"));
                        }
                    }
                }
                foreach (var (source, urgency, scope) in trackerFacts.GetValueOrDefault(cve) ?? [])
                {
                    if (urgency == "high" && scope != "local")
                    {
                        reasons.Add(new OsFindingReason("urgency", true, source, "urgency high, not local", TrackerLink(cve)));
                    }
                    else if (urgency is "high" or "medium" && update.Security)
                    {
                        reasons.Add(new OsFindingReason("urgency", false, source, $"urgency {urgency}{(scope == "local" ? ", local" : "")}", TrackerLink(cve)));
                    }
                }
                foreach (var claim in facts.Claims.Where(c => c.Cve == cve))
                {
                    var what = claim.ZeroDay ? "a zero-day" : "exploited in the wild";
                    reasons.Add(new OsFindingReason("ai", claim.Verified, claim.SourceName,
                        $"{what} per {claim.SourceName} ({(claim.Verified ? "AI, verified" : "AI, disputed")}): \"{Cuts.Cut(claim.Quote, 300)}\"", claim.Link));
                }
                foreach (var (_, sourceName, keyword, link) in facts.KeywordHits.Where(k => k.Cve == cve))
                {
                    reasons.Add(new OsFindingReason("text", false, sourceName, $"its advisory says \"{keyword}\" (not verified)", link));
                }
                if (reasons.Count == 0)
                {
                    continue;
                }
                findings.Add(new OsRatedFinding(update.Source, cve, update.Packages, update.Installed, update.Candidate,
                    reasons.Any(r => r.High) ? OsFindingRating.HighRisk : OsFindingRating.Important, reasons.DistinctBy(r => (r.Kind, r.Source, r.High)).ToList()));
            }
            // The changelog's own urgency is about the upload, not one CVE: it raises every CVE it names.
            if (changelogUrgencies.Any(u => HighUrgencies.Contains(u)) || changelogUrgencies.Contains("high"))
            {
                var high = changelogUrgencies.Any(u => HighUrgencies.Contains(u));
                var urgency = changelogUrgencies.FirstOrDefault(u => HighUrgencies.Contains(u)) ?? "high";
                foreach (var cve in cves.Keys)
                {
                    var existing = findings.FindIndex(f => f.SourcePackage == update.Source && f.Cve == cve);
                    var reason = new OsFindingReason("changelog", high, "changelog", $"urgency={urgency} in its changelog", null);
                    if (!high && !update.Security)
                    {
                        continue;
                    }
                    if (existing >= 0)
                    {
                        var f = findings[existing];
                        findings[existing] = f with { Rating = high ? OsFindingRating.HighRisk : f.Rating, Reasons = [.. f.Reasons, reason] };
                    }
                    else
                    {
                        findings.Add(new OsRatedFinding(update.Source, cve, update.Packages, update.Installed, update.Candidate,
                            high ? OsFindingRating.HighRisk : OsFindingRating.Important, [reason]));
                    }
                }
            }
        }
        return findings;
    }

    private static string TrackerLink(string cve) => "https://security-tracker.debian.org/tracker/" + cve;

    // --- 466's message --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// "web-01: 2 high-risk updates pending: openssl 3.0.22-1~deb12u2 (CVE-2026-1234: exploited in the wild (CISA KEV since 2026-11-18)), sudo …
    /// (1 more acknowledged)". Unacknowledged high-risk findings only, by source package (at most 5, then "and 3 more"); stable while the same
    /// findings are pending (EPSS by its threshold, not its daily score), so it collapses by the dedupe. Null when there is none.
    /// </summary>
    public static string? Message(string hostname, IReadOnlyList<OsUpdateFindingEntity> open, DateTime now, double highPercentile)
    {
        var high = open.Where(f => f.Rating == OsFindingRating.HighRisk && f.ResolvedAt is null).ToList();
        var active = high.Where(f => !f.IsAcknowledged(now)).ToList();
        if (active.Count == 0)
        {
            return null;
        }
        var acknowledged = high.Count - active.Count;
        var packages = active.GroupBy(f => (f.SourcePackage, f.Candidate)).OrderBy(g => g.Key.SourcePackage, StringComparer.Ordinal).ToList();
        var parts = packages.Take(5).Select(g =>
        {
            var cves = g.OrderBy(f => f.Cve, StringComparer.Ordinal).ToList();
            var described = cves.Take(3).Select(f => $"{f.Cve}: {Short(f, highPercentile)}");
            return $"{g.Key.SourcePackage} {g.Key.Candidate} ({string.Join("; ", described)}{(cves.Count > 3 ? $"; {cves.Count - 3} more CVEs" : "")})";
        }).ToList();
        var count = packages.Count == 1 ? "1 high-risk update pending" : $"{packages.Count} high-risk updates pending";
        var more = packages.Count > 5 ? $", and {packages.Count - 5} more" : "";
        var ended = active.Where(f => f.AcknowledgementEndedAt is { } at && now - at < EndedNoteFor).Select(f => $"{f.Cve}: {f.AcknowledgementEndedReason}").Take(3).ToList();
        var message = $"{hostname}: {count}: {string.Join(", ", parts)}{more}"
                      + (acknowledged > 0 ? $" ({acknowledged} more acknowledged)" : "")
                      + (ended.Count > 0 ? $" (acknowledgement ended: {string.Join("; ", ended)})" : "");
        return Cuts.Cut(message, 2000);
    }

    /// <summary>A finding's strongest reason, worded stably for the message.</summary>
    private static string Short(OsUpdateFindingEntity finding, double highPercentile)
    {
        var reasons = Reasons(finding.Reasons).Where(r => r.High).ToList();
        var first = reasons.FirstOrDefault(r => r.Kind == "kev") ?? reasons.FirstOrDefault(r => r.Kind == "ai") ?? reasons.FirstOrDefault(r => r.Kind == "epss")
                    ?? reasons.FirstOrDefault();
        return first?.Kind switch
        {
            "kev" => first.Detail,
            "epss" => $"likely to be exploited (EPSS, top {Math.Max(0.1, (1 - highPercentile) * 100).ToString("0.#", CultureInfo.InvariantCulture)} %)",
            "ai" => $"exploited per {first.Source} (verified)",
            "urgency" => $"urgency high ({first.Source})",
            "changelog" => first.Detail,
            _ => "high risk",
        };
    }

    public static List<OsFindingReason> Reasons(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<OsFindingReason>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>dpkg's order for sorting versions.</summary>
public sealed class DebianVersionComparer : IComparer<string?>
{
    public static DebianVersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y) => DebianVersion.Compare(x, y);
}
