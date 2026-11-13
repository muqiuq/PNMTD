using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Ai;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Mails;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Decisions;

/// <summary>What applying a decision may use: the request's database context and services, and who decides.</summary>
public sealed record DecisionContext(PnmtdDbContext Db, MailService Mail, AiWorkQueue Queue, bool IsAdmin);

/// <summary>A kind of decision (docs/ai-operations-plan.md §5.1): its options, and what each does.</summary>
public interface IDecisionKind
{
    string Kind { get; }

    IReadOnlyList<DecisionOption> Options(DecisionEntity decision);

    /// <summary>Applies the choice; returns the result as JSON. An <see cref="ApiException"/> leaves the decision open.</summary>
    Task<string?> ApplyAsync(DecisionEntity decision, string choice, DecideRequest request, DecisionContext context, CancellationToken ct);

    /// <summary>False when its subject was handled elsewhere: the decision is superseded.</summary>
    Task<bool> StillRelevantAsync(DecisionEntity decision, PnmtdDbContext db, CancellationToken ct);
}

/// <summary>Helpers the mail kinds share.</summary>
internal static class MailDecisions
{
    public static async Task<bool> AnyOpenMailAsync(DecisionEntity decision, PnmtdDbContext db, CancellationToken ct) =>
        decision.Subjects.Count == 0 || await db.MailLogs.AnyAsync(m => decision.Subjects.Contains(m.Id) && !m.Processed, ct);

    /// <summary>The subjects' mails: processed with this disposition (a delete removes them from the mailbox, §6.4).</summary>
    public static async Task<int> DisposeAsync(DecisionEntity decision, PnmtdDbContext db, MailDisposition disposition, string log, CancellationToken ct)
    {
        var mails = await db.MailLogs.Where(m => decision.Subjects.Contains(m.Id) && !m.Processed).ToListAsync(ct);
        foreach (var mail in mails)
        {
            mail.Processed = true;
            mail.Disposition = disposition;
            mail.ProcessLog += "\n" + log;
        }
        await db.SaveChangesAsync(ct);
        return mails.Count;
    }

    public static string Result(object value) => JsonSerializer.Serialize(value, DecisionBook.Json);
}

/// <summary>
/// <c>mail.rule</c> (§5.2, §6): a rule the AI proposed. Creating it runs through <see cref="MailService"/>'s validation; the
/// mails stay unprocessed, so the new rule takes them at its next run. A too-broad rule can only be created after editing.
/// </summary>
public sealed class MailRuleDecision : IDecisionKind
{
    public string Kind => MailDecisionKinds.Rule;

    public IReadOnlyList<DecisionOption> Options(DecisionEntity decision)
    {
        var p = DecisionBook.Payload<MailDecisionPayload>(decision).Proposal!;
        var needsSensor = p.Action is "event" or "ai";
        var createBlocked = p.Action == "none" ? "The AI proposes no rule."
            : p.Preview.TooBroad ? "Too broad: it would match many other mails. Edit it first."
            : needsSensor && p.SensorId is null ? "No sensor chosen: edit it first."
            : null;
        var options = new List<DecisionOption>();
        if (p.ExistingRuleId is not null)
        {
            options.Add(new DecisionOption("update-rule", $"Update the rule {p.ExistingRuleName}",
                "Give the existing rule the proposed OK and FAIL tests.", false, false, p.OkTest is not null || p.FailTest is not null));
        }
        else
        {
            options.Add(new DecisionOption("create", "Create the rule", createBlocked ?? $"A rule that {Describe(p.Action)}, as proposed.", false, false,
                createBlocked is null));
            options.Add(new DecisionOption("edit", "Edit, then create", "Change the proposal in the form, then create it.", true, false, true));
            options.Add(new DecisionOption("delete-rule", "Delete mails like this", "A rule that deletes them from the mailbox.", false, false,
                !p.Preview.TooBroad));
            options.Add(new DecisionOption("ai-rule", "AI every time", "A rule that hands every such mail to the AI, which says OK or failure.",
                false, false, p.SensorId is not null && !p.Preview.TooBroad));
            options.Add(new DecisionOption("ignore-rule", "Ignore mails like this", "A rule that marks them processed, without an event.", false, false,
                !p.Preview.TooBroad));
        }
        options.Add(new DecisionOption("reject", "Reject", "No rule. The mails stay as they are.", false, false, true));
        return options;
    }

