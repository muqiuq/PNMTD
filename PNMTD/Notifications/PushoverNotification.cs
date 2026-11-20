using System.Diagnostics;
using System.Text.Json;
using PNMTD.Lib.Models.Enum;

namespace PNMTD.Notifications
{
    /// <summary>
    /// <c>pushover://USERKEY</c>. The API key is <c>Pushover:ApiKey</c>; <c>Pushover:Endpoint</c> lets tests
    /// send to a fake. The priority is the message's own (the debug page's), else the settings' for its importance
    /// (<see cref="PushoverPriorities"/>, docs/cookie-object-notes-plan.md §5.2); an emergency carries the settings' retry and expiry.
    /// </summary>
    public sealed class PushoverNotification(IConfiguration configuration, IHttpClientFactory http, PushoverPriorities priorities) : INotificationProvider
    {
        public const string PUSHOVER_APIKEY_KEY = "Pushover:ApiKey";
        public const string PUSHOVER_ENDPOINT = "https://api.pushover.net/1/messages.json";
        public const string Prefix = "pushover://";

        public string Name => "pushover";

        public static bool Matches(string recipient) => recipient.StartsWith(Prefix, StringComparison.Ordinal);

        public bool IsMatch(string recipient) => Matches(recipient);

        public static string UserKey(string recipient) => recipient[Prefix.Length..].Trim();

        public string? ApiKey => configuration[PUSHOVER_APIKEY_KEY];

        public string Endpoint => configuration["Pushover:Endpoint"] is { Length: > 0 } endpoint ? endpoint : PUSHOVER_ENDPOINT;

        /// <summary>The JSON Pushover gets: the app token, the user key, the title, the text, the priority, and an emergency's retry and expiry.</summary>
        private async Task<Dictionary<string, object>> BodyAsync(NotificationMessage message, CancellationToken ct)
        {
            var settings = await priorities.GetAsync(ct);
            var priority = message.Priority ?? settings.Of(message.Importance);
            var body = new Dictionary<string, object>
            {
                ["token"] = ApiKey ?? "",
                ["user"] = UserKey(message.Recipient),
                ["title"] = message.Subject,
                ["message"] = message.Short,
                ["priority"] = priority,
            };
            if (priority == PushoverPrioritySettings.Emergency)
            {
                // Pushover needs both for an emergency: how often the phone repeats it, and when it gives up (§5.2).
                body["retry"] = settings.RetrySeconds;
                body["expire"] = settings.ExpireSeconds;
            }
            if (!string.IsNullOrWhiteSpace(message.Device))
            {
                body["device"] = message.Device!.Trim();
            }
            return body;
        }

        public async Task<OutboundDescription> DescribeAsync(NotificationMessage message, CancellationToken ct) =>
            new("POST", Endpoint, [["Content-Type", "application/json; charset=utf-8"]],
                PNMTD.Outbound.OutboundRedactor.PushoverBody(JsonSerializer.Serialize(await BodyAsync(message, ct))));

        public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                return DeliveryResult.Failed($"No API key: {PUSHOVER_APIKEY_KEY} is not set.", watch.Elapsed);
            }
            var body = await BodyAsync(message, ct);
            try
            {
                using var response = await http.CreateClient(NotificationService.HttpClientName).PostAsJsonAsync(Endpoint, body, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                return Describe((int)response.StatusCode, response.IsSuccessStatusCode, text, watch.Elapsed);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return DeliveryResult.Failed($"Pushover could not be reached: {ex.Message}", watch.Elapsed);
            }
        }

        /// <summary>Pushover answers <c>{"status":1,"request":"…"}</c>, or <c>status</c> 0 with <c>errors</c>.</summary>
        internal static DeliveryResult Describe(int status, bool success, string body, TimeSpan duration)
        {
            string? request = null;
            var errors = new List<string>();
            var accepted = false;
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("request", out var r))
                {
                    request = r.GetString();
                }
                if (json.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number)
                {
                    accepted = s.GetInt32() == 1;
                }
                if (json.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array)
                {
                    errors.AddRange(e.EnumerateArray().Select(x => x.GetString()).OfType<string>());
                }
            }
            catch (JsonException)
            {
            }
            var suffix = request is null ? "" : $" (request {request})";
            return success && accepted
                ? new DeliveryResult(true, $"Pushover accepted it{suffix}.", duration)
                : DeliveryResult.Failed($"HTTP {status}: {(errors.Count > 0 ? string.Join("; ", errors) : "Pushover refused it")}{suffix}.", duration);
        }
    }
}
