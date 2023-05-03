namespace PNMTD.Notifications
{
    public interface INotificationProvider
    {

        public void SendNotification(string recipient, string subject, string messageContent);

        public bool IsMatch(string recipient);
    }
}
