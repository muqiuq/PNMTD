using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Org.BouncyCastle.Asn1.X509;
using PNMTD.Data;
using PNMTD.Lib.Logic;
using PNMTD.Mails;
using PNMTD.Models.Db;
using PNMTD.Models.Helper;
using PNMTD.Notifications;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PNMTD.Tasks
{
    public class MailInboxCheckTask : IHostedService, IDisposable
    {
        private const string PROCESSED_SUBFOLDER_NAME = "ARCHIVE";
        private const string PREPROCESSED_SUBFOLDER_NAME = "RECEIVED";

        private const int DEFAULT_MARGIN_IN_DAYS = 7;
        private const int DEFAULT_DELETE_ARCHIVED_MESSAGES_OLDER_THEN_DAYS = 365;

        private readonly ILogger<MailInboxCheckTask> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;
        private int executionCount;
        private bool isRunning = false;
        private string? username;
        private string? host;
        private int port;
        private string? password;

        public MailInboxCheckTask(ILogger<MailInboxCheckTask> _logger, IServiceProvider services, IConfiguration configuration)
        {
            logger = _logger;
            this.services = services;
            this.configuration = configuration;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Starting MailInboxCheckTask");

            username = configuration["Mailbox:Username"];
            host = configuration["Mailbox:Host"];

            if (username.IsNullOrEmpty() || password.IsNullOrEmpty() || host.IsNullOrEmpty())
            {
                logger.LogError("MailInboxCheckTask failed to start. Require Mailbox:Username, Mailbox:Host, Mailbox:Password");
                return Task.CompletedTask;
            }

            port = 993;
            if (host.Split(":").Length == 2)
            {
                var portStr = host.Split(":")[1];
                if (int.TryParse(portStr, out var parsedPort))
                {
                    port = parsedPort;
                    host = host.Split(':')[0];
                }
            }

            password = configuration["Mailbox:Password"];

            logger.LogInformation($"MailInboxCheckTask started with {username} @ {host} port {port}");

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(60));

            return Task.CompletedTask;
        }


        private void tryDoWork(object? state)
        {
            try
            {
                if (isRunning) return;
                isRunning = true;
                doWork(state);
            }
            catch (Exception ex)
            {
                if (Debugger.IsAttached)
                {
                    throw ex;
                }
                logger.LogError("MailInboxCheckTask", ex);
            }
            isRunning = false;
        }

        private void doWork(object? state)
        {
            using (var dbContext = new PnmtdDbContext())
            {
                var count = Interlocked.Increment(ref executionCount);

                #region LoadDefaults from KeyValueStore
                var defaultMargin = DEFAULT_MARGIN_IN_DAYS;
                var defaultDeleteArchivedMessagesOlderThenDays = DEFAULT_DELETE_ARCHIVED_MESSAGES_OLDER_THEN_DAYS;

                if (dbContext.TryGetKeyValueByEnumSetIfFailed(Models.Enums.KeyValueKeyEnums.MAILBOX_DEFAULT_MARGIN_IN_DAYS, DEFAULT_MARGIN_IN_DAYS, out var outMailboxDefaultMarginInDays, readOnly: false))
                {
                    defaultMargin = outMailboxDefaultMarginInDays;
                }
                if (dbContext.TryGetKeyValueByEnumSetIfFailed(Models.Enums.KeyValueKeyEnums.MAILBOX_DEFAULT_DELETE_ARCHIVED_MESSAGES_OLDER_THEN_DAYS, DEFAULT_DELETE_ARCHIVED_MESSAGES_OLDER_THEN_DAYS, out var outMailboxDefautDeleteArchivedMessagesOlderThenDays, readOnly: false))
                {
                    defaultDeleteArchivedMessagesOlderThenDays = outMailboxDefautDeleteArchivedMessagesOlderThenDays;
                }
                #endregion

                using (var client = new ImapClient())
                {
                    #region Connect and prepare variables
                    client.Connect(host, port, true);

                    client.Authenticate(username, password);

                    var inbox = client.Inbox;

                    var toplevel = client.GetFolder(client.PersonalNamespaces[0]);

                    IMailFolder processedFolder = CreateOrGetFolder(toplevel, PROCESSED_SUBFOLDER_NAME);
                    IMailFolder preProcessedFolder = CreateOrGetFolder(toplevel, PREPROCESSED_SUBFOLDER_NAME);

                    int numOfMessagesMovedToProcessed = 0;

                    #endregion

                    #region Check if any messages in preprocessed folder are ready for move to processed folder

                    preProcessedFolder.Open(FolderAccess.ReadWrite);
                    if (preProcessedFolder.Count != 0)
                    {
                        var items = preProcessedFolder.Fetch(0, -1, MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.Flags | MessageSummaryItems.EmailId);

                        foreach (var item in items)
                        {
                            if (DateTime.Now - item.Date.DateTime > TimeSpan.FromDays(defaultMargin)) continue;

                            var message = preProcessedFolder.GetMessage(item.UniqueId);

                            var mailLog = dbContext.MailLogs.Where(ml =>
                                ml.Subject == message.Subject &&
                                ml.MessageDate == message.Date.DateTime &&
                                ml.To == MailHelper.ExtractMailAdresses(message.To) &&
                                ml.From == MailHelper.ExtractMailAdresses(message.From))
                                .SingleOrDefault();

                            if (mailLog == null) continue;

                            if (mailLog.Processed)
                            {
                                preProcessedFolder.MoveTo(item.UniqueId, processedFolder);
                                preProcessedFolder.Expunge();
                                numOfMessagesMovedToProcessed++;
                            }
                        }
                    }

                    if (numOfMessagesMovedToProcessed != 0) logger.LogInformation($"Moved {numOfMessagesMovedToProcessed} messages from {PREPROCESSED_SUBFOLDER_NAME} to {PROCESSED_SUBFOLDER_NAME} folder");

                    #endregion

                    #region Process new messages

                    inbox.Open(FolderAccess.ReadWrite);
                    if (inbox.Count != 0)
                    {
                        logger.LogInformation($"Found {inbox.Count} new E-Mails in {username} at {host}");

                        var items = inbox.Fetch(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.Flags | MessageSummaryItems.EmailId);

                        foreach (var item in items)
                        {
                            var message = inbox.GetMessage(item.UniqueId);
                            inbox.Store(item.UniqueId, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Seen) { Silent = true });

                            logger.LogTrace(message.MessageId);

                            var mailLog = new MailLogEntity()
                            {
                                Id = Guid.NewGuid(),
                                RemoteId = item.UniqueId.ToString(),
                                From = MailHelper.ExtractMailAdresses(message.From),
                                To = MailHelper.ExtractMailAdresses(message.To),
                                Content = MailHelper.ExtractBodyText(message),
                                Subject = message.Subject.ToString(),
                                MessageDate = message.Date.DateTime,
                                Created = DateTime.Now,
                                Processed = false,
                                ProcessedBy = null,
                                ProcessedById = null,
                                ProcessLog = ""
                            };
                            inbox.MoveTo(item.UniqueId, preProcessedFolder);
                            inbox.Expunge();
                            dbContext.MailLogs.Add(mailLog);
                            dbContext.SaveChanges();
                        }
                    }

                    #endregion

                    #region Delete old messages from archive
                    int deletedMessages = 0;
                    processedFolder.Open(FolderAccess.ReadWrite);
                    if (processedFolder.Count != 0)
                    {
                        var items = processedFolder.Fetch(0, -1, MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.Flags | MessageSummaryItems.EmailId);

                        foreach (var item in items)
                        {
                            if (DateTime.Now - item.Date.DateTime < TimeSpan.FromDays(defaultDeleteArchivedMessagesOlderThenDays)) continue;

                            processedFolder.AddFlags(item.UniqueId, MessageFlags.Deleted, false);
                            processedFolder.Expunge();
                            deletedMessages++;
                        }
                    }
                    #endregion

                    if (deletedMessages != 0) logger.LogInformation($"Deleted {deletedMessages} messages from {PROCESSED_SUBFOLDER_NAME} folder");

                    client.Disconnect(true);
                }
                dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_MAILBOX_CHECK);
            }
        }

        private IMailFolder CreateOrGetFolder(IMailFolder toplevel, string folderName)
        {
            var subfolders = toplevel.GetSubfolders();

            if (!subfolders.Any(sb => sb.Name == folderName))
            {
                logger.LogInformation($"Created {folderName} on mail server as archive");
                return toplevel.Create(folderName, true);
            }
            else
            {
                return subfolders.Single(sb => sb.Name == folderName);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("MailInboxCheckTask is stopping.");

            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }
    }
}
