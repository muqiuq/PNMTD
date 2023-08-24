using MailKit.Net.Pop3;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using PNMTD.Data;
using PNMTD.Lib.Logic;
using PNMTD.Mails;
using PNMTD.Models.Db;
using PNMTD.Models.Helper;
using PNMTD.Notifications;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PNMTD.Services
{
    public class MailInboxCheckTask : IHostedService, IDisposable
    {
        private readonly ILogger<MailInboxCheckTask> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;
        private int executionCount;
        private string? username;
        private string? host;
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
            password = configuration["Mailbox:Password"];

            if(username.IsNullOrEmpty() || password.IsNullOrEmpty() || host.IsNullOrEmpty()) {
                logger.LogError("MailInboxCheckTask failed to start. Require Mailbox:Username, Mailbox:Host, Mailbox:Password");
                return Task.CompletedTask;
            }

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(60));

            return Task.CompletedTask;
        }


        private void tryDoWork(object? state)
        {
            try
            {
                doWork(state);
            }
            catch (Exception ex)
            {
                logger.LogError("MailInboxCheckTask", ex);
            }
            
        }

        private void doWork(object? state)
        {
            using(var dbContext = new PnmtdDbContext())
            {
                var count = Interlocked.Increment(ref executionCount);

                using (var client = new Pop3Client())
                {
                    client.Connect(host, 995, true);

                    client.Authenticate(username, password);

                    if (client.Count != 0)
                    {
                        logger.LogInformation($"Found {client.Count} new E-Mails in {username} at {host}");

                        for (int i = 0; i < client.Count; i++)
                        {
                            var message = client.GetMessage(i);
                            var mailLog = new MailLogEntity()
                            {
                                Id = Guid.NewGuid(),
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
                            dbContext.MailLogs.Add(mailLog);
                            dbContext.SaveChanges();
                            client.DeleteMessage(i);
                        }
                    }
                    client.Disconnect(true);
                }
                dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_MAILBOX_CHECK);
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
