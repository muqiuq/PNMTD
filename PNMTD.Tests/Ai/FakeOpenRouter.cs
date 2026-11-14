using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Ai;

namespace PNMTD.Tests.Ai;

/// <summary>
/// OpenRouter as PNMTD's AI clients see it (docs/ai-operations-plan.md §18): chat completions answer with
/// <see cref="Answer"/> (by the schema's name), or with a scripted failure; Jev's decisions answer
/// <see cref="Injection"/>. Every request is kept.
/// </summary>
public sealed class FakeOpenRouter
{
    public Func<string, JsonNode, string>? Answer { get; set; }

    public double Injection { get; set; } = 0.01;

    /// <summary>Other noul questions' answers by name (the knowledge base's credentials and personalData); <see cref="Injection"/> otherwise.</summary>
    public ConcurrentDictionary<string, double> Nouls { get; } = new();

    /// <summary>Failures for the next chat requests: an HTTP status, or 0 for a timeout-like exception.</summary>
    public ConcurrentQueue<int> Failures { get; } = new();

    public ConcurrentQueue<(string Url, string Body, string? Authorization)> Requests { get; } = new();

    public int ChatCalls => Requests.Count(r => r.Url.EndsWith("/chat/completions", StringComparison.Ordinal));

    public int GuardCalls => Requests.Count(r => r.Url.Contains("/decisions", StringComparison.Ordinal));

    public static readonly Dictionary<string, string?> Settings = new()
    {
        ["Ai:Enabled"] = "true",
        ["Ai:ApiKey"] = "sk-or-test-key",
        ["Ai:Retry:QuickDelaySeconds"] = "1",
    };

    public void Install(IServiceCollection services) =>
        services.AddHttpClient(ChatClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new Handler(this));

    private sealed class Handler(FakeOpenRouter owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            var url = request.RequestUri!.ToString();
            owner.Requests.Enqueue((url, body, request.Headers.Authorization?.ToString()));
            var json = JsonNode.Parse(body)!;
            if (url.Contains("/decisions", StringComparison.Ordinal))
            {
                var answers = new JsonObject();
                foreach (var (name, q) in json["questions"]!.AsObject())
                {
                    answers[name] = q!["type"]!.GetValue<string>() == "noul"
                        ? new JsonObject { ["type"] = "noul", ["noul"] = owner.Nouls.TryGetValue(name, out var p) ? p : owner.Injection }
                        : new JsonObject { ["type"] = "choice", ["choice"] = "other", ["probabilities"] = new JsonObject { ["other"] = 0.9 }, ["confidence"] = 0.9 };
                }
                answers["usage"] = new JsonObject { ["cost"] = 0.00001 };
                return Json(HttpStatusCode.OK, answers.ToJsonString());
            }
            if (owner.Failures.TryDequeue(out var status))
            {
                if (status == 0)
                {
                    throw new HttpRequestException("Connection refused (openrouter.ai:443)");
                }
                return Json((HttpStatusCode)status, new JsonObject { ["error"] = new JsonObject { ["message"] = $"scripted {status}", ["code"] = status } }.ToJsonString());
            }
            var schema = json["response_format"]!["json_schema"]!;
            var content = owner.Answer?.Invoke(schema["name"]!.GetValue<string>(), json) ?? """{"answer":"pong"}""";
            var response = new JsonObject
            {
                ["id"] = $"gen-{owner.Requests.Count}",
                ["model"] = json["model"]!.GetValue<string>(),
                ["provider"] = "FakeProvider",
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 120, ["completion_tokens"] = 30, ["total_tokens"] = 150, ["cost"] = 0.0012,
                    ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                    ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 7 },
                },
            };
            return Json(HttpStatusCode.OK, response.ToJsonString());
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