    public async Task<string?> ApplyAsync(DecisionEntity decision, string choice, DecideRequest request, DecisionContext context, CancellationToken ct)
    {
        var p = DecisionBook.Payload<MailDecisionPayload>(decision).Proposal!;
        if (choice == "reject")
        {
            return null;
        }
        if (choice == "update-rule")
        {
            var rule = await context.Mail.GetRuleAsync(p.ExistingRuleId!.Value, ct);
            var updated = await context.Mail.UpdateRuleAsync(rule.Id, new MailInputRuleRequest
            {
                Name = rule.Name, Enabled = rule.Enabled, FromTest = rule.FromTest, SubjectTest = rule.SubjectTest, BodyTest = rule.BodyTest,
                OkTest = p.OkTest ?? rule.OkTest, OkCode = rule.OkCode ?? (p.OkTest is null ? null : 200),
                FailTest = p.FailTest ?? rule.FailTest, FailCode = rule.FailCode ?? (p.FailTest is null ? null : 500),
                DefaultCode = rule.DefaultCode, ExtractMessageRegex = rule.ExtractMessageRegex ?? p.ExtractMessageRegex, SensorId = rule.Sensor?.Id,
                Action = rule.Action, AskAiWhenUnsure = rule.AskAiWhenUnsure, VerifyAi = rule.VerifyAi,
            }, ct);
            return MailDecisions.Result(new { ruleId = updated.Id });
        }
        var created = choice switch
        {
            "edit" => await context.Mail.CreateRuleAsync(request.Rule ?? throw ApiException.Invalid("rule", "The edited rule."), ct),
            "create" => await context.Mail.CreateRuleAsync(Rule(p, p.Action), ct),
            "delete-rule" => await context.Mail.CreateRuleAsync(Rule(p, "delete"), ct),
            "ai-rule" => await context.Mail.CreateRuleAsync(Rule(p, "ai"), ct),
            "ignore-rule" => await context.Mail.CreateRuleAsync(Rule(p, "ignore"), ct),
            _ => throw ApiException.Invalid("choice", "Not an option of this decision."),
        };
        return MailDecisions.Result(new { ruleId = created.Id });
    }

    /// <summary>The proposal as a rule request with <paramref name="action"/>.</summary>
    public static MailInputRuleRequest Rule(MailRuleProposal p, string action)
    {
        var kind = action switch
        {
            "ai" => MailRuleAction.AskAi,
            "delete" => MailRuleAction.Delete,
            "ignore" => MailRuleAction.Ignore,
            _ => MailRuleAction.Event,
        };
        var isEvent = kind is MailRuleAction.Event or MailRuleAction.AskAi;
        return new MailInputRuleRequest
        {
            Name = p.Name, Enabled = true, FromTest = p.FromTest, SubjectTest = p.SubjectTest,
            OkTest = isEvent ? p.OkTest : null, OkCode = isEvent && p.OkTest is not null ? 200 : null,
            FailTest = isEvent ? p.FailTest : null, FailCode = isEvent && p.FailTest is not null ? 500 : null,
            DefaultCode = p.DefaultOutcome == "fail" ? 500 : 200, ExtractMessageRegex = isEvent ? p.ExtractMessageRegex : null,
            SensorId = isEvent ? p.SensorId : null, Action = kind,
        };
    }

    public Task<bool> StillRelevantAsync(DecisionEntity decision, PnmtdDbContext db, CancellationToken ct) =>
        DecisionBook.Payload<MailDecisionPayload>(decision).Proposal?.ExistingRuleId is not null
            ? Task.FromResult(true)
            : MailDecisions.AnyOpenMailAsync(decision, db, ct);

    private static string Describe(string action) => action switch
    {
        "event" => "writes an event for each such mail",
        "ai" => "asks the AI about each such mail",
        "delete" => "deletes such mails",
        "ignore" => "ignores such mails",
        _ => "does nothing",
    };
}

/// <summary><c>mail.suspicious</c> (§5.2): a mail the scan or the guard stopped. No model saw it.</summary>
public sealed class MailSuspiciousDecision : IDecisionKind
{
    public string Kind => MailDecisionKinds.Suspicious;

    public IReadOnlyList<DecisionOption> Options(DecisionEntity decision) =>
    [
        new("delete-mail", "Delete this mail", "Processed, and deleted from the mailbox.", false, false, true),
        new("keep", "Keep it", "Processed, without an event.", false, false, true),
        new("delete-sender", "Delete everything from this sender", "A rule that deletes every mail of the sender.", false, false, true),
        new("analyse-anyway", "Analyse anyway", "The main model reads it, with the warnings shown; Admins only.", false, true, true),
    ];

