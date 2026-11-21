using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.OsUpdates;

/// <summary>The input of <c>os.advisory.rate</c> (§7.1): the source's name and the text's CVEs (code's), and the text in fields named untrusted.</summary>
public sealed record OsAdvisoryInput(string SourceName, string? PublishedAt, IReadOnlyList<string> CvesFoundByCode, string UntrustedTitle, string UntrustedText);

/// <summary>One claim of the answer (§7.1, step 4).</summary>
public sealed record OsAdvisoryClaimOutput(
    [property: Description("One of cvesFoundByCode, exactly")] string Cve,
    [property: Description("Package names exactly as the text writes them; at most 10")] IReadOnlyList<string> Packages,
    [property: Description("The text says the vulnerability is exploited in the wild or actively exploited")] bool ExploitedInTheWild,
    [property: Description("The text says it was a zero-day: exploited before a fix existed")] bool ZeroDay,
    [property: Description("Exploitable remotely without credentials, per the text")] bool RemoteWithoutCredentials,
    [property: Description("At most 300 characters copied word for word from the text that support the claim, and contain the CVE or a package name")] string Quote);

public sealed record OsAdvisoryOutput(
    [property: Description("At most 20 claims; only CVEs the text says are exploited, zero-days, or remotely exploitable without credentials")] IReadOnlyList<OsAdvisoryClaimOutput> Claims,
    [property: Description("At most 400 characters")] string Summary);

/// <summary>
/// What an advisory text says about the exploitation of its CVEs (§7.1). Only claims; PNMT's code decides which servers they concern. The
/// verification model (another vendor) judges the claims as a whole; only its agreement lets them raise a finding to high risk.
/// </summary>
public sealed class OsAdvisoryRateTask(int maxTokens) : AiTask<OsAdvisoryInput, OsAdvisoryOutput>(maxTokens)
{
    public const string TaskPurpose = "os.advisory.rate";

    public override string Purpose => TaskPurpose;

    public override string PromptVersion => "os-advisory-rate-1";

    public override bool Verify => true;

    public override string VerifyQuestion =>
        "Does the advisory text itself say, for each claim, that the CVE is exploited in the wild or was a zero-day, as claimed, and is each quote taken from it word for word?";

    protected override string Instructions =>
        "Read a security advisory about Linux packages. For each CVE in cvesFoundByCode that the text says is exploited in the wild, actively " +
        "exploited, a zero-day, or exploitable remotely without credentials, return one claim with a quote copied word for word from the text. " +
        "Use only CVEs from cvesFoundByCode. Do not guess: if the text doesn't say it, return no claim for that CVE. An empty list is a valid answer.";

    public override string? Validate(OsAdvisoryOutput output, OsAdvisoryInput input)
    {
        var unknown = output.Claims.FirstOrDefault(c => !input.CvesFoundByCode.Contains(c.Cve, StringComparer.OrdinalIgnoreCase));
        return unknown is not null ? $"{unknown.Cve} is not among the CVEs of the text."
            : output.Claims.Count > 20 ? "more than 20 claims."
            : output.Summary.Length > 800 ? "summary too long." : null;
    }

    /// <summary>The most characters between a claim's quote and a mention of its CVE: the same paragraph, roughly.</summary>
    public const int NearChars = 400;

