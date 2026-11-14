using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>A tool call the model asked for.</summary>
public sealed record ModelToolCall(string Id, string Name, string Arguments);

/// <summary>One answer of the model: its text, its tool calls, its reasoning, and the message to send back with the next turn.</summary>
public sealed record ModelTurn(
    string? Content,
    IReadOnlyList<ModelToolCall> ToolCalls,
    string? Reasoning,
    JsonObject AssistantMessage,
    long RequestId,
    decimal Cost,
    string? Provider);

/// <summary>Cookie's budget is spent for the month (§19.1): Cookie pauses until the next.</summary>
public sealed class CookieBudgetException(decimal spent, decimal limit) : Exception($"Cookie's budget is spent: {spent:0.00} of {limit:0.00} USD this month.");

/// <summary>
/// Cookie's model (docs/cookie-plan.md §14.3): OpenRouter's Chat Completions with tools (<c>tool_choice: auto</c> only), reasoning
/// passed back unchanged with every tool result, a strict schema only on a final call without tools, the providers in order
/// with zero data retention and fp8 or better, and the provider that answered first kept for the rest of the round so that
/// the prompt cache works. Every request is in the AI request log under a <c>cookie.*</c> purpose, which Cookie's own
/// budget counts.
/// </summary>
public sealed class CookieModel(IHttpClientFactory http, IOptionsMonitor<AiOptions> ai, IOptionsMonitor<CookieOptions> options, CookieSettings settings,
    AiRequestLog log, IDbContextFactory<PnmtdDbContext> dbs, ILogger<CookieModel> logger)
{
    public const string PurposePrefix = "cookie.";

    public static readonly string[] AtLeastFp8 = ["fp8", "fp16", "bf16", "fp32"];

    public async Task<decimal> BudgetLimitAsync(CancellationToken ct) =>
        (await settings.GetAsync<ModelSettings>(ct)).BudgetUsd ?? options.CurrentValue.BudgetUsd;

    /// <summary>What Cookie spent this calendar month (PNMT's time zone).</summary>
    public async Task<decimal> SpentAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(Clock.LocalNow);
        var first = new DateOnly(today.Year, today.Month, 1);
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.AiSpending.Where(s => s.Day >= first && s.Purpose.StartsWith(PurposePrefix)).SumAsync(s => (decimal?)s.Cost, ct) ?? 0m;
    }

    public async Task EnsureBudgetAsync(CancellationToken ct)
    {
        var limit = await BudgetLimitAsync(ct);
        if (limit > 0 && await SpentAsync(ct) is var spent && spent >= limit)
        {
            throw new CookieBudgetException(spent, limit);
        }
    }

    /// <summary>
    /// One call. <paramref name="tools"/> lets the model call them (auto); <paramref name="schema"/> forces the answer's shape and
    /// is only used without tools. <paramref name="provider"/> keeps the round on the provider that answered first.
    /// </summary>
    public async Task<ModelTurn> CompleteAsync(string purpose, JsonArray messages, JsonArray? tools, (string Name, JsonObject Schema)? schema, string effort,
        string? provider, int maxTokens, CancellationToken ct)
    {
        var o = ai.CurrentValue;
        if (!o.Usable)
        {
            throw new AiNotConfiguredException("AI is off or has no key: Ai__Enabled=true and Ai__ApiKey.");
        }
        await EnsureBudgetAsync(ct);
        var models = await settings.GetAsync<ModelSettings>(ct);
        var c = options.CurrentValue;
        var model = string.IsNullOrWhiteSpace(models.Model) ? c.Model : models.Model.Trim();
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = messages.DeepClone(),
            ["reasoning"] = new JsonObject { ["effort"] = effort },
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        else if (schema is { } s)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = s.Name, ["strict"] = true, ["schema"] = s.Schema.DeepClone() },
            };
        }
        if (o.IsOpenRouter)
        {
            body["provider"] = Provider(c, models, provider);
        }
        var requestText = body.ToJsonString();
        var entry = new AiRequestEntity { Purpose = PurposePrefix + purpose, Attempt = 1, Role = AiRole.Main, ModelAsked = model, PromptVersion = "cookie-1" };
        var watch = Stopwatch.StartNew();
        string? responseText = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, o.BaseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(requestText, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
            request.Headers.TryAddWithoutValidation("X-Title", "PNMT Cookie");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, o.TimeoutSeconds * 2)));
            using var response = await http.CreateClient(ChatClient.HttpClientName).SendAsync(request, timeout.Token);
            responseText = await response.Content.ReadAsStringAsync(timeout.Token);
            entry.HttpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                throw new AiRequestException(AiOutcome.HttpError, $"HTTP {(int)response.StatusCode}", response.StatusCode != HttpStatusCode.BadRequest,
                    (int)response.StatusCode);
            }
            var turn = Parse(responseText, entry);
            entry.Outcome = AiOutcome.Ok;
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            var id = await log.WriteAsync(entry, requestText, responseText, CancellationToken.None);
            return turn with { RequestId = id, Cost = entry.Cost ?? 0m, Provider = entry.Provider };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = ex switch
            {
                AiRequestException a => a,
                OperationCanceledException => new AiRequestException(AiOutcome.Timeout, "No answer in time."),
                HttpRequestException h => new AiRequestException(AiOutcome.ConnectError, h.Message),
                JsonException j => new AiRequestException(AiOutcome.InvalidOutput, $"Not the expected JSON: {j.Message}"),
                _ => new AiRequestException(AiOutcome.ConnectError, ex.Message),
            };
            entry.Outcome = failure.Outcome;
            entry.Error = failure.Message;
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            await log.WriteAsync(entry, requestText, responseText, CancellationToken.None);
            logger.LogWarning("Cookie's call ({Purpose}) failed: {Error}", purpose, failure.Message);
            throw failure;
        }
    }

    /// <summary>OpenRouter's provider preferences (§14.3): the order, sticky within a round; ZDR; fp8 or better; the schema honoured.</summary>
    public static JsonObject Provider(CookieOptions c, ModelSettings models, string? sticky)
    {
        var order = (string.IsNullOrWhiteSpace(models.Providers) ? c.Providers : models.Providers)
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (sticky is not null)
        {
            order.RemoveAll(p => string.Equals(p, sticky, StringComparison.OrdinalIgnoreCase));
            order.Insert(0, sticky);
        }
        var provider = new JsonObject { ["require_parameters"] = true, ["data_collection"] = "deny", ["allow_fallbacks"] = true };
        if (order.Count > 0)
        {
            provider["order"] = new JsonArray([.. order.Select(p => (JsonNode)p)]);
        }
        if (c.Zdr)
        {
            provider["zdr"] = true;
        }
        if (string.Equals(c.MinQuantization, "fp8", StringComparison.OrdinalIgnoreCase))
        {
            provider["quantizations"] = new JsonArray([.. AtLeastFp8.Select(q => (JsonNode)q)]);
        }
        return provider;
    }

    internal static ModelTurn Parse(string responseText, AiRequestEntity entry)
    {
        ChatClient.Parse(InjectContent(responseText), entry);
        var root = JsonNode.Parse(responseText)!;
        var message = root["choices"]?[0]?["message"]?.AsObject() ?? throw new AiRequestException(AiOutcome.InvalidOutput, "The answer has no message.");
        var content = message["content"]?.GetValueKind() == JsonValueKind.String ? message["content"]!.GetValue<string>() : null;
        var reasoning = message["reasoning"]?.GetValueKind() == JsonValueKind.String ? message["reasoning"]!.GetValue<string>() : null;
        var calls = new List<ModelToolCall>();
        foreach (var call in message["tool_calls"]?.AsArray() ?? [])
        {
            var id = call?["id"]?.GetValue<string>();
            var name = call?["function"]?["name"]?.GetValue<string>();
            if (id is not null && name is not null)
            {
                calls.Add(new ModelToolCall(id, name, call!["function"]?["arguments"]?.GetValue<string>() ?? "{}"));
            }
        }
        // Sent back as received (OpenRouter's rule for reasoning across tool calls), without the fields only answers carry.
        var assistant = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (message["tool_calls"] is JsonArray toolCalls)
        {
            assistant["tool_calls"] = toolCalls.DeepClone();
        }
        if (message["reasoning_details"] is JsonArray details)
        {
            assistant["reasoning_details"] = details.DeepClone();
        }
        if (content is null && calls.Count == 0)
        {
            throw new AiRequestException(AiOutcome.InvalidOutput, "The answer has neither text nor tool calls.");
        }
        return new ModelTurn(content, calls, reasoning, assistant, 0, 0m, entry.Provider);
    }

    /// <summary>The package one parser wants a string content; a turn of tool calls has none.</summary>
    private static string InjectContent(string responseText)
    {
        var root = JsonNode.Parse(responseText)!;
        if (root["choices"]?[0]?["message"] is JsonObject message && message["content"]?.GetValueKind() != JsonValueKind.String)
        {
            message["content"] = "";
        }
        return root.ToJsonString();
    }
}
