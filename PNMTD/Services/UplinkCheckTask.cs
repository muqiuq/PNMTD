using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Models.Db;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PNMTD.Services
{
    public class UplinkCheckTask : IHostedService, IDisposable
    {
        private readonly ILogger<UplinkCheckTask> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private readonly HttpClient httpClient;
        private Timer _timer;
        private int executionCount;


        private const int MAXIMUM_NUMBER_OF_FAILURES_IN_A_ROW = 3;

        private List<string> uplinkEndpoints = new List<string>();
        private string uplinkSharedKey = null;

        public UplinkCheckTask(ILogger<UplinkCheckTask> _logger,
            IServiceProvider services,
            IConfiguration configuration,
            HttpClient httpClient)
        {
            logger = _logger;
            this.services = services;
            this.configuration = configuration;
            this.httpClient = httpClient;
        }

        public void Dispose()
        {
            _timer?.Dispose();

        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Starting UplinkCheckTask");

            using (var dbContext = new PnmtdDbContext())
            {
                dbContext.SetKeyValueEntryByEnum(Models.Enums.KeyValueKeyEnums.UPLINK_OK, true);
            }

            var uplink_sharedKey = configuration["UplinkCheck:SharedKey"];

            if (uplink_sharedKey == null)
            {
                logger.LogWarning("Uplink check task is missing configuration UplinkCheck:SharedKey. Will not start");
                return Task.CompletedTask;
            }
            uplinkSharedKey = uplink_sharedKey;
            for (int a = 1; a <= 3; a++)
            {
                var uplinkEndpoint = configuration[$"UplinkCheck:Url{a}"];
                if (uplinkEndpoint != null && (uplinkEndpoint.StartsWith("http://") || uplinkEndpoint.StartsWith("https://")))
                {
                    uplinkEndpoints.Add(uplinkEndpoint);
                }
            }
            if (uplinkEndpoints.Count >= 2)
            {
                logger.LogInformation($"UplinkCheckTask is running with {uplinkEndpoints.Count} endpoints");
            }
            else
            {
                logger.LogError("Not enough UplinkCheck:UrlN (N is number from 1 to 3) supplied. Will not start");
                return Task.CompletedTask;
            }

            

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(10));

            return Task.CompletedTask;
        }


        private void tryDoWork(object? state)
        {
            try
            {
                var t = doWork(state);
                t.Wait();
            }
            catch (Exception ex)
            {
                logger.LogError("UplinkCheckTask", ex);
            }
        }

        int numberOfFailuresInARow = 0;
        int currentPosition = 0;
        bool lastTryFailed = false;
        bool justWereInFailedState = false;

        private async Task doWork(object? state)
        {
            using(var dbContext = new PnmtdDbContext())
            {
                var count = Interlocked.Increment(ref executionCount);
                currentPosition++;

                if (currentPosition >= uplinkEndpoints.Count)
                {
                    currentPosition = 0;
                }

                if (lastTryFailed)
                {
                    numberOfFailuresInARow++;
                }
                else
                {
                    if (numberOfFailuresInARow > 0) numberOfFailuresInARow--;
                }
                lastTryFailed = true;

                if (numberOfFailuresInARow >= MAXIMUM_NUMBER_OF_FAILURES_IN_A_ROW && !justWereInFailedState)
                {
                    dbContext.SetKeyValueEntryByEnum(Models.Enums.KeyValueKeyEnums.UPLINK_OK, false);
                    justWereInFailedState = true;
                }
                else if (numberOfFailuresInARow == 0 && justWereInFailedState)
                {
                    dbContext.SetKeyValueEntryByEnum(Models.Enums.KeyValueKeyEnums.UPLINK_OK, true);
                    justWereInFailedState = false;
                }

                dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_UPLINK_CHECK);

                var currentUrlEndpoint = uplinkEndpoints[currentPosition];

                dbContext.SetKeyValueEntryByEnum(Models.Enums.KeyValueKeyEnums.LAST_UPLINK_CHECKED_LINK, currentUrlEndpoint);

                var challenge = RandomString(32);

                var result = await httpClient.GetAsync(QueryHelpers.AddQueryString(currentUrlEndpoint, "challenge", challenge));

                if (result.IsSuccessStatusCode)
                {
                    var content = await result.Content.ReadAsStringAsync();
                    var cParts = content.Split(",");

                    if (cParts.Length == 2)
                    {
                        var correctHash = ComputeHMACSHA256(challenge, uplinkSharedKey, cParts[1]);
                        if (correctHash.ToUpper() == cParts[0].ToUpper())
                        {
                            lastTryFailed = false;
                            dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_UPLINK_CHECK_SUCCESSFULL);
                        }
                    }
                }
            }
        }

        public static string ComputeHMACSHA256(string message, string secret, string randomPart)
        {
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] msg = Encoding.UTF8.GetBytes(message + "-" + randomPart);

            using (HMACSHA256 hmac = new HMACSHA256(key))
            {
                byte[] hashBytes = hmac.ComputeHash(msg);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
            }
        }

        private static Random random = new Random();
        private object currentUrlEndpoint;

        public static string RandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("UplinkCheckTask is stopping.");

            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }
    }
}