    /// <summary>
    /// Code's checks of each claim (§7.1, step 5): its quote (at most 300 characters) occurs word for word in the text, within
    /// <see cref="NearChars"/> of a mention of its CVE (advisories say "this issue is exploited" in a sentence of its own); each package
    /// occurs in the text. A claim that fails is dropped, with the reason.
    /// </summary>
    public static (List<OsAdvisoryClaim> Kept, List<string> Dropped) Check(OsAdvisoryOutput output, string text)
    {
        var kept = new List<OsAdvisoryClaim>();
        var dropped = new List<string>();
        foreach (var claim in output.Claims.Take(20))
        {
            var cve = claim.Cve.ToUpperInvariant();
            var quote = claim.Quote.Trim();
            if (quote.Length is 0 or > 300 || !text.Contains(quote, StringComparison.Ordinal))
            {
                dropped.Add($"{cve}: its quote isn't in the text word for word");
                continue;
            }
            var packages = claim.Packages.Take(10).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (packages.FirstOrDefault(p => !text.Contains(p, StringComparison.OrdinalIgnoreCase)) is { } missing)
            {
                dropped.Add($"{cve}: the package {Cuts.Cut(missing, 100)} isn't in the text");
                continue;
            }
            if (!NearCve(text, quote, cve))
            {
                dropped.Add($"{cve}: its quote is not near a mention of the CVE");
                continue;
            }
            kept.Add(new OsAdvisoryClaim(cve, packages, claim.ExploitedInTheWild, claim.ZeroDay, claim.RemoteWithoutCredentials, quote));
        }
        return (kept, dropped);
    }

    /// <summary>Whether some occurrence of the quote is within <see cref="NearChars"/> of some mention of the CVE.</summary>
    public static bool NearCve(string text, string quote, string cve)
    {
        var quotes = Occurrences(text, quote, StringComparison.Ordinal);
        var mentions = Occurrences(text, cve, StringComparison.OrdinalIgnoreCase);
        return quotes.Any(q => mentions.Any(m => m + cve.Length >= q - NearChars && m <= q + quote.Length + NearChars));
    }

    private static List<int> Occurrences(string text, string value, StringComparison comparison)
    {
        var found = new List<int>();
        for (var at = text.IndexOf(value, comparison); at >= 0 && found.Count < 100; at = text.IndexOf(value, at + 1, comparison))
        {
            found.Add(at);
        }
        return found;
    }
}

