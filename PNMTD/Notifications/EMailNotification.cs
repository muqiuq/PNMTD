using PNMTD.Helper;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;

namespace PNMTD.Notifications
{
    public class EMailNotification : INotificationProvider 
    {
        private static readonly ILogger _logger = LogManager.CreateLogger<EMailNotification>();
        private string? smtpHost;
        private string? smtpSender;
        private string? smtpUsername;
        private string? smtpPassword;
        private int smtpPort;

        public bool IsMatch(string recipient)
        {
            Regex regexIsEMail = new Regex(@"^[\w-\.]+@([\w-]+\.)+[\w-]{2,4}$");

            return regexIsEMail.IsMatch(recipient);
        }

        public void Configure(IConfiguration configuration)
        {
            if (string.IsNullOrWhiteSpace(configuration["Smtp:Host"])
                || string.IsNullOrWhiteSpace(configuration["Smtp:Sender"])
               )
            {
                _logger.LogWarning("Smtp:Host or sender is empty. Define in appsettings.Production.json");
                return;
            }
            smtpHost = configuration["Smtp:Host"];
            smtpSender = configuration["Smtp:Sender"];
            smtpUsername = configuration["Smtp:Username"];
            smtpPassword = configuration["Smtp:Password"];
            if (!string.IsNullOrEmpty(configuration["Smtp:Port"]) && Int32.TryParse(configuration["Smtp:Port"], out int port))
            {
                smtpPort = port;
            }
        }

        public void SendNotification(string recipient, string subject, string messageShort, string messageLong)
        {
            if (smtpHost.IsNullOrEmpty() || smtpSender.IsNullOrEmpty())
            {
                _logger.LogError($"Smtp:Host or Smtp:sender is empty. Cannot send E-Mail to {recipient}");
                return;
            }
            SmtpClient client = new SmtpClient(smtpHost, smtpPort);

            if(smtpPort != 25)
            {
                client.EnableSsl = true;
            }
            
            if(!string.IsNullOrWhiteSpace(smtpUsername) 
                && !string.IsNullOrWhiteSpace(smtpPassword))
            {
                client.Credentials = new System.Net.NetworkCredential(smtpUsername, smtpPassword);
            }

            MailAddress from = new MailAddress(smtpSender);
            MailAddress to = new MailAddress(recipient);
            MailMessage message = new MailMessage(from, to);
            message.Body = messageLong;
            message.BodyEncoding = System.Text.Encoding.UTF8;
            message.Subject = subject;
            message.SubjectEncoding = System.Text.Encoding.UTF8;

            try
            {
                client.Send(message);
            }catch(SmtpException ex)
            {
                _logger.LogWarning($"Failed to Send E-Mail to {recipient}", ex);
            }

            message.Dispose();
        }
    }
}
