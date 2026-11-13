using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Decisions;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Mails;

/// <summary>A mail decision's payload (docs/ai-operations-plan.md §5.2): the proposal, the mail, what the layers said.</summary>
public sealed record MailDecisionPayload(MailRuleProposal? Proposal, MailEvidence Mail, AiEvidence Ai, Guid? RuleId = null);

/// <summary>The kinds of the mail decisions.</summary>
public static class MailDecisionKinds
{
    public const string Rule = "mail.rule";
    public const string Suspicious = "mail.suspicious";
    public const string Outcome = "mail.outcome";
}

/// <summary>What the mail handlers share: the mail as untrusted text, the evidence, and the dedupe keys.</summary>
public static partial class MailAi
{
    /// <summary>The subject with digits, dates, hex and GUIDs replaced: mails that differ only there are alike (§6.2).</summary>
    public static string SubjectShape(string subject) =>
        Spaces().Replace(Numbers().Replace(Hex().Replace(subject.ToLowerInvariant(), "#"), "#"), " ").Trim();

    public static string RuleKey(string from, string subject) => $"{MailDecisionKinds.Rule}:{RuleTemplate.SenderOf(from).ToLowerInvariant()}:{SubjectShape(subject)}";

    public static string SuspiciousKey(string from) => $"{MailDecisionKinds.Suspicious}:{RuleTemplate.SenderOf(from).ToLowerInvariant()}";

    public static MailEvidence Evidence(MailLogEntity mail, string body) =>
        new(mail.Id, mail.From, mail.To, mail.Subject, body, mail.MessageDate.Year <= 1 ? null : mail.MessageDate);

    public static AiEvidence Ai<T>(AiPipelineResult<T> result) =>
        new(result.Injection, result.BlockedBy, result.Scan.Flags, result.Verdict?.Answer.ToString().ToLowerInvariant(), result.Verdict?.Issues ?? [],
            result.RequestIds);

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b|\b[0-9a-f]{6,}\b")]
    private static partial Regex Hex();