/// <summary>
/// AI for advisory texts (§7): which items go to it, and its work handler. An item goes only with AI on (<c>OsUpdates__Advisories__Ai</c>
/// and <c>Ai__Enabled</c>), a CVE and an exploit keyword in it, and at least one of its CVEs fixed by an update pending somewhere; each
/// item once. The answer is checked by code; a guard hit marks the item suspicious and no model sees it; the verification model's
/// agreement makes the claims verified, anything else disputed. Then the evaluator looks again.
/// </summary>
public sealed class OsAdvisoryRating(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<OsUpdatesOptions> options, IOptionsMonitor<AiOptions> ai,
    AiWorkQueue queue, ILogger<OsAdvisoryRating> logger)
{
    public bool Enabled => options.CurrentValue is { Enabled: true, Advisories: { Enabled: true, Ai: true } } && ai.CurrentValue.Usable;

    /// <summary>Items waiting for a server that needs them (or new ones): queued when a pending update fixes one of their CVEs.</summary>
    public async Task ConsiderAsync(IReadOnlyList<Guid>? itemIds, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        var query = db.OsAdvisoryItems.Where(i => i.AssessedBy == "code" && i.Cves.Count > 0);
        if (itemIds is not null)
        {
            query = query.Where(i => itemIds.Contains(i.Id));
        }
        var items = await query.OrderByDescending(i => i.FirstSeenAt).Take(200).ToListAsync(ct);
        var candidates = items.Where(i => i.Keywords.Any(OsAdvisoryTexts.IsExploitKeyword)).ToList();
        if (candidates.Count == 0)
        {
            return;
        }
        // The CVEs some pending update fixes, by the sources' fixes and the changelogs (code decides; versions are the evaluator's).
        var cves = candidates.SelectMany(i => i.Cves).Distinct().ToList();
        var pendingSources = db.OsPendingPackages.Join(db.Agents, p => p.SensorId, a => a.SensorId, (p, a) => new { p.Source, a.OsDistribution });
        var needed = await db.OsFixes.Where(f => cves.Contains(f.Cve))
            .Where(f => pendingSources.Any(p => p.Source == f.SourcePackage && p.OsDistribution == f.Distribution))
            .Select(f => f.Cve).Distinct().ToListAsync(ct);
        needed.AddRange(await db.OsPackageChangelogs.Where(c => c.Cves.Any(x => cves.Contains(x)))
            .Where(c => pendingSources.Any(p => p.Source == c.SourcePackage && p.OsDistribution == c.Distribution))
            .SelectMany(c => c.Cves).Where(x => cves.Contains(x)).Distinct().ToListAsync(ct));
        foreach (var item in candidates.Where(i => i.Cves.Any(needed.Contains)))
        {
            item.AiWorkId = await queue.EnqueueAsync(OsAdvisoryRateTask.TaskPurpose, "os-advisory-item", item.Id, item.Hash, ct);
            item.AssessedBy = "pending";
            logger.LogInformation("OS advisories: {Title} goes to the AI ({Cves})", item.Title, string.Join(", ", item.Cves.Take(5)));
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary><c>os.advisory.rate</c>: the pipeline for one item, its claims checked and stored (§7.1).</summary>
public sealed class OsAdvisoryRateHandler(IDbContextFactory<PnmtdDbContext> dbs, AiPipeline pipeline, IOptionsMonitor<AiOptions> ai, OsAdvisoryNudge nudge) : IAiWorkHandler
{
    public string Purpose => OsAdvisoryRateTask.TaskPurpose;

    public async Task<string?> RunAsync(AiWorkEntity work, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var item = await db.OsAdvisoryItems.SingleOrDefaultAsync(i => i.Id == work.SubjectId, ct) ?? throw new AiWorkStaleException("The advisory is gone.");
        var source = await db.OsAdvisorySources.AsNoTracking().Where(s => s.Id == item.SourceId).Select(s => s.Name).SingleOrDefaultAsync(ct) ?? "an advisory feed";
        var input = new OsAdvisoryInput(source, item.PublishedAt?.ToString("yyyy-MM-dd"), item.Cves, item.Title, item.Text);
        var result = await pipeline.RunAsync(new OsAdvisoryRateTask(ai.CurrentValue.Tokens.OsAdvisoryRate), input, [item.Title, item.Text],
            new AiCall(Purpose, work.Id, work.Attempts + 1), ct);
        if (result.Blocked)
        {
            // A guard or scan hit: no model saw it, and the code's reading stands (a keyword makes it important at most).
            item.AssessedBy = "suspicious";
            item.Assessment = new OsAdvisoryAssessment([], $"Stopped by the {result.BlockedBy}: the text may try to instruct an AI.", new Dictionary<string, string>(), [],
                result.Injection).ToJson();
            item.AssessedAt = Clock.UtcNow;
            await db.SaveChangesAsync(ct);
            nudge.All();
            return JsonSerializer.Serialize(new { blocked = result.BlockedBy });
        }
        var output = result.Output!;
        var (kept, dropped) = OsAdvisoryRateTask.Check(output, item.Text);
        var verdict = result.Verdict?.Answer switch
        {
            VerdictKind.Agree => "agree",
            VerdictKind.Disagree => "disagree",
            _ => "unsure",
        };
        var exploited = kept.Where(c => c.ExploitedInTheWild || c.ZeroDay).ToList();
        item.AssessedBy = exploited.Count == 0 ? "ai" : verdict == "agree" ? "verified" : "disputed";
        item.Assessment = new OsAdvisoryAssessment(kept, Cuts.Cut(output.Summary, 800), kept.DistinctBy(c => c.Cve).ToDictionary(c => c.Cve, _ => verdict),
            [.. dropped, .. result.Verdict?.Issues.Select(i => "verifier: " + Cuts.Cut(i, 200)) ?? []], result.Injection).ToJson();
        item.AssessedAt = Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        nudge.All();
        return JsonSerializer.Serialize(new { item.AssessedBy, claims = kept.Count, dropped = dropped.Count });
    }
}
