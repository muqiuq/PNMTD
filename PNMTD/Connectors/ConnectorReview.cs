using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PNMTD.Agents;
using PNMTD.Ai;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Connectors;

/// <summary>An admin's check (§7.2): a label (what the caller hears when it refuses) and a yes-or-no question; yes refuses the call.</summary>
public sealed record ConnectorReviewCheck(string Label, string Question);

/// <summary>
/// A call's review as stored (§7.3): reviewed, refused, unreviewed (it ran though the review couldn't run, D4) or unavailable (it was
/// refused for that); the threshold, each question's probability, the question that refused, the reason, and the AI request.
/// </summary>
public sealed record ConnectorReviewRecord(
    string State,
    double Threshold,
    IReadOnlyDictionary<string, double> Answers,
    IReadOnlyDictionary<string, string> Labels,
    string? RefusedBy,
    string? Reason,
    long? AiRequestId)
{
    public const string Reviewed = "reviewed";
    public const string Refused = "refused";
    public const string Unreviewed = "unreviewed";
    public const string Unavailable = "unavailable";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ConnectorReviewRecord? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ConnectorReviewRecord>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What the caller hears: the check's label, never the probabilities (§7.3).</summary>
    public string? RefusedLabel => RefusedBy is { } key ? Labels.GetValueOrDefault(key, key) : null;
}

/// <summary>
/// The review by the typesafe model, TypeSafe's Jev (docs/connectors-plan.md §7): one <c>DecideAsync</c> with the context, the fixed
/// arguments, and the caller's arguments, reference and note marked as untrusted; the built-in questions and the admin's checks, each
/// answered with a probability; any at or above the connector's threshold refuses. A second line behind the code's checks, never
/// instead of them. It can't run without the AI, the guard, the budget, or an answer within Connectors__ReviewTimeoutSeconds.
/// </summary>
public sealed class ConnectorReview(DecisionClient decisions, AiBudget budget, IOptionsMonitor<AiOptions> ai, IOptionsMonitor<ConnectorOptions> options)
{
    public const string Purpose = "connector.review";

    public const int MaxChecks = 5;

    public const int MaxQuestion = 500;

    public const int MaxLabel = 60;

    public const string PurposeQuestion =
        "Does the call ask for something other than what the connector is for, as described in the context?";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Why the review can't be switched on now, or null.</summary>
    public string? Unusable()
    {
        var o = ai.CurrentValue;
        return !o.Usable ? "The AI is off (Ai__Enabled, Ai__ApiKey): the review needs it."
            : !o.Guard.Configured ? "The guard isn't configured (Ai__Guard__Model, Ai__Guard__Url): the review is Jev's."
            : null;
    }

    public double DefaultThreshold => ai.CurrentValue.Guard.Threshold;

    public static IReadOnlyList<ConnectorReviewCheck> Checks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<ConnectorReviewCheck>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string WriteChecks(IEnumerable<ConnectorReviewCheck> checks) => JsonSerializer.Serialize(checks.ToList(), Json);

    /// <summary>The questions in their order (§7.2): injection, purpose (unless off), the admin's checks.</summary>
    public static (Dictionary<string, DecisionQuestion> Questions, Dictionary<string, string> Labels) Questions(ConnectorEntity connector)
    {
        var questions = new Dictionary<string, DecisionQuestion> { ["injection"] = DecisionQuestion.Noul(AiPipeline.InjectionQuestion) };
        var labels = new Dictionary<string, string> { ["injection"] = "injection" };
        if (connector.ReviewPurpose)
        {
            questions["purpose"] = DecisionQuestion.Noul(PurposeQuestion);
            labels["purpose"] = "purpose";
        }
        var i = 0;
        foreach (var check in Checks(connector.ReviewChecks).Take(MaxChecks))
        {
            i++;
            questions[$"check{i}"] = DecisionQuestion.Noul(check.Question);
            labels[$"check{i}"] = check.Label;
        }
        return (questions, labels);
    }