    [GeneratedRegex(@"\d+([.:/-]\d+)*")]
    private static partial Regex Numbers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// <c>mail.analyse</c> (§6.2, §6.3): an unmatched mail (or with <c>mail.improve</c>, a mail its rule was unsure about) through the
/// pipeline; a proposal becomes a <c>mail.rule</c> decision with the expressions code built, and a block a
/// <c>mail.suspicious</c> one. <c>InputHash = force</c>: an Admin's "analyse anyway", without the scan and the guard.
/// </summary>
public class MailAnalyseHandler(IDbContextFactory<PnmtdDbContext> dbs, AiPipeline pipeline, DecisionBook decisions, IOptionsMonitor<AiOptions> options)
    : IAiWorkHandler
{
    public const string Force = "force";

    public virtual string Purpose => "mail.analyse";

    protected virtual bool Improve => false;

    public async Task<string?> RunAsync(AiWorkEntity work, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var mail = await db.MailLogs.SingleOrDefaultAsync(m => m.Id == work.SubjectId, ct) ?? throw new AiWorkStaleException("The mail is gone.");
        if (!Improve && mail.Processed)
        {
            throw new AiWorkStaleException("A rule handled the mail meanwhile.");
        }
        var unsure = Improve && mail.ProcessedById is { } ruleId ? await db.MailInputs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == ruleId, ct) : null;
        if (Improve && unsure is null)
        {
            throw new AiWorkStaleException("The rule is gone.");
        }
        var max = options.CurrentValue.MaxInputChars;
        var subject = InputSanitizer.Clean(mail.Subject, 500).Text;
        var cleaned = InputSanitizer.Clean(mail.Content, max);
        var scan = InjectionScanner.Check(cleaned.Text, cleaned.InvisibleRemoved);
        var sender = RuleTemplate.SenderOf(mail.From);
        var candidates = await CandidatesAsync(db, sender, subject, cleaned.Text, ct);
        var domain = RuleTemplate.DomainOf(sender);
        var rules = (await db.MailInputs.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct))
            .Where(r => r.FromTest is { } f && (f.Contains(Regex.Escape(domain), StringComparison.OrdinalIgnoreCase) || RuleTemplate.IsMatch(sender, f)))
            .Take(10).Select(Known).ToList();
        var input = new MailAnalyseInput(sender, mail.To, subject, cleaned.Text, mail.MessageDate.Year <= 1 ? null : mail.MessageDate.ToString("u"),
            scan.Flags, candidates.Select(c => c.Candidate).ToList(), rules, unsure is null ? null : Known(unsure));
        var force = work.InputHash == Force;
        var untrusted = force ? Array.Empty<string>() : new[] { $"From: {sender}\nSubject: {subject}\n\n{cleaned.Text}" };
        var result = await pipeline.RunAsync(new MailAnalyseTask(), input, untrusted, new AiCall(Purpose, work.Id, work.Attempts + 1), ct);
        Guid decision;
        if (result.Blocked)
        {
            decision = await decisions.RaiseAsync(new DecisionDraft(MailDecisionKinds.Suspicious,
                $"Suspicious mail from {sender}: “{Short(subject)}”",
                result.BlockedBy == "guard"
                    ? $"The guard rates it as a probable prompt injection ({result.Injection:0.00}). No model saw it."
                    : $"It looks like a prompt injection: {string.Join(", ", result.Scan.Flags)}. No model saw it.",
                new MailDecisionPayload(null, MailAi.Evidence(mail, cleaned.Text), MailAi.Ai(result)),
                MailAi.SuspiciousKey(mail.From), mail.Id, "mail", mail.Id, AiWorkId: work.Id), ct);
            await MarkAsync(mail.Id, MailAiState.Blocked, decision, ct);
            return JsonSerializer.Serialize(new { decision, blocked = result.BlockedBy });
        }
        var p = result.Output!;
        var chosen = p.SensorRef is { } sensorRef ? candidates.FirstOrDefault(c => c.Candidate.Ref == sensorRef) : default;
        var fromTest = RuleTemplate.From(p.From.Kind == MailFromKind.Domain, p.From.Value);
        var subjectTest = RuleTemplate.Subject(p.SubjectTemplate);
        var recent = await db.MailLogs.AsNoTracking().OrderByDescending(m => m.Created).Take(500).ToListAsync(ct);
        var preview = RuleTemplate.Preview(recent, fromTest, subjectTest, null, sender);
        var action = p.Action switch
        {
            MailProposedAction.EventRule => "event",
            MailProposedAction.AiRule => "ai",
            MailProposedAction.DeleteRule => "delete",
            MailProposedAction.IgnoreRule => "ignore",
            _ => "none",
        };
        var proposal = new MailRuleProposal(action, Name(p, sender, chosen.Name), fromTest, subjectTest,
            string.IsNullOrEmpty(p.OkPhrase) ? null : RuleTemplate.Phrase(p.OkPhrase),
            string.IsNullOrEmpty(p.FailPhrase) ? null : RuleTemplate.Phrase(p.FailPhrase),
            string.IsNullOrEmpty(p.MessageAfter) ? null : RuleTemplate.MessageAfter(p.MessageAfter),
            p.DefaultOutcome == MailOutcome.Fail ? "fail" : "ok",
            chosen.Id == Guid.Empty ? null : chosen.Id, chosen.Id == Guid.Empty ? null : chosen.Name, chosen.Id == Guid.Empty ? null : chosen.Host,
            p.NewSensorName, ToKebab(p.Kind.ToString()), p.Confidence, p.Summary, p.Reasoning,
            new RulePreview(preview.Checked, preview.Matches, preview.OtherSenders, preview.TooBroad, preview.Examples),
            unsure?.Id, unsure?.Name);
        var disputed = result.Verdict?.Answer == VerdictKind.Disagree;
        decision = await decisions.RaiseAsync(new DecisionDraft(MailDecisionKinds.Rule,
            Improve ? $"Better tests for the rule {unsure!.Name}" : $"Mail from {sender}: {Describe(action)}",
            p.Summary, new MailDecisionPayload(proposal, MailAi.Evidence(mail, cleaned.Text), MailAi.Ai(result), unsure?.Id),
            Improve ? $"mail.improve:{unsure!.Id}" : MailAi.RuleKey(mail.From, mail.Subject), mail.Id, "mail", mail.Id, Disputed: disputed,
            Priority: action is "event" or "ai" ? 1 : 0, AiWorkId: work.Id), ct);
        if (!Improve)
        {
            await MarkAsync(mail.Id, MailAiState.Analysed, decision, ct);
        }
        return JsonSerializer.Serialize(new { decision });
    }

    private async Task MarkAsync(Guid mailId, MailAiState state, Guid decision, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.MailLogs.Where(m => m.Id == mailId).ExecuteUpdateAsync(u => u.SetProperty(m => m.AiState, state).SetProperty(m => m.DecisionId, decision), ct);
    }

    private static KnownRule Known(MailInputRuleEntity r) => new(r.Name, r.FromTest ?? "", r.SubjectTest, r.OkTest, r.FailTest);

    /// <summary>
    /// At most 100 sensors a rule could feed: those whose host or name share words with the sender, the subject or the
    /// body, and those of rules with the same sender domain, best first.
    /// </summary>
    internal static async Task<List<(CandidateSensor Candidate, Guid Id, string Name, string Host)>> CandidatesAsync(PnmtdDbContext db, string sender,
        string subject, string body, CancellationToken ct)
    {
        var words = Words($"{sender} {subject} {body[..Math.Min(body.Length, 2000)]}");
        var domain = RuleTemplate.DomainOf(sender);
        var ruled = (await db.MailInputs.AsNoTracking().Where(r => r.SensorOutputId != null).Select(r => new { r.FromTest, r.SensorOutputId }).ToListAsync(ct))
            .Where(r => r.FromTest is { } f && f.Contains(Regex.Escape(domain), StringComparison.OrdinalIgnoreCase)).Select(r => r.SensorOutputId!.Value).ToHashSet();
        var sensors = await db.Sensors.AsNoTracking().Where(s => s.Type != Lib.Models.Enum.SensorType.SYSTEM)
            .Select(s => new { s.Id, s.Name, Host = s.Parent.Name, s.Type }).ToListAsync(ct);
        return sensors
            .Select(s => (s, Score: (ruled.Contains(s.Id) ? 100 : 0) + Words($"{s.Host} {s.Name}").Count(words.Contains)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.s.Host).ThenBy(x => x.s.Name)
            .Take(100)
            .Select((x, i) => (new CandidateSensor($"s{i + 1}", x.s.Host, x.s.Name, x.s.Type.ToString()), x.s.Id, x.s.Name, x.s.Host))
            .ToList();
    }

    private static HashSet<string> Words(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length >= 3).ToHashSet();

    private static string Name(MailAiProposal p, string sender, string? sensor) =>
        (sensor is null ? $"{p.Kind}: {sender}" : $"{sensor}: {sender}") is var name && name.Length > 128 ? name[..128] : name;

    private static string Describe(string action) => action switch
    {
        "event" => "a rule for its events",
        "ai" => "a rule that asks the AI every time",
        "delete" => "a rule that deletes such mails",
        "ignore" => "a rule that ignores such mails",
        _ => "no rule fits",
    };

    private static string Short(string text) => text.Length <= 80 ? text : text[..79] + "…";

    private static string ToKebab(string name) => Regex.Replace(name, "(?<!^)([A-Z])", "-$1").ToLowerInvariant();
}

