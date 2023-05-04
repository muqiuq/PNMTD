using System.Net.Mail;
using System.Text.RegularExpressions;

namespace PNMTD.Notifications
{
    public class EMailNotification : INotificationProvider
    {
        public bool IsMatch(string recipient)
        {
            Regex regexIsEMail = new Regex(@"^[\w-\.]+@([\w-]+\.)+[\w-]{2,4}$");

            return regexIsEMail.IsMatch(recipient);
        }

        public void SendNotification(string recipient, string subject, string messageContent)
        {
            SmtpClient client = new SmtpClient(GlobalConfiguration.SMTP_HOST);
            client.Credentials = new System.Net.NetworkCredential(GlobalConfiguration.SMTP_USERNAME, GlobalConfiguration.SMTP_PASSWORD);

            MailAddress from = new MailAddress(GlobalConfiguration.SMTP_SENDER);
            MailAddress to = new MailAddress(recipient);

            MailMessage message = new MailMessage(from, to);
            message.Body = messageContent;
            // Include some non-ASCII characters in body and subject.
            message.BodyEncoding = System.Text.Encoding.UTF8;
            message.Subject = subject;
            message.SubjectEncoding = System.Text.Encoding.UTF8;

            client.Send(message);

            message.Dispose();
        }
    }
}
