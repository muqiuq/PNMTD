using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Models.Db;

namespace PNMTD.Services.Domain;

/// <summary>Mail input rules and the mail log (what MailInboxCheckTask read, and which rule took it).</summary>
public sealed class MailService(PnmtdDbContext db)
{
    public async Task<Paged<MailInputRule>> ListRulesAsync(PageQuery query, CancellationToken ct)
    {
        var rules = db.MailInputs.AsNoTracking();
        if (Text.Pattern(query.Search) is { } pattern)
        {
            rules = rules.Where(r => EF.Functions.ILike(r.Name, pattern, @"\"));
        }
        var (_, descending) = Paging.Sort(query.Sort, "name", "name");
        rules = descending ? rules.OrderByDescending(r => r.Name) : rules.OrderBy(r => r.Name);
        return await Paging.PageAsync(Project(rules), query, ct);
    }

    private static IQueryable<MailInputRule> Project(IQueryable<MailInputRuleEntity> rules) =>
        rules.Select(r => new MailInputRule(r.Id, r.Name, r.Enabled, r.FromTest, r.SubjectTest, r.BodyTest, r.OkTest, r.OkCode,
            r.FailTest, r.FailCode, r.DefaultCode, r.ExtractMessageRegex,
            r.SensorOutput != null
                ? new SensorRef(r.SensorOutput.Id, r.SensorOutput.Name, r.SensorOutput.ParentId, r.SensorOutput.Parent.Name)
                : null, r.Action, r.AskAiWhenUnsure, r.VerifyAi));

    public async Task<MailInputRule> GetRuleAsync(Guid id, CancellationToken ct) =>
        await Project(db.MailInputs.AsNoTracking().Where(r => r.Id == id)).SingleOrDefaultAsync(ct)
        ?? throw ApiException.NotFound("Mail input rule");

    public async Task<MailInputRule> CreateRuleAsync(MailInputRuleRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var rule = new MailInputRuleEntity { Id = Guid.NewGuid() };
        Apply(rule, request);
        db.MailInputs.Add(rule);
        await db.SaveChangesAsync(ct);
        return await GetRuleAsync(rule.Id, ct);
    }

    public async Task<MailInputRule> UpdateRuleAsync(Guid id, MailInputRuleRequest request, CancellationToken ct)
    {
        var rule = await db.MailInputs.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Mail input rule");
        await ValidateAsync(request, ct);
        Apply(rule, request);
        await db.SaveChangesAsync(ct);
        return await GetRuleAsync(id, ct);
    }

    /// <summary>The rule; the mail it processed stays in the log, without the rule's name.</summary>
    public async Task DeleteRuleAsync(Guid id, CancellationToken ct)
    {
        if (!await db.MailInputs.AnyAsync(r => r.Id == id, ct))
        {
            throw ApiException.NotFound("Mail input rule");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.MailLogs.Where(m => m.ProcessedById == id).ExecuteUpdateAsync(u => u.SetProperty(m => m.ProcessedById, (Guid?)null), ct);
        await db.MailInputs.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Newest first. Search: sender, recipient and subject.</summary>
    public async Task<Paged<MailLogItem>> ListLogAsync(MailLogQuery query, CancellationToken ct)
    {
        var log = db.MailLogs.AsNoTracking();
        if (query.Processed is { } processed)
        {
            log = log.Where(m => m.Processed == processed);
        }
        if (query.AiState is { } aiState)
        {
            log = log.Where(m => m.AiState == aiState);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            log = log.Where(m => EF.Functions.ILike(m.From, pattern, @"\") || EF.Functions.ILike(m.To, pattern, @"\")
                || EF.Functions.ILike(m.Subject, pattern, @"\"));
        }
        var page = await Paging.PageAsync(log
            .OrderByDescending(m => m.Created).ThenBy(m => m.Id)
            .Select(m => new
            {
                m.Id, m.Created, m.MessageDate, m.From, m.To, m.Subject, m.Processed, m.ProcessedById,
                RuleName = m.ProcessedBy != null ? m.ProcessedBy.Name : null, m.Disposition, m.AiState, m.DecisionId,
            }), query, ct);
        return new Paged<MailLogItem>(page.Items.Select(m => new MailLogItem(m.Id, Instants.Utc(m.Created), Instants.OrNull(m.MessageDate),
            m.From, m.To, m.Subject, m.Processed, m.ProcessedById is { } rule && m.RuleName is { } name ? new Ref(rule, name) : null,
            m.Disposition, m.AiState, m.DecisionId)).ToList(),
            page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<MailLogDetail> GetLogAsync(Guid id, CancellationToken ct)
    {
        var m = await db.MailLogs.AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new { Mail = m, RuleName = m.ProcessedBy != null ? m.ProcessedBy.Name : null })
            .SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Mail");
        var mail = m.Mail;
        return new MailLogDetail(mail.Id, Instants.Utc(mail.Created), Instants.OrNull(mail.MessageDate), mail.From, mail.To, mail.Subject,
            mail.Processed, mail.ProcessedById is { } rule && m.RuleName is { } name ? new Ref(rule, name) : null, mail.Content,
            mail.ProcessLog, mail.Disposition, mail.AiState, mail.DecisionId);
    }

    private async Task ValidateAsync(MailInputRuleRequest request, CancellationToken ct)
    {
        var v = new ApiValidator();
        v.Check(request.Name?.Trim() is { Length: >= 1 and <= 128 }, "name", "The name must have 1 to 128 characters.");
        // MailProcessTask matches every mail's sender against FromTest.
        v.Check(!string.IsNullOrEmpty(request.FromTest) && SensorService.IsRegex(request.FromTest), "fromTest",
            "A regular expression the sender must match.");
        foreach (var (field, value) in new[]
                 {
                     ("subjectTest", request.SubjectTest), ("bodyTest", request.BodyTest), ("okTest", request.OkTest),
                     ("failTest", request.FailTest), ("extractMessageRegex", request.ExtractMessageRegex?.Trim()),
                 })
        {
            v.Check(string.IsNullOrEmpty(value) || SensorService.IsRegex(value), field, "Not a valid regular expression.");
        }
        v.Check(request.DefaultCode is >= 0 and <= 999, "defaultCode", "A status code from 0 to 999.");
        v.Check(request.OkCode is null or >= 0 and <= 999, "okCode", "A status code from 0 to 999.");
        v.Check(request.FailCode is null or >= 0 and <= 999, "failCode", "A status code from 0 to 999.");
        v.Check(string.IsNullOrEmpty(request.OkTest) || request.OkCode is not null, "okCode", "OkTest needs an OkCode.");
        v.Check(string.IsNullOrEmpty(request.FailTest) || request.FailCode is not null, "failCode", "FailTest needs a FailCode.");
        if (request.SensorId is { } sensorId)
        {
            v.Check(await db.Sensors.AnyAsync(s => s.Id == sensorId, ct), "sensorId", "No such sensor.");
        }
        v.Check(Enum.IsDefined(request.Action), "action", "Event, AskAi, Ignore or Delete.");
        v.Check(request.Action != MailRuleAction.AskAi || request.SensorId is not null, "sensorId", "AI every time needs the sensor its events go to.");
        v.ThrowIfInvalid();
    }

    private static void Apply(MailInputRuleEntity rule, MailInputRuleRequest request)
    {
        rule.Name = request.Name!.Trim();
        rule.Enabled = request.Enabled;
        rule.FromTest = request.FromTest;
        rule.SubjectTest = Empty(request.SubjectTest);
        rule.BodyTest = Empty(request.BodyTest);
        rule.OkTest = Empty(request.OkTest);
        rule.OkCode = request.OkCode;
        rule.FailTest = Empty(request.FailTest);
        rule.FailCode = request.FailCode;
        rule.DefaultCode = request.DefaultCode;
        rule.ExtractMessageRegex = Empty(request.ExtractMessageRegex?.Trim());
        rule.SensorOutputId = request.Action is MailRuleAction.Ignore or MailRuleAction.Delete ? null : request.SensorId;
        rule.Action = request.Action;
        rule.AskAiWhenUnsure = request.Action == MailRuleAction.Event && request.AskAiWhenUnsure;
        rule.VerifyAi = request.VerifyAi;
    }

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
