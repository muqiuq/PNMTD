namespace PNMTD.Notifications
{
    public interface INotificationProvider
    {
        public void Configure(IConfiguration configuration);

        public void SendNotification(string recipient, string subject, string messageShort, string messageLong);

        public bool IsMatch(string recipient);
    }
}
