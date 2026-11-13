using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Ai.Analysis;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

/// <summary>The situation analysis's runs and findings (docs/ai-operations-plan.md §9.3), with the real names, and a run on demand.</summary>
public sealed class AiAnalysisService(PnmtdDbContext db, SituationScheduler scheduler, IOptionsMonitor<AiOptions> options, IHttpContextAccessor accessor)
{
    public async Task<Paged<AiAnalysisRunItem>> ListAsync(AiAnalysisQuery query, CancellationToken ct)
    {
        var runs = db.AiAnalysisRuns.AsNoTracking();
        if (query.State is { } state)
        {
            runs = runs.Where(r => r.State == state);
        }
        var page = await Paging.PageAsync(runs.OrderByDescending(r => r.At).Select(Item), query, ct);
        return new Paged<AiAnalysisRunItem>(page.Items, page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<AiAnalysisRunDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var run = await db.AiAnalysisRuns.AsNoTracking().Where(r => r.Id == id).Select(Item).SingleOrDefaultAsync(ct) ?? throw ApiException.NotFound("analysis run");
        var entity = await db.AiAnalysisRuns.AsNoTracking().Include(r => r.Findings).SingleAsync(r => r.Id == id, ct);
        var ids = entity.Findings.SelectMany(f => f.Sensors.Concat(f.Hosts)).Distinct().ToList();
        var names = await SituationHandler.NamesAsync(db, ids, ct);
        var findings = entity.Findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Muted).ThenByDescending(f => f.Confidence).Select(f => new AiFindingItem(
            f.Id, f.Key, f.Kind, f.Severity, f.Title, f.Explanation, f.Recommendation, f.Confidence, Refs(f.Sensors, names), Refs(f.Hosts, names), f.Evidence,
            f.Verified, f.Disputed, f.New, f.Muted, f.DecisionId)).ToList();
        var requests = entity.WorkId is { } work ? await db.AiRequests.Where(r => r.WorkId == work).OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(ct) : [];
        return new AiAnalysisRunDetail(run, findings, requests, entity.SnapshotChars);
    }

    public async Task<AiAnalysisQueued> RunAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Usable || !o.Analysis.Enabled)
        {
            throw ApiException.Conflict("ai_not_configured", "The situation analysis is off: Ai__Enabled, Ai__ApiKey and Ai__Analysis__Enabled.");
        }
        var id = await scheduler.QueueAsync("manual", null, ct);
        UdpViews.Audit(db, accessor, AuthEvents.AiAnalysisStarted, $"run {id}");
        await db.SaveChangesAsync(ct);
        return new AiAnalysisQueued(id);
    }

    private static IReadOnlyList<Ref> Refs(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, string> names) =>
        ids.Where(names.ContainsKey).Select(id => new Ref(id, names[id])).ToList();

    private static readonly Expression<Func<AiAnalysisRunEntity, AiAnalysisRunItem>> Item = r => new AiAnalysisRunItem(r.Id, r.At, r.Trigger, r.State,
        r.Status, r.Summary, r.Findings.Count(f => f.Severity == FindingSeverity.Critical && !f.Muted),
        r.Findings.Count(f => f.Severity == FindingSeverity.Warning && !f.Muted), r.Findings.Count(f => f.Severity == FindingSeverity.Info && !f.Muted),
        r.Withheld, r.GuardFlagged, r.CompletedAt, r.Error);
}
