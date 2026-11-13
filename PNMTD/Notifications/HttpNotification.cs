using System.Diagnostics;

namespace PNMTD.Notifications
{
    /// <summary>An http(s) URL: the subject and the long text, as a plain-text POST.</summary>
    public sealed class HttpNotification(IHttpClientFactory http) : INotificationProvider
    {
        public string Name => "http";

        public static bool Matches(string recipient) =>
            recipient.StartsWith("http://", StringComparison.Ordinal) || recipient.StartsWith("https://", StringComparison.Ordinal);

        public bool IsMatch(string recipient) => Matches(recipient);

        public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var response = await http.CreateClient(NotificationService.HttpClientName)
                    .PostAsync(message.Recipient, new StringContent($"{message.Subject}\n{message.Long}"), ct);
                return response.IsSuccessStatusCode
                    ? new DeliveryResult(true, $"HTTP {(int)response.StatusCode}", watch.Elapsed)
                    : DeliveryResult.Failed($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", watch.Elapsed);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return DeliveryResult.Failed(ex.Message, watch.Elapsed);
            }
        }
    }
}
