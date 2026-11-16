using System.ComponentModel;
using PNMTD.Ai;

namespace PNMTD.Mails;

public enum MailKind
{
    StatusReport,
    Alert,
    NotificationNoise,
    Newsletter,
    Spam,
    Phishing,
    Personal,
    Other,
}

public enum MailProposedAction
{
    EventRule,
    DeleteRule,
    IgnoreRule,
    AiRule,
    None,
}

public enum MailFromKind
{
    Address,
    Domain,
}

public enum MailOutcome
{
    Ok,
    Fail,
}

public enum MailVerdict
{
    Ok,
    Fail,
    Unknown,
}

/// <summary>A sensor a rule could feed, by a short reference the model returns (s1, s2, …).</summary>
public sealed record CandidateSensor(string Ref, string Host, string Sensor, string Type);

/// <summary>A rule PNMT already has: for consistency, or the unsure rule whose tests are to improve.</summary>
public sealed record KnownRule(string Name, string FromTest, string? SubjectTest, string? OkTest, string? FailTest);

/// <summary>What the main model reads about an unmatched mail (docs/ai-operations-plan.md §6.3).</summary>
public sealed record MailAnalyseInput(
    string UntrustedSender,
    string UntrustedRecipient,
    string UntrustedSubject,
    string UntrustedBody,
    string? Date,
    IReadOnlyList<string> ScanFlags,
    IReadOnlyList<CandidateSensor> Candidates,
    IReadOnlyList<KnownRule> RulesOfTheDomain,
    KnownRule? UnsureRule);

public sealed record MailSender(
    [property: Description("address: the sender's exact address; domain: its domain, for every sender of it")] MailFromKind Kind,
    [property: Description("The address or the domain, exactly as in untrustedSender")] string Value);

/// <summary>The proposal (§6.3): a template and verbatim phrases; code builds the expressions from them.</summary>
public sealed record MailAiProposal(
    MailKind Kind,
    MailProposedAction Action,
    [property: Description("The ref of one of the candidates (s1, s2, …), or null")] string? SensorRef,
    [property: Description("A name for a new sensor when no candidate fits, or null")] string? NewSensorName,
    MailSender From,
    [property: Description("The subject with {*} for every part that varies between such mails (numbers, dates, names); the rest exactly as in the subject")] string SubjectTemplate,
    [property: Description("A phrase copied exactly from the subject or body that means success, or null")] string? OkPhrase,
    [property: Description("A phrase copied exactly from the subject or body that means failure, or null")] string? FailPhrase,
    [property: Description("A phrase copied exactly from the body after which the useful message follows on the same line, or null")] string? MessageAfter,
    MailOutcome DefaultOutcome,
    [property: Description("0 to 1")] double Confidence,
    [property: Description("At most 300 characters")] string Summary,
    [property: Description("At most 600 characters")] string Reasoning);

/// <summary>
/// An unmatched mail, or a mail its rule was unsure about: what it is, and the rule that would handle it (§6.2, §6.3).
/// </summary>
public sealed class MailAnalyseTask(int maxTokens) : AiTask<MailAnalyseInput, MailAiProposal>(maxTokens)
{
    public override string Purpose => "mail.analyse";

    public override string PromptVersion => "mail-analyse-1";

    public override bool Verify => true;

    public override string VerifyQuestion =>
        "Is this a sensible rule for mails like this one: the right kind and action, the right sensor among the candidates, a subject template that " +
        "matches such mails without matching unrelated ones, and phrases that really mean success or failure?";

    protected override string Instructions =>
        "PNMT receives mails from systems it monitors (backup jobs, appliances, cloud services) and turns them into events with rules. " +
        "This mail matched no rule (or, with unsureRule, matched a rule whose OK and FAIL tests both missed). Decide what kind of mail it is " +
        "and propose the rule that should handle it:\n" +
        "- eventRule: the mail reports the state of something monitored; choose the sensor from candidates (sensorRef), or name a new one;\n" +
        "- aiRule: like eventRule, but the state can only be read by understanding the text, not by fixed phrases;\n" +
        "- deleteRule: unwanted mail (spam, phishing, newsletters) that should be deleted;\n" +
        "- ignoreRule: harmless mail that needs no event;\n" +
        "- none: no rule fits.\n" +
        "from: the sender's address, or its domain when every mail of the domain is alike. subjectTemplate: the subject exactly, with {*} for " +
        "every part that varies between such mails. okPhrase, failPhrase and messageAfter must be copied exactly from the mail. With " +
        "unsureRule, propose okPhrase and failPhrase that this mail would have matched. Keep summary and reasoning short and factual.";

    public override string? Validate(MailAiProposal output, MailAnalyseInput input)
    {
        if (output.Confidence is < 0 or > 1)
        {
            return "confidence must be between 0 and 1.";
        }
        if (output.SensorRef is { } r && input.Candidates.All(c => c.Ref != r))
        {
            return $"sensorRef {r} is not one of the candidates.";
        }
        var sender = RuleTemplate.SenderOf(input.UntrustedSender);
        var fromOk = output.From.Kind == MailFromKind.Address
            ? string.Equals(output.From.Value.Trim(), sender, StringComparison.OrdinalIgnoreCase)
            : string.Equals(output.From.Value.Trim().TrimStart('@'), RuleTemplate.DomainOf(sender), StringComparison.OrdinalIgnoreCase);
        if (!fromOk)
        {
            return "from must be the sender's address or its domain.";
        }
        if (!RuleTemplate.IsMatch(input.UntrustedSubject, RuleTemplate.Subject(output.SubjectTemplate)))
        {
            return "subjectTemplate does not reproduce the subject.";
        }
        foreach (var (name, phrase) in new[] { ("okPhrase", output.OkPhrase), ("failPhrase", output.FailPhrase), ("messageAfter", output.MessageAfter) })
        {
            if (!string.IsNullOrEmpty(phrase) && !input.UntrustedBody.Contains(phrase, StringComparison.Ordinal)
                && !input.UntrustedSubject.Contains(phrase, StringComparison.Ordinal))
            {
                return $"{name} does not occur verbatim in the mail.";
            }
        }
        return output.Summary.Length > 600 || output.Reasoning.Length > 1200 ? "summary or reasoning too long." : null;
    }
}

/// <summary>What the main model reads for a rule set to AI every time (§6.4).</summary>
public sealed record MailEvaluateInput(
    string UntrustedSender,
    string UntrustedSubject,
    string UntrustedBody,
    string Host,
    string Sensor,
    string RuleName);

public sealed record MailEvaluation(
    [property: Description("ok when the mail reports success, fail when it reports a failure or a problem, unknown when it cannot tell")] MailVerdict Outcome,
    [property: Description("At most 200 characters, preferably copied from the mail")] string Message);

/// <summary>A mail of a rule set to AI every time: did the monitored thing succeed? (§6.4).</summary>
public sealed class MailEvaluateTask(bool verify, int maxTokens) : AiTask<MailEvaluateInput, MailEvaluation>(maxTokens)
{
    public override string Purpose => "mail.evaluate";

    public override string PromptVersion => "mail-evaluate-1";

    public override bool Verify => verify;

    public override string VerifyQuestion => "Does the mail really report this outcome for the monitored thing?";

    protected override string Instructions =>
        "The mail reports on something PNMT monitors (the sensor of the host). Say whether it reports success (ok), a failure or problem " +
        "(fail), or cannot tell (unknown), and give a short message, preferably copied from the mail.";

    public override string? Validate(MailEvaluation output, MailEvaluateInput input) =>
        output.Message.Length > 400 ? "message too long." : null;
}
