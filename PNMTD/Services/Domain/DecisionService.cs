using PNMTD.Auth.Rights;
using Microsoft.EntityFrameworkCore;
using PNMTD.Ai;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Decisions;
using PNMTD.Lib.Helper;
using PNMTD.Mails;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

/// <summary>
/// Monitoring → Decisions (docs/ai-operations-plan.md §5.3): the decisions a role may make, their options and evidence,
/// and deciding. Users decide the mail kinds; options marked Admin need an Admin.
/// </summary>
public sealed class DecisionService(PnmtdDbContext db, IEnumerable<IDecisionKind> kinds, MailService mail, AiWorkQueue queue, IHttpContextAccessor accessor)
{
    private bool IsAdmin => accessor.HttpContext?.User.IsAdmin() ?? false;

    private IQueryable<DecisionEntity> Visible() =>
        IsAdmin ? db.Decisions.AsNoTracking() : db.Decisions.AsNoTracking().Where(d => !d.AdminOnly);

    public async Task<Paged<DecisionItem>> ListAsync(DecisionQuery query, CancellationToken ct)
    {
        var decisions = Visible();
        if (query.State is { } state)
        {
            decisions = decisions.Where(d => d.State == state);
        }
        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            decisions = decisions.Where(d => d.Kind == query.Kind);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            decisions = decisions.Where(d => EF.Functions.ILike(d.Title, pattern, @"\") || EF.Functions.ILike(d.Summary, pattern, @"\"));
        }
        var page = await Paging.PageAsync(decisions
            .OrderBy(d => d.State == DecisionState.Open ? 0 : 1)
            .ThenByDescending(d => d.Priority)
            .ThenByDescending(d => d.LastSeenAt), query, ct);
        return new Paged<DecisionItem>(page.Items.Select(Item).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<DecisionCount> CountAsync(CancellationToken ct) => new(await Visible().CountAsync(d => d.State == DecisionState.Open, ct));

    public async Task<DecisionDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var decision = await Visible().SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiException.NotFound("Decision");
        return Detail(decision);
    }

    public async Task<DecisionDetail> DecideAsync(Guid id, DecideRequest request, CancellationToken ct)
    {
        var decision = await db.Decisions.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiException.NotFound("Decision");
        if (decision.AdminOnly && !IsAdmin)
        {
            throw ApiException.NotFound("Decision");
        }
        if (decision.State != DecisionState.Open)
        {
            throw ApiException.Conflict("decision_closed", $"The decision is {decision.State.ToString().ToLowerInvariant()} already.");
        }
        var kind = Kind(decision);
        var option = kind.Options(decision).FirstOrDefault(o => o.Id == request.Choice)
            ?? throw ApiException.Invalid("choice", "Not an option of this decision.");
        if (!option.Enabled)
        {
            throw ApiException.Invalid("choice", option.Description);
        }
        if (option.AdminOnly && !IsAdmin)
        {
            throw ApiException.Invalid("choice", "Admins only.");
        }
        var (userId, userName) = UdpViews.User(accessor);
        try
        {
            decision.Result = await kind.ApplyAsync(decision, option.Id, request, new DecisionContext(db, mail, queue, IsAdmin), ct);
            decision.State = option.Id switch
            {
                "reject" => DecisionState.Rejected,
                "dismiss" => DecisionState.Dismissed,
                _ => DecisionState.Accepted,
            };
        }
        catch (ApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            decision.State = DecisionState.Failed;
            decision.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }
        decision.Choice = option.Id;
        decision.DecidedAt = Clock.UtcNow;
        decision.DecidedBy = userId;
        decision.DecidedByName = userName;
        decision.Edits = request.Rule is null ? null : System.Text.Json.JsonSerializer.Serialize(request.Rule, DecisionBook.Json);
        await db.SaveChangesAsync(ct);
        return Detail(decision);
    }

    private IDecisionKind Kind(DecisionEntity decision) =>
        kinds.FirstOrDefault(k => k.Kind == decision.Kind) ?? throw new InvalidOperationException($"No decision kind {decision.Kind}.");

    private DecisionDetail Detail(DecisionEntity d)
    {
        var options = kinds.FirstOrDefault(k => k.Kind == d.Kind)?.Options(d) ?? [];
        if (!IsAdmin)
        {
            options = options.Select(o => o.AdminOnly ? o with { Enabled = false } : o).ToList();
        }
        MailRuleProposal? rule = null;
        MailEvidence? mailEvidence = null;
        AiEvidence? ai = null;
        FindingEvidence? finding = null;
        if (d.Kind.StartsWith("mail.", StringComparison.Ordinal))
        {
            var payload = DecisionBook.Payload<MailDecisionPayload>(d);
            (rule, mailEvidence, ai) = (payload.Proposal, payload.Mail, payload.Ai);
        }
        else if (d.Kind == AnalysisFindingDecision.KindName)
        {
            var payload = DecisionBook.Payload<FindingPayload>(d);
            (finding, ai) = (payload.Finding, payload.Ai);
        }
        return new DecisionDetail(Item(d), d.State == DecisionState.Open ? options : [], rule, mailEvidence, ai, finding, d.Subjects, d.Result, d.Error);
    }

    private static DecisionItem Item(DecisionEntity d) =>
        new(d.Id, d.Kind, d.State, d.Title, d.Summary, d.Occurrences, d.Disputed, d.AdminOnly, Instants.Utc(d.CreatedAt), Instants.Utc(d.LastSeenAt),
            d.ExpiresAt is { } e ? Instants.Utc(e) : null, d.DecidedAt is { } a ? Instants.Utc(a) : null, d.DecidedByName, d.Choice);
}
