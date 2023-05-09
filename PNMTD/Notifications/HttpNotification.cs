
namespace PNMTD.Notifications
{
    public class HttpNotification : INotificationProvider
    {
        public bool IsMatch(string recipient)
        {
            return recipient.StartsWith("http://") || recipient.StartsWith("https://");
        }

        public void SendNotification(string recipient, string subject, string messageContent)
        {
            throw new NotImplementedException();
        }
    }
}
