using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PNMTD.Models.Db;

namespace PNMTD.Ai;

/// <summary>Who asks, for what: logged with every request.</summary>
public sealed record AiCall(string Purpose, Guid? WorkId = null, int Attempt = 1, string? PromptVersion = null);

/// <summary>A model's answer, parsed, with the request's log id.</summary>
public sealed record ChatResult(string Content, long RequestId, string? ModelServed, decimal? Cost);

/// <summary>
/// The OpenAI Chat Completions API (docs/ai-operations-plan.md §3.2), as OpenRouter serves it: strict structured
/// outputs (<c>response_format: json_schema</c>), temperature 0, and on OpenRouter <c>provider.require_parameters</c>
/// (only endpoints that support the schema) and <c>data_collection</c>. Every request is logged, whatever its outcome.
/// </summary>
public sealed class ChatClient(IHttpClientFactory http, IOptionsMonitor<AiOptions> options, AiRequestLog log, ILogger<ChatClient> logger)
{
    public const string HttpClientName = "ai";

    public async Task<ChatResult> CompleteAsync(AiCall call, AiRole role, string model, string system, string user, string schemaName,
        JsonObject schema, int maxTokens, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Usable)
        {
            throw new AiNotConfiguredException("AI is off or has no key: Ai__Enabled=true and Ai__ApiKey.");
        }
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["max_tokens"] = maxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = schemaName, ["strict"] = true, ["schema"] = schema.DeepClone() },
            },
        };
        if (o.IsOpenRouter)
        {
            body["provider"] = new JsonObject { ["require_parameters"] = true, ["data_collection"] = o.DataCollection };
        }
        var requestText = body.ToJsonString();
        var entry = new AiRequestEntity
        {
            Purpose = call.Purpose, WorkId = call.WorkId, Attempt = call.Attempt, Role = role, ModelAsked = model, PromptVersion = call.PromptVersion,
        };
        var watch = Stopwatch.StartNew();
        string? responseText = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(requestText, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
            request.Headers.TryAddWithoutValidation("X-Title", "PNMT");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds)));
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, timeout.Token);
            responseText = await response.Content.ReadAsStringAsync(timeout.Token);
            entry.HttpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var retryable = response.StatusCode != HttpStatusCode.BadRequest;
                throw new AiRequestException(AiOutcome.HttpError, $"HTTP {(int)response.StatusCode}: {ErrorOf(responseText)}", retryable, (int)response.StatusCode);
            }
            var content = Parse(responseText, entry);
            entry.Outcome = AiOutcome.Ok;
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            var id = await log.WriteAsync(entry, requestText, responseText, CancellationToken.None);
            return new ChatResult(content, id, entry.ModelServed, entry.Cost);
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
            logger.LogWarning("AI request ({Purpose}, {Model}) failed: {Error}", call.Purpose, model, failure.Message);
            throw failure;
        }
    }

    /// <summary>The message's content, and the generation's id, model, provider and usage into <paramref name="entry"/>.</summary>
    internal static string Parse(string responseText, AiRequestEntity entry)
    {
        using var json = JsonDocument.Parse(responseText);
        var root = json.RootElement;
        entry.GenerationId = Text(root, "id");
        entry.ModelServed = Text(root, "model");
        entry.Provider = Text(root, "provider");
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            entry.PromptTokens = Int(usage, "prompt_tokens");
            entry.CompletionTokens = Int(usage, "completion_tokens");
            entry.Cost = usage.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null;
            if (usage.TryGetProperty("prompt_tokens_details", out var prompt) && prompt.ValueKind == JsonValueKind.Object)
            {
                entry.CachedTokens = Int(prompt, "cached_tokens");
            }
            if (usage.TryGetProperty("completion_tokens_details", out var completion) && completion.ValueKind == JsonValueKind.Object)
            {
                entry.ReasoningTokens = Int(completion, "reasoning_tokens");
            }
        }
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String)
        {
            throw new AiRequestException(AiOutcome.InvalidOutput, "The answer has no message content.");
        }
        return content.GetString()!;
    }

    private static string ErrorOf(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                return error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : error.ToString();
            }
        }
        catch (JsonException)
        {
        }
        return body.Length > 300 ? body[..300] : body;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
