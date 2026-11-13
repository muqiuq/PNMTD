using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PNMTD.Models.Db;

namespace PNMTD.Ai;

/// <summary>A typed question for Jev: <c>noul</c> (a statement, answered with a probability) or <c>choice</c> (options).</summary>
public sealed record DecisionQuestion(string Type, string Instructions, IReadOnlyDictionary<string, string>? Criteria = null)
{
    public static DecisionQuestion Noul(string instructions) => new("noul", instructions);

    public static DecisionQuestion Choice(string instructions, IReadOnlyDictionary<string, string> criteria) => new("choice", instructions, criteria);
}

/// <summary>An answer: a noul's probability, or a choice with its probabilities and confidence.</summary>
public sealed record DecisionAnswer(double? Probability, string? Choice, IReadOnlyDictionary<string, double> Probabilities, double? Confidence);

public sealed record DecisionResult(IReadOnlyDictionary<string, DecisionAnswer> Answers, long RequestId);

/// <summary>
/// TypeSafe's Jev through OpenRouter (docs/ai-operations-plan.md §3.2): a <c>state</c> (texts) and typed
/// <c>questions</c>, answered with probabilities only, so it cannot be talked into writing text. The URL is
/// <c>Ai:Guard:Url</c> (the Decisions API, or <c>/api/v1/systemone</c>), with the same key as the chat models. The
/// state is capped at <c>Ai:Guard:MaxStateBytes</c>: above about 32 KB Jev is slower and less accurate.
/// </summary>
public sealed class DecisionClient(IHttpClientFactory http, IOptionsMonitor<AiOptions> options, AiRequestLog log, ILogger<DecisionClient> logger)
{
    public async Task<DecisionResult> DecideAsync(AiCall call, IReadOnlyList<string> state, IReadOnlyDictionary<string, DecisionQuestion> questions,
        CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Usable || !o.Guard.Configured)
        {
            throw new AiNotConfiguredException("The guard (Ai__Guard__Model, Ai__Guard__Url) is not configured.");
        }
        var questionsJson = new JsonObject();
        foreach (var (name, q) in questions)
        {
            var question = new JsonObject { ["type"] = q.Type, ["instructions"] = q.Instructions };
            if (q.Criteria is { } criteria)
            {
                question["criteria"] = new JsonObject(criteria.Select(c => KeyValuePair.Create(c.Key, (JsonNode?)c.Value)));
            }
            questionsJson[name] = question;
        }
        var body = new JsonObject
        {
            ["model"] = o.Guard.Model,
            ["state"] = new JsonArray(Cap(state, o.Guard.MaxStateBytes).Select(s => (JsonNode)s).ToArray()),
            ["questions"] = questionsJson,
        };
        var requestText = body.ToJsonString();
        var entry = new AiRequestEntity
        {
            Purpose = call.Purpose, WorkId = call.WorkId, Attempt = call.Attempt, Role = AiRole.Guard, ModelAsked = o.Guard.Model,
            PromptVersion = call.PromptVersion,
        };
        var watch = Stopwatch.StartNew();
        string? responseText = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, o.Guard.Url)
            {
                Content = new StringContent(requestText, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds)));
            using var response = await http.CreateClient(ChatClient.HttpClientName).SendAsync(request, timeout.Token);
            responseText = await response.Content.ReadAsStringAsync(timeout.Token);
            entry.HttpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                throw new AiRequestException(AiOutcome.HttpError, $"HTTP {(int)response.StatusCode}: {(responseText.Length > 300 ? responseText[..300] : responseText)}",
                    response.StatusCode != HttpStatusCode.BadRequest, (int)response.StatusCode);
            }
            var answers = Parse(responseText, questions.Keys, entry);
            entry.Outcome = AiOutcome.Ok;
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            var id = await log.WriteAsync(entry, requestText, responseText, CancellationToken.None);
            return new DecisionResult(answers, id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = ex switch
            {
                AiRequestException a => a,
                OperationCanceledException => new AiRequestException(AiOutcome.Timeout, $"No answer within {o.TimeoutSeconds} s."),
                HttpRequestException h => new AiRequestException(AiOutcome.ConnectError, h.Message),
                JsonException j => new AiRequestException(AiOutcome.InvalidOutput, $"Not the expected JSON: {j.Message}"),
                _ => new AiRequestException(AiOutcome.ConnectError, ex.Message),
            };
            entry.Outcome = failure.Outcome;
            entry.Error = failure.Message;
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            await log.WriteAsync(entry, requestText, responseText, CancellationToken.None);
            logger.LogWarning("Guard request ({Purpose}) failed: {Error}", call.Purpose, failure.Message);
            throw failure;
        }
    }

    /// <summary>The texts, each shortened (head and tail) so that together they stay under the cap.</summary>
    internal static IReadOnlyList<string> Cap(IReadOnlyList<string> state, int maxBytes)
    {
        var total = state.Sum(s => Encoding.UTF8.GetByteCount(s));
        if (total <= maxBytes || state.Count == 0)
        {
            return state;
        }
        var each = Math.Max(256, maxBytes / state.Count);
        return state.Select(s => InputSanitizer.HeadAndTail(s, each / 2)).ToList();
    }

    /// <summary>
    /// The answers by question name: under <c>answers</c> (TypeSafe's own format) or at the top level (the Decisions
    /// API). A noul's probability is <c>noul</c> or <c>probability</c>; a choice has <c>choice</c>, <c>probabilities</c>
    /// and <c>confidence</c>. The cost is <c>usage.cost</c>.
    /// </summary>
    internal static IReadOnlyDictionary<string, DecisionAnswer> Parse(string text, IEnumerable<string> names, AiRequestEntity entry)
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number)
        {
            entry.Cost = cost.GetDecimal();
        }
        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
        {
            entry.ModelServed = model.GetString();
        }
        var holder = root.TryGetProperty("answers", out var answers) && answers.ValueKind == JsonValueKind.Object ? answers : root;
        var result = new Dictionary<string, DecisionAnswer>();
        foreach (var name in names)
        {
            if (!holder.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Object)
            {
                throw new AiRequestException(AiOutcome.InvalidOutput, $"No answer to {name}.");
            }
            double? Number(params string[] keys) =>
                keys.Select(k => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null).FirstOrDefault(v => v is not null);
            var probabilities = a.TryGetProperty("probabilities", out var p) && p.ValueKind == JsonValueKind.Object
                ? p.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Number).ToDictionary(x => x.Name, x => x.Value.GetDouble())
                : new Dictionary<string, double>();
            var choice = a.TryGetProperty("choice", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            result[name] = new DecisionAnswer(Number("noul", "probability"), choice, probabilities, Number("confidence"));
        }
        return result;
    }
}
