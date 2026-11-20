using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Outbound;

namespace PNMTD.Notifications
{
    /// <summary>
    /// A mail address, sent with <c>Smtp:*</c>. MailKit with <see cref="SecureSocketOptions.Auto"/>
    /// (docs/ai-operations-plan.md §8.3): implicit TLS on 465, STARTTLS where the server offers it.
    /// System.Net.Mail, before, could not do 465 at all. Each mail goes into the notification log
    /// (docs/redmine-queue-outbound-plan.md §6.3): its headers, the whole MIME message, the server's answer and the SMTP
    /// conversation, <c>AUTH</c> redacted.
    /// </summary>
    public sealed partial class EMailNotification(IConfiguration configuration, OutboundLog outbound) : INotificationProvider
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

        private string Target => $"smtp://{Host}:{Port}";

        /// <summary>The mail as it goes out, or why it can't.</summary>
        private (MimeMessage? Mail, string? Error) Build(NotificationMessage message)
        {
            var mail = new MimeMessage();
            try
            {
                mail.From.Add(MailboxAddress.Parse(Sender));
                mail.To.Add(MailboxAddress.Parse(message.Recipient));
            }
            catch (ParseException ex)
            {
                return (null, $"Not a mail address: {ex.Message}");
            }
            mail.Subject = message.Subject;
            mail.Body = new TextPart("plain") { Text = message.Long };
            return (mail, null);
        }

        private static List<string[]> Headers(MimeMessage mail) => mail.Headers.Select(h => new[] { h.Field, h.Value }).ToList();

        private static string Mime(MimeMessage mail)
        {
            using var buffer = new MemoryStream();
            mail.WriteTo(buffer);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        public Task<OutboundDescription> DescribeAsync(NotificationMessage message, CancellationToken ct)
        {
            var (mail, _) = Build(message);
            return Task.FromResult(mail is null
                ? new OutboundDescription("SMTP", Target, [["To", message.Recipient], ["Subject", message.Subject]], message.Long)
                : new OutboundDescription("SMTP", Target, Headers(mail), Mime(mail)));
        }

        public async Task<DeliveryResult> SendAsync(NotificationMessage message, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Sender))
            {
                return DeliveryResult.Failed("Smtp:Host or Smtp:Sender is not set.", watch.Elapsed);
            }
            var (mail, error) = Build(message);
            if (mail is null)
            {
                return DeliveryResult.Failed(error!, watch.Elapsed);
            }
            var where = $"{Host}:{Port}";
            var context = OutboundScope.Current;
            if (context is not null)
            {
                context.Recorded = true;
            }
            var entry = new OutboundNotificationEntity
            {
                StartedAt = Clock.UtcNow, Channel = OutboundChannel.Email, Method = "SMTP", Target = Target, Recipient = message.Recipient,
                Summary = message.Subject, Purpose = context?.Purpose ?? "(not a notification)", AskedBy = context?.AskedBy ?? "PNMT",
                RequestHeaders = OutboundRedactor.Json(Headers(mail)),
            };
            var mime = Mime(mail);
            using var transcript = new MemoryStream();
            var running = outbound.Begin(OutboundLog.NotificationsLog, nameof(OutboundChannel.Email), "SMTP", Target, entry.Purpose, entry.AskedBy);
            try
            {
                using var client = new SmtpClient(new ProtocolLogger(transcript, leaveOpen: true) { RedactSecrets = true }) { Timeout = 30_000 };
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
                entry.Outcome = OutboundOutcome.Ok;
                entry.StatusCode = 250;
                entry.DurationMs = (int)watch.ElapsedMilliseconds;
                outbound.Record(entry, mime, answer.Trim(), Text(transcript));
                return new DeliveryResult(true, $"{where}, {tls}, {login}: {answer.Trim()}", watch.Elapsed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                entry.Outcome = OutboundOutcome.Failed;
                entry.StatusCode = ex is SmtpCommandException command ? (int)command.StatusCode : null;
                entry.Error = $"{ex.GetType().Name}: {ex.Message}";
                entry.DurationMs = (int)watch.ElapsedMilliseconds;
                outbound.Record(entry, mime, null, Text(transcript));
                return DeliveryResult.Failed($"{where}: {ex.GetType().Name}: {ex.Message}", watch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                entry.Outcome = OutboundOutcome.Cancelled;
                entry.DurationMs = (int)watch.ElapsedMilliseconds;
                outbound.Record(entry, mime, null, Text(transcript));
                throw;
            }
            finally
            {
                outbound.End(running);
            }
        }

        private static string Text(MemoryStream transcript) => Encoding.UTF8.GetString(transcript.ToArray());
    }
}