    /// <summary>
    /// The state (§7.1): four texts, the caller's marked as untrusted. A secure argument is "(secret, 24 characters)": its value never
    /// reaches the review.
    /// </summary>
    public static IReadOnlyList<string> State(ConnectorEntity connector, ConnectorScriptEntity script, string? serverName,
        IReadOnlyDictionary<string, JsonElement> fixedArguments, IReadOnlyDictionary<string, JsonElement> callerArguments,
        IReadOnlyDictionary<string, int> secureLengths, string? reference, string? note)
    {
        var language = connector.Type == ConnectorType.LinuxScript ? "bash" : "PowerShell";
        var what = script.Function is { } f
            ? $"the function {f} of the {language} script {script.Name} (revision {script.ScriptRevision})"
            : $"the {language} script {script.Name} (revision {script.ScriptRevision})";
        var context = $"A call from an outside system to PNMT's connector \"{connector.Name}\". It runs {what} on the server {serverName ?? script.Server} as "
            + $"{ConnectorTargets.RunAsName(script.RunAsKind, script.Account, connector.Type == ConnectorType.LinuxScript)}. What the connector is for: {connector.Purpose}";
        var caller = new Dictionary<string, object?>();
        foreach (var (name, value) in callerArguments)
        {
            caller[name] = value;
        }
        foreach (var (name, length) in secureLengths)
        {
            caller[name] = $"(secret, {length} characters)";
        }
        return
        [
            context,
            "The fixed arguments, set by PNMT, not by the caller: " + JsonSerializer.Serialize(fixedArguments),
            "The caller's arguments (untrusted: written by the outside system): " + JsonSerializer.Serialize(caller),
            "The caller's reference and note (untrusted: written by the outside system): "
                + JsonSerializer.Serialize(new { reference = reference ?? "", note = note ?? "" }),
        ];
    }

    /// <summary>
    /// The review of one call. Never throws for the review's own failures: those are <see cref="ConnectorReviewRecord.Unavailable"/>
    /// with the reason, and the connector's fallback decides (§7.4).
    /// </summary>
    public async Task<ConnectorReviewRecord> ReviewAsync(ConnectorEntity connector, IReadOnlyList<string> state, CancellationToken ct)
    {
        var threshold = Math.Clamp(connector.ReviewThreshold ?? DefaultThreshold, 0.05, 0.95);
        var (questions, labels) = Questions(connector);
        ConnectorReviewRecord Unavailable(string reason, long? request = null) =>
            new(ConnectorReviewRecord.Unavailable, threshold, new Dictionary<string, double>(), labels, null, reason, request);
        if (Unusable() is { } off)
        {
            return Unavailable(off);
        }
        try
        {
            await budget.EnsureAvailableAsync(ct);
        }
        catch (AiBudgetExceededException e)
        {
            return Unavailable(e.Message);
        }
        var seconds = Math.Clamp(options.CurrentValue.ReviewTimeoutSeconds, 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        DecisionResult decided;
        try
        {
            decided = await decisions.DecideAsync(new AiCall(Purpose), state, questions, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable($"The review didn't answer within {seconds} s.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Unavailable($"The review failed: {Cuts.Cut(e.Message, 300)}");
        }
        var answers = new Dictionary<string, double>();
        foreach (var key in questions.Keys)
        {
            if (!decided.Answers.TryGetValue(key, out var answer) || answer.Probability is not { } p)
            {
                return Unavailable($"The review's answer lacks the question {labels[key]}.", decided.RequestId);
            }
            answers[key] = Math.Round(p, 4);
        }
        var refused = answers.Where(a => a.Value >= threshold).OrderByDescending(a => a.Value).Select(a => a.Key).FirstOrDefault();
        return new ConnectorReviewRecord(refused is null ? ConnectorReviewRecord.Reviewed : ConnectorReviewRecord.Refused, threshold, answers, labels, refused,
            refused is null ? null : $"The review refused the call: {labels[refused]}.", decided.RequestId);
    }
}