    public async Task<string?> ApplyAsync(DecisionEntity decision, string choice, DecideRequest request, DecisionContext context, CancellationToken ct)
    {
        var payload = DecisionBook.Payload<MailDecisionPayload>(decision);
        switch (choice)
        {
            case "delete-mail":
                return MailDecisions.Result(new { mails = await MailDecisions.DisposeAsync(decision, context.Db, MailDisposition.Deleted, "Deleted by a decision.", ct) });
            case "keep":
                return MailDecisions.Result(new { mails = await MailDecisions.DisposeAsync(decision, context.Db, MailDisposition.Ignored, "Kept by a decision.", ct) });
            case "delete-sender":
                var sender = RuleTemplate.SenderOf(payload.Mail.From);
                var rule = await context.Mail.CreateRuleAsync(new MailInputRuleRequest
                {
                    Name = $"Delete: {sender}", Enabled = true, FromTest = RuleTemplate.From(false, sender), Action = MailRuleAction.Delete,
                }, ct);
                return MailDecisions.Result(new { ruleId = rule.Id });
            case "analyse-anyway":
                if (!context.IsAdmin)
                {
                    throw ApiException.Invalid("choice", "Admins only.");
                }
                var mails = await context.Db.MailLogs.Where(m => decision.Subjects.Contains(m.Id) && !m.Processed).Select(m => m.Id).ToListAsync(ct);
                foreach (var mail in mails)
                {
                    await context.Queue.EnqueueAsync("mail.analyse", "mail", mail, MailAnalyseHandler.Force, ct);
                }
                return MailDecisions.Result(new { queued = mails.Count });
            default:
                throw ApiException.Invalid("choice", "Not an option of this decision.");
        }
    }

    public Task<bool> StillRelevantAsync(DecisionEntity decision, PnmtdDbContext db, CancellationToken ct) => MailDecisions.AnyOpenMailAsync(decision, db, ct);
}

/// <summary><c>mail.outcome</c> (§6.4): a mail of an AI-every-time rule the AI could not decide; a person does.</summary>
public sealed class MailOutcomeDecision : IDecisionKind
{
    public string Kind => MailDecisionKinds.Outcome;

    public IReadOnlyList<DecisionOption> Options(DecisionEntity decision) =>
    [
        new("ok", "It reports success", "The rule's OK event.", false, false, true),
        new("fail", "It reports a failure", "The rule's FAIL event.", false, false, true),
        new("ignore", "Ignore it", "Processed, without an event.", false, false, true),
    ];

    public async Task<string?> ApplyAsync(DecisionEntity decision, string choice, DecideRequest request, DecisionContext context, CancellationToken ct)
    {
        var payload = DecisionBook.Payload<MailDecisionPayload>(decision);
        if (choice == "ignore")
        {
            return MailDecisions.Result(new { mails = await MailDecisions.DisposeAsync(decision, context.Db, MailDisposition.Ignored, "Ignored by a decision.", ct) });
        }
        if (choice is not ("ok" or "fail"))
        {
            throw ApiException.Invalid("choice", "Not an option of this decision.");
        }
        var rule = await context.Db.MailInputs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == payload.RuleId, ct)
            ?? throw ApiException.Conflict("rule_gone", "The rule is gone.");
        if (rule.SensorOutputId is not { } sensor)
        {
            throw ApiException.Conflict("rule_without_sensor", "The rule has no sensor any more.");
        }
        var ok = choice == "ok";
        var mails = await context.Db.MailLogs.Where(m => decision.Subjects.Contains(m.Id) && !m.Processed).ToListAsync(ct);
        foreach (var mail in mails)
        {
            context.Db.Events.Add(new EventEntity
            {
                Id = Guid.NewGuid(), SensorId = sensor, Code = ok ? rule.OkCode ?? 200 : rule.FailCode ?? 500, Created = Clock.UtcNow,
                Message = $"Decided: {(ok ? "OK" : "failure")}", Source = mail.From,
            });
            mail.Processed = true;
            mail.Disposition = MailDisposition.Event;
            mail.ProcessLog += $"\nDecided by a person: {choice}.";
        }
        await context.Db.SaveChangesAsync(ct);
        return MailDecisions.Result(new { mails = mails.Count });
    }

    public Task<bool> StillRelevantAsync(DecisionEntity decision, PnmtdDbContext db, CancellationToken ct) => MailDecisions.AnyOpenMailAsync(decision, db, ct);
}