/// <summary><c>mail.improve</c> (§6.2, trigger B): a mail its rule matched without its OK or FAIL test; better tests for the rule.</summary>
public sealed class MailImproveHandler(IDbContextFactory<PnmtdDbContext> dbs, AiPipeline pipeline, DecisionBook decisions, IOptionsMonitor<AiOptions> options)
    : MailAnalyseHandler(dbs, pipeline, decisions, options)
{
    public override string Purpose => "mail.improve";

    protected override bool Improve => true;
}

/// <summary>
/// <c>mail.evaluate</c> (§6.4): a mail of a rule set to AI every time. OK or failure becomes the rule's event (its OK or FAIL
/// code, or 200 and 500), with "AI:" before the message. Unknown, a block, or the verification model's disagreement
/// leave the mail to a person: a <c>mail.outcome</c> decision.
/// </summary>
public sealed class MailEvaluateHandler(IDbContextFactory<PnmtdDbContext> dbs, AiPipeline pipeline, DecisionBook decisions, IOptionsMonitor<AiOptions> options)
    : IAiWorkHandler
{
    public string Purpose => "mail.evaluate";

    public async Task<string?> RunAsync(AiWorkEntity work, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var mail = await db.MailLogs.SingleOrDefaultAsync(m => m.Id == work.SubjectId, ct) ?? throw new AiWorkStaleException("The mail is gone.");
        if (mail.Processed)
        {
            throw new AiWorkStaleException("The mail was handled meanwhile.");
        }
        var rule = mail.ProcessedById is { } ruleId
            ? await db.MailInputs.Include(r => r.SensorOutput).ThenInclude(s => s!.Parent).SingleOrDefaultAsync(r => r.Id == ruleId, ct)
            : null;
        if (rule is not { Enabled: true, Action: MailRuleAction.AskAi, SensorOutput: { } sensor })
        {
            throw new AiWorkStaleException("The rule is gone, disabled, or no longer asks the AI.");
        }
        var subject = InputSanitizer.Clean(mail.Subject, 500).Text;
        var body = InputSanitizer.Clean(mail.Content, options.CurrentValue.MaxInputChars).Text;
        var sender = RuleTemplate.SenderOf(mail.From);
        var input = new MailEvaluateInput(sender, subject, body, sensor.Parent.Name, sensor.Name, rule.Name);
        var result = await pipeline.RunAsync(new MailEvaluateTask(rule.VerifyAi), input, [$"From: {sender}\nSubject: {subject}\n\n{body}"],
            new AiCall(Purpose, work.Id, work.Attempts + 1), ct);
        var payload = new MailDecisionPayload(null, MailAi.Evidence(mail, body), MailAi.Ai(result), rule.Id);
        if (result.Blocked)
        {
            var id = await decisions.RaiseAsync(new DecisionDraft(MailDecisionKinds.Suspicious, $"Suspicious mail from {sender}",
                $"For the rule {rule.Name}, the guard or the scan stopped it. No model saw it.", payload, MailAi.SuspiciousKey(mail.From), mail.Id,
                "mail", mail.Id, AiWorkId: work.Id), ct);
            mail.AiState = MailAiState.Blocked;
            mail.DecisionId = id;
            await db.SaveChangesAsync(ct);
            return JsonSerializer.Serialize(new { decision = id, blocked = result.BlockedBy });
        }
        var e = result.Output!;
        var disagree = result.Verdict?.Answer == VerdictKind.Disagree;
        if (e.Outcome == MailVerdict.Unknown || disagree)
        {
            var id = await decisions.RaiseAsync(new DecisionDraft(MailDecisionKinds.Outcome, $"The AI could not decide: {Short(subject)}",
                disagree ? $"The main model said {e.Outcome.ToString().ToLowerInvariant()} ({e.Message}); the verification model disagreed."
                    : $"For the rule {rule.Name}: {e.Message}", payload, $"{MailDecisionKinds.Outcome}:{mail.Id}", mail.Id, "mail", mail.Id,
                Disputed: disagree, AiWorkId: work.Id), ct);
            mail.AiState = MailAiState.Analysed;
            mail.DecisionId = id;
            await db.SaveChangesAsync(ct);
            return JsonSerializer.Serialize(new { decision = id });
        }
        var ok = e.Outcome == MailVerdict.Ok;
        db.Events.Add(new EventEntity
        {
            Id = Guid.NewGuid(), SensorId = sensor.Id, Code = ok ? rule.OkCode ?? 200 : rule.FailCode ?? 500, Created = Clock.UtcNow,
            Message = $"AI: {e.Message}".Trim(), Source = mail.From,
        });
        mail.Processed = true;
        mail.Disposition = MailDisposition.AiEvent;
        mail.AiState = MailAiState.Analysed;
        mail.ProcessLog += $"\nAI: {(ok ? "ok" : "failure")}: {e.Message}";
        await db.SaveChangesAsync(ct);
        return JsonSerializer.Serialize(new { outcome = e.Outcome.ToString().ToLowerInvariant() });
    }

    private static string Short(string text) => text.Length <= 80 ? text : text[..79] + "…";
}
