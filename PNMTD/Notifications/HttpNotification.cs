
using PNMTD.Helper;

namespace PNMTD.Notifications
{
    public class HttpNotification : INotificationProvider
    {
        private static readonly ILogger _logger = LogManager.CreateLogger<HttpNotification>();
        public bool IsMatch(string recipient)
        {
            return recipient.StartsWith("http://") || recipient.StartsWith("https://");
        }

        public void SendNotification(string recipient, string subject, string messageContent)
        {
            using(var client = new HttpClient())
            {
                var content = new StringContent($"{subject}\n{messageContent}");
                var postTask = client.PostAsync(recipient, content);
                postTask.Wait();
                if(!postTask.IsCompletedSuccessfully || postTask.Result == null)
                {
                    _logger.LogError($"Failed to Post to {recipient}");
                }
                if(!postTask.Result.IsSuccessStatusCode)
                {
                    _logger.LogError($"Failed to Post to {recipient} with status code {postTask.Result.StatusCode} reason '{postTask.Result.ReasonPhrase}'");
                }
            }
        }
    }
}
