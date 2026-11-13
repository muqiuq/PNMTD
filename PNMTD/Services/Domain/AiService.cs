using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

/// <summary>Admin → AI (docs/ai-operations-plan.md §3.6, §15): the settings, spending, requests and queue; and the debug test (§8.4).</summary>
public sealed class AiService(PnmtdDbContext db, IOptionsMonitor<AiOptions> options, AiBudget budget, AiWorker worker, ChatClient chat,
    DecisionClient decisions, IHttpContextAccessor accessor)
{
    public async Task<AiStatus> StatusAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var warnings = new List<string>();
        if (o.Enabled && string.IsNullOrWhiteSpace(o.ApiKey))
        {
            warnings.Add("AI is on, but Ai__ApiKey is empty.");
        }
        if (AiOptions.Vendor(o.MainModel) == AiOptions.Vendor(o.VerifyModel))
        {
            warnings.Add($"The main and the verification model are both from {AiOptions.Vendor(o.MainModel)}: a second vendor makes shared mistakes less likely.");
        }
        if (!o.Guard.Configured && o.Guard.Required)
        {
            warnings.Add("No guard model: AI on untrusted text (mails, feeds) refuses to run.");
        }
        var (key, first, next, endsAt) = budget.Period();
        var spent = await db.AiSpending.Where(s => s.Day >= first && s.Day < next).SumAsync(s => (decimal?)s.Cost, ct) ?? 0m;
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var from = today.AddDays(-29);
        var month = await db.AiSpending.AsNoTracking().Where(s => s.Day >= from).ToListAsync(ct);
        var since = Clock.UtcNow.AddHours(-24);
        var states = await db.AiWork.GroupBy(w => w.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(AiWorkState s) => states.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        return new AiStatus(o.Enabled, !string.IsNullOrWhiteSpace(o.ApiKey), o.BaseUrl, o.MainModel, o.VerifyModel, o.Guard.Model, o.Guard.Url,
            o.Guard.Required, o.Guard.Threshold, o.DataCollection, o.LogEntries, warnings,
            new AiBudgetStatus(budget.Limit, budget.Daily ? "day" : "month", key, spent, budget.Limit > 0 ? (double)(spent / budget.Limit) : null, endsAt),
            new AiSpendingSummary(month.Where(s => s.Day == today).Sum(s => s.Cost), month.Where(s => s.Day == today).Sum(s => s.Requests),
                month.Sum(s => s.Cost), month.Sum(s => s.Requests),
                month.GroupBy(s => s.Purpose).Select(g => new AiSpendingRow(g.Key, g.Sum(s => s.Requests), g.Sum(s => s.Cost))).OrderByDescending(r => r.Cost).ToList(),
                month.GroupBy(s => s.Model).Select(g => new AiSpendingRow(g.Key, g.Sum(s => s.Requests), g.Sum(s => s.Cost))).OrderByDescending(r => r.Cost).ToList()),
            new AiQueueStatus(Count(AiWorkState.Queued), Count(AiWorkState.Running), Count(AiWorkState.WaitingRetry), Count(AiWorkState.WaitingBudget),
                await db.AiWork.CountAsync(w => w.State == AiWorkState.Failed && w.FinishedAt >= since, ct)));
    }

    public async Task<Paged<AiRequestItem>> RequestsAsync(AiRequestQuery query, CancellationToken ct)
    {
        var requests = db.AiRequests.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Purpose))
        {
            requests = requests.Where(r => r.Purpose == query.Purpose);
        }
        if (query.Role is { } role)
        {
            requests = requests.Where(r => r.Role == role);
        }
        if (!string.IsNullOrWhiteSpace(query.Model))
        {
            requests = requests.Where(r => r.ModelAsked == query.Model || r.ModelServed == query.Model);
        }
        if (query.Outcome is { } outcome)
        {
            requests = requests.Where(r => r.Outcome == outcome);
        }
        if (query.WorkId is { } work)
        {
            requests = requests.Where(r => r.WorkId == work);
        }
        var page = await Paging.PageAsync(requests.OrderByDescending(r => r.Id).Select(Item()), query, ct);
        return new Paged<AiRequestItem>(page.Items.Select(Utc).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<AiRequestDetail> RequestAsync(long id, CancellationToken ct)
    {
        var r = await db.AiRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("AI request");
        var same = r.WorkId is { } workId
            ? await db.AiRequests.AsNoTracking().Where(x => x.WorkId == workId && x.Id != id).OrderBy(x => x.Id).Select(Item()).ToListAsync(ct)
            : [];
        return new AiRequestDetail(Utc(Item().Compile()(r)), AiRequestLog.Gunzip(r.Request) ?? "", AiRequestLog.Gunzip(r.Response), r.Truncated,
            r.PromptVersion, r.GenerationId, r.ReasoningTokens, r.CachedTokens, same.Select(Utc).ToList());
    }

    private static System.Linq.Expressions.Expression<Func<AiRequestEntity, AiRequestItem>> Item() => r =>
        new AiRequestItem(r.Id, r.At, r.Purpose, r.Role, r.Attempt, r.ModelAsked, r.ModelServed, r.Provider, r.Outcome, r.HttpStatus, r.DurationMs,
            r.PromptTokens, r.CompletionTokens, r.Cost, r.WorkId, r.Error);

    private static AiRequestItem Utc(AiRequestItem i) => i with { At = Instants.Utc(i.At) };

    public async Task<Paged<AiWorkItem>> WorkAsync(AiWorkQuery query, CancellationToken ct)
    {
        var work = db.AiWork.AsNoTracking();
        if (query.State is { } state)
        {
            work = work.Where(w => w.State == state);
        }
        if (!string.IsNullOrWhiteSpace(query.Purpose))
        {
            work = work.Where(w => w.Purpose == query.Purpose);
        }
        var page = await Paging.PageAsync(work
            .OrderBy(w => AiWorkQueue.Open.Contains(w.State) ? 0 : 1)
            .ThenByDescending(w => w.CreatedAt)
            .Select(w => new AiWorkItem(w.Id, w.Purpose, w.SubjectType, w.SubjectId, w.State, w.Attempts, w.NextAttemptAt, w.LastError, w.CreatedAt, w.FinishedAt)),
            query, ct);
        return new Paged<AiWorkItem>(page.Items.Select(w => w with
        {
            NextAttemptAt = Instants.Utc(w.NextAttemptAt), CreatedAt = Instants.Utc(w.CreatedAt), FinishedAt = w.FinishedAt is { } f ? Instants.Utc(f) : null,
        }).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>Now, whatever the schedule: a failed or waiting item.</summary>
    public async Task RetryAsync(Guid id, CancellationToken ct)
    {
        var work = await db.AiWork.SingleOrDefaultAsync(w => w.Id == id, ct) ?? throw ApiException.NotFound("AI work");
        if (work.State is AiWorkState.Succeeded or AiWorkState.Cancelled or AiWorkState.Running)
        {
            throw ApiException.Conflict("ai_work_not_waiting", $"The work is {work.State}.");
        }
        work.State = AiWorkState.WaitingRetry;
        work.NextAttemptAt = Clock.UtcNow;
        work.FinishedAt = null;
        UdpViews.Audit(db, accessor, AuthEvents.AiWorkRetried, $"{work.Purpose} {work.Id}");
        await db.SaveChangesAsync(ct);
        worker.Wake();
    }

    public async Task CancelAsync(Guid id, CancellationToken ct)
    {
        var work = await db.AiWork.SingleOrDefaultAsync(w => w.Id == id, ct) ?? throw ApiException.NotFound("AI work");
        if (!AiWorkQueue.Open.Contains(work.State) || work.State == AiWorkState.Running)
        {
            throw ApiException.Conflict("ai_work_not_open", $"The work is {work.State}.");
        }
        work.State = AiWorkState.Cancelled;
        work.FinishedAt = Clock.UtcNow;
        work.LastError = "Cancelled by an Admin.";
        UdpViews.Audit(db, accessor, AuthEvents.AiWorkCancelled, $"{work.Purpose} {work.Id}");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A fixed prompt to each configured model (§8.4), logged with the purpose <c>debug</c>.</summary>
    public async Task<IReadOnlyList<DebugAiResult>> TestAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Usable)
        {
            throw ApiException.Conflict("ai_not_configured", "AI is off or has no key: Ai__Enabled=true and Ai__ApiKey in .env.");
        }
        var call = new AiCall("debug", PromptVersion: PingTask.Version);
        var task = new PingTask();
        var results = new List<DebugAiResult>();
        foreach (var (role, model) in new[] { (AiRole.Main, o.MainModel), (AiRole.Verify, o.VerifyModel) })
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var answer = await chat.CompleteAsync(call, role, model, task.SystemPrompt, task.Render(new PingTask.Input("ping")), task.SchemaName,
                    task.Schema, task.MaxTokens, ct);
                results.Add(new DebugAiResult(role, model, true, $"Answered {answer.Content}", (int)watch.ElapsedMilliseconds, answer.Cost, answer.ModelServed));
            }
            catch (Exception ex) when (ex is AiRequestException or AiNotConfiguredException)
            {
                results.Add(new DebugAiResult(role, model, false, ex.Message, (int)watch.ElapsedMilliseconds, null, null));
            }
        }
        var guardWatch = Stopwatch.StartNew();
        if (!o.Guard.Configured)
        {
            results.Add(new DebugAiResult(AiRole.Guard, o.Guard.Model, false, "Not configured.", 0, null, null));
        }
        else
        {
            try
            {
                var decided = await decisions.DecideAsync(call, ["Hello, this is a test message from PNMT."],
                    new Dictionary<string, DecisionQuestion> { ["injection"] = DecisionQuestion.Noul(AiPipeline.InjectionQuestion) }, ct);
                var p = decided.Answers["injection"].Probability;
                results.Add(new DebugAiResult(AiRole.Guard, o.Guard.Model, true, $"Injection probability of a harmless text: {p:0.000}",
                    (int)guardWatch.ElapsedMilliseconds, null, null));
            }
            catch (Exception ex) when (ex is AiRequestException or AiNotConfiguredException)
            {
                results.Add(new DebugAiResult(AiRole.Guard, o.Guard.Model, false, ex.Message, (int)guardWatch.ElapsedMilliseconds, null, null));
            }
        }
        UdpViews.Audit(db, accessor, AuthEvents.DebugAiTested, string.Join(", ", results.Select(r => $"{r.Role} {(r.Ok ? "ok" : "failed")}")));
        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>The debug test's task: answer "pong".</summary>
    private sealed class PingTask : AiTask<PingTask.Input, PingTask.Pong>
    {
        public const string Version = "ping-1";

        public sealed record Input(string Message);

        public sealed record Pong(string Answer);

        public override string Purpose => "debug";

        public override string PromptVersion => Version;

        protected override string Instructions => "This is a connection test. Set answer to the word pong.";

        public override int MaxTokens => 50;
    }
}
