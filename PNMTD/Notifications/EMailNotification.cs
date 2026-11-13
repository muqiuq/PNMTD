using System.Diagnostics;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace PNMTD.Notifications
{
    /// <summary>
    /// A mail address, sent with <c>Smtp:*</c>. MailKit with <see cref="SecureSocketOptions.Auto"/>
    /// (docs/ai-operations-plan.md §8.3): implicit TLS on 465, STARTTLS where the server offers it.
    /// System.Net.Mail, before, could not do 465 at all.
    /// </summary>
    public sealed partial class EMailNotification(IConfiguration configuration) : INotificationProvider
    {
        public string Name => "email";

        [GeneratedRegex(@"^[\w-\.]+@([\w-]+\.)+[\w-]{2,4}$")]
        private static partial Regex Address();

        public static bool Matches(string recipient) => Address().IsMatch(recipient);

        public bool IsMatch(string recipient) => Matches(recipient);

        public string? Host => configuration["Smtp:Host"];

        public string? Sender => configuration["Smtp:Sender"];

        public string? Username => configuration["Smtp:Username"];

        /// <summary>25 when not set, as before.</summary>
        public int Port => configuration.GetValue("Smtp:Port", 0) is > 0 and var port ? port : 25;

        public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Sender))
            {
                return DeliveryResult.Failed("Smtp:Host or Smtp:Sender is not set.", watch.Elapsed);
            }
            var mail = new MimeMessage();
            try
            {
                mail.From.Add(MailboxAddress.Parse(Sender));
                mail.To.Add(MailboxAddress.Parse(message.Recipient));
            }
            catch (ParseException ex)
            {
                return DeliveryResult.Failed($"Not a mail address: {ex.Message}", watch.Elapsed);
            }
            mail.Subject = message.Subject;
            mail.Body = new TextPart("plain") { Text = message.Long };
            var where = $"{Host}:{Port}";
            using var client = new SmtpClient { Timeout = 30_000 };
            try
            {
                await client.ConnectAsync(Host, Port, SecureSocketOptions.Auto, ct);
                var tls = client.IsEncrypted ? $"TLS ({client.SslProtocol})" : "no TLS";
                var password = configuration["Smtp:Password"];
                var login = "no login";
                if (!string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(password))
                {
                    await client.AuthenticateAsync(Username, password, ct);
                    login = $"signed in as {Username}";
                }
                var answer = await client.SendAsync(mail, ct);
                await client.DisconnectAsync(true, ct);
                return new DeliveryResult(true, $"{where}, {tls}, {login}: {answer.Trim()}", watch.Elapsed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return DeliveryResult.Failed($"{where}: {ex.GetType().Name}: {ex.Message}", watch.Elapsed);
            }
        }
    }
}
