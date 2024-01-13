using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PNMTD.Helper;

namespace PNMTD.Notifications
{
    public class PushoverNotification : INotificationProvider
    {
        private static readonly ILogger _logger = LogManager.CreateLogger<PushoverNotification>();
        
        private string? apikey;

        public const string PUSHOVER_APIKEY_KEY = "Pushover:ApiKey";
        public const string PUSHOVER_ENDPOINT = "https://api.pushover.net/1/messages.json";

        public void Configure(IConfiguration configuration)
        {
            apikey = configuration[PUSHOVER_APIKEY_KEY];
        }

        public void SendNotification(string recipient, string subject, string messageShort, string messageLong)
        {
            if (string.IsNullOrWhiteSpace(apikey))
            {
                _logger.LogError($"Cannot send Pushover notification without apikey ({PUSHOVER_APIKEY_KEY})");
                return;
            }
            using (var client = new HttpClient())
            {
                var userToken = recipient.Replace("pushover://", "").Trim();
                var requestData = new
                {
                    token = apikey,
                    user = userToken,
                    title = subject,
                    message = messageShort
                };
                
                var jsonContent = JsonSerializer.Serialize(requestData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                var postTask = client.PostAsync(PUSHOVER_ENDPOINT, content);
                postTask.Wait();
                var contentTask = postTask.Result.Content.ReadAsStringAsync();
                contentTask.Wait();
                _logger.LogDebug(contentTask.Result);
                if (!postTask.IsCompletedSuccessfully || postTask.Result == null)
                {
                    _logger.LogError($"Failed to Pushover to {recipient}");
                }
                if (!postTask.Result.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to Pushover to {recipient} with status code {postTask.Result.StatusCode} reason '{postTask.Result.ReasonPhrase}'");
                }
            }
            _logger.LogDebug($"Sended Pushover Notification to {recipient}");
        }

        public bool IsMatch(string recipient)
        {
            return recipient.StartsWith("pushover://");
        }
    }
}
