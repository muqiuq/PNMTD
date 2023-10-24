using MailKit.Net.Imap;
using MailKit;
using Microsoft.IdentityModel.Tokens;
using PNMTD.Data;
using PNMTD.Mails;
using PNMTD.Models.Db;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using DnsClient;
using System.Net;
using PNMTD.Tasks.Helpers;
using PNMTD.Lib.Models.Enum;
using DnsClient.Protocol;
using PNMTD.Services.DnsZones;
using PNMTD.Migrations;
using System.Linq;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Tls;

namespace PNMTD.Tasks
{
    public class DnsCheckTask : IHostedService, IDisposable
    {
        private const string DefaultDnsServer = "9.9.9.9";
        private const int NumOfFailuresRequiredBeforeAlarm = 3;

        private readonly ILogger<DnsCheckTask> logger;
        private readonly IServiceProvider services;
        private readonly IConfiguration configuration;
        private Timer _timer;
        private int executionCount;
        private bool isRunning = false;

        public List<string> DnsServers = new List<string>();
        public List<NameServer> DnsNameServers = new List<NameServer>();

        public DnsCheckTask(ILogger<DnsCheckTask> _logger, IServiceProvider services, IConfiguration configuration)
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
            logger.LogInformation("Starting DnsCheckTask");

            if (configuration["dns:server1"] != null)
            {
                DnsServers.Add(configuration["dns:server1"]);
            }

            if (configuration["dns:server2"] != null)
            {
                DnsServers.Add(configuration["dns:server2"]);
            }
            if(DnsServers.Count == 0)
            {
                DnsServers.Add(DefaultDnsServer);
            }

            _timer = new Timer(tryDoWork, null, TimeSpan.Zero,
            TimeSpan.FromSeconds(15));

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
                logger.LogError($"DnsCheckTask {ex.Message}", ex);
            }
            isRunning = false;
        }

        private void doWork(object? state)
        {
            using (var dbContext = new PnmtdDbContext())
            {
                var count = Interlocked.Increment(ref executionCount);

                UpdateDnsServerConfig(dbContext);

                UpdateZonesThatRequireProcessing(dbContext);

                UpdateMostOutdatedZone(dbContext);

                dbContext.UpdateKeyValueTimestampToNow(Models.Enums.KeyValueKeyEnums.LAST_DNS_CHECK);
            }
        }

        public void UpdateDnsServerConfig(PnmtdDbContext dbContext)
        {
            if (dbContext.TryGetKeyValueByEnumSetIfFailed<string>(Models.Enums.KeyValueKeyEnums.DNS_SERVERS, string.Join(",", DnsServers), out var outDnsServers, readOnly: false))
            {
                DnsNameServers.Clear();
                outDnsServers.Split(",").ToList().ForEach(d => {
                    var dnsServerProvidedByUser = d.Trim();
                    if (IPAddress.TryParse(dnsServerProvidedByUser, out var ip))
                    {
                        DnsNameServers.Add(new NameServer(ip));
                    }
                    else
                    {
                        logger.LogError($"Could not parse IP address out of {dnsServerProvidedByUser}");
                    }
                });
                if (DnsNameServers.Count == 0)
                {
                    DnsNameServers.Add(new NameServer(IPAddress.Parse(DefaultDnsServer)));
                    logger.LogWarning($"No set DNS servers are valid. using default DNS server {DefaultDnsServer}");
                }
            }
        }

        private bool IncreaseNumOfFailuresAndHadAdjustMatch(DnsZoneEntryEntity dnsZoneEntry)
        {
            if (dnsZoneEntry.NumOfFailuresInARow >= NumOfFailuresRequiredBeforeAlarm)
            {
                dnsZoneEntry.IsMatch = false;
                return true;
            }
            else
            {
                dnsZoneEntry.NumOfFailuresInARow += 1;
                return false;
            }
        }

        private void UpdateMostOutdatedZone(PnmtdDbContext dbContext)
        {
            var dnsZone = dbContext
                    .DnsZones
                    .Where(d => d.Enabled && (d.NextCheck < DateTime.Now || d.ForceUpdate))
                    .OrderBy(d => d.LastChecked)
                    .FirstOrDefault();

            if (dnsZone == null) return;

            var logEntries = new List<DnsZoneLogEntryEntity>();

            var dnsZoneEntries = dbContext.DnsZoneEntries
                .Include(d => d.DnsZone)
                .Where(d => d.DnsZoneId == dnsZone.Id)
                .GroupBy(d => new { d.Name, d.RecordType })
                .Select(dg => new DnsGroupingResult()
                {
                    Name = dg.Key.Name,
                    RecordType = dg.Key.RecordType,
                    Entities = dg.ToList()
                })
                .ToList();

            dnsZone.ForceUpdate = false;
            dnsZone.LastChecked = DateTime.Now;
            dnsZone.NextCheck = DateTime.Now.AddSeconds(dnsZone.Interval);

            dbContext.SaveChanges();

            var lookup = new LookupClient(DnsNameServers.ToArray());

            foreach (var dnsZoneEntry in dnsZoneEntries)
            {
                var shoudLog = !(dnsZoneEntry.Entities.Count == 1 && dnsZoneEntry.Entities.First().Ignore);

                IDnsQueryResponse result = null;
                try
                {
                    result = lookup.Query(new DnsQuestion(dnsZoneEntry.Name, dnsZoneEntry.RecordType.ToQueryType()),
                    new DnsQueryAndServerOptions()
                    {
                        Retries = 2,
                        Timeout = TimeSpan.FromSeconds(5),
                    });
                }
                catch(DnsClient.DnsResponseException ex)
                {
                    var firstEntry = dnsZoneEntry.Entities.First();
                    if (IncreaseNumOfFailuresAndHadAdjustMatch(firstEntry))
                    {
                        if (shoudLog) logEntries.Add(NewDnsLogEntry(DnsZoneLogEntryType.ENTRY_DISCREPANCY, dnsZone,
                            $"Exception while looking up {dnsZoneEntry.Name} {dnsZoneEntry.RecordType}"));
                    }
                    logger.LogWarning(ex, $"{dnsZoneEntry.Name} {dnsZoneEntry.RecordType}");
                }
                
                if(result == null)
                {
                    dnsZoneEntry.Entities.First().NumOfFailuresInARow += 1;
                }

                var valuesToCompare = new List<string>();

                foreach (var answer in result.Answers)
                {
                    switch (answer)
                    {
                        case MxRecord mxRecord:
                            valuesToCompare.Add(mxRecord.Exchange);
                            break;
                        case CNameRecord cNameRecord:
                            valuesToCompare.Add(cNameRecord.CanonicalName);
                            break;
                        case ARecord aRecord:
                            valuesToCompare.Add(aRecord.Address.ToString());
                            break;
                        case AaaaRecord aaaaRecord:
                            valuesToCompare.Add(aaaaRecord.Address.ToString());
                            break;
                        case NsRecord nsRecord:
                            valuesToCompare.Add(nsRecord.NSDName.ToString());
                            break;
                        case TxtRecord txtRecord:
                            valuesToCompare.Add(string.Join("", txtRecord.Text));
                            break;
                        case SoaRecord soaRecord:
                            valuesToCompare.Add(soaRecord.ToString());
                            break;
                        default:
                            break;
                    }
                }

                var valuesToMatch = new List<DnsZoneEntryEntity>(dnsZoneEntry.Entities);
                var valuesMatched = new List<DnsZoneEntryEntity>();
                var valuesNotMatched = new List<string>();

                foreach (var valueToCompare in valuesToCompare)
                {
                    var matchingEntry = valuesToMatch.Where(e => e.ReferenceValue == valueToCompare).FirstOrDefault();
                    if (matchingEntry != null)
                    {
                        matchingEntry.ActualValue = valueToCompare;
                        matchingEntry.Updated = DateTime.Now;
                        matchingEntry.IsMatch = true;
                        matchingEntry.NumOfFailuresInARow = 0;
                        valuesMatched.Add(matchingEntry);
                        valuesToMatch.Remove(matchingEntry);
                    }
                    else
                    {
                        valuesNotMatched.Add(valueToCompare);
                    }
                }
                if(dnsZoneEntry.Entities.Count != valuesToCompare.Count && valuesToCompare.Count > 0)
                {
                    var valuesToMatchStr = string.Join(",", valuesToCompare);
                    if(shoudLog) logEntries.Add(NewDnsLogEntry(DnsZoneLogEntryType.ENTRY_DISCREPANCY, dnsZone,
                            $"Number of records mismatch (ref) {dnsZoneEntry.Entities.Count} vs (act) {valuesToCompare.Count} (for {valuesToMatchStr})"));
                }
                foreach (var valueToCompare in valuesNotMatched)
                {
                    if (valuesToMatch.Any())
                    {
                        var firstValue = valuesToMatch.First();
                        var actualValueAlreadyWrong = firstValue.ActualValue == valueToCompare;
                        firstValue.IsMatch = false;
                        firstValue.ActualValue = valueToCompare;
                        firstValue.Updated = DateTime.Now;
                        firstValue.NumOfFailuresInARow = 0;
                        valuesMatched.Add(firstValue);
                        valuesToMatch.Remove(firstValue);
                        if (shoudLog && !actualValueAlreadyWrong) logEntries.Add(NewDnsLogEntry(DnsZoneLogEntryType.ENTRY_DISCREPANCY, dnsZone, 
                            $"Value mismatch {firstValue.Name} ({firstValue.RecordType}) (ref) {firstValue.ReferenceValue} != (act) {valueToCompare}"));
                    }
                    else
                    {
                        if (shoudLog) logEntries.Add(NewDnsLogEntry(DnsZoneLogEntryType.ENTRY_DISCREPANCY, dnsZone,
                            $"Actual value contains more (act) {valueToCompare}"));
                    }
                }
                // By now all entries should have been matched.
                foreach(var valueToMatch in valuesToMatch)
                {
                    valueToMatch.Updated = DateTime.Now;
                    if(IncreaseNumOfFailuresAndHadAdjustMatch(valueToMatch))
                    {
                        if (shoudLog && valueToMatch.IsMatch) logEntries.Add(NewDnsLogEntry(DnsZoneLogEntryType.ENTRY_DISCREPANCY, dnsZone,
                            $"Could not resolve {dnsZoneEntry.Name} {dnsZoneEntry.RecordType}"));
                    }
                }


                dbContext.SaveChanges();
            }

            dnsZone.RecordsMatch = dbContext.DnsZoneEntries
                .Where(e => !e.Ignore && e.DnsZoneId == dnsZone.Id)
                .ToList()
                .All(e => e.IsMatch) && !logEntries.Any();

            dbContext.DnsZoneLogEntries.AddRange(logEntries);

            dbContext.SaveChanges();

            dbContext.DnsZoneLogEntries.RemoveRange(dbContext.DnsZoneLogEntries.Where(d => d.DnsZoneId == dnsZone.Id).Skip(500).ToList());

            dbContext.SaveChanges();
        }

        private DnsZoneLogEntryEntity NewDnsLogEntry(DnsZoneLogEntryType entryType, DnsZoneEntity dnsZoneEntity, string message)
        {
            return new DnsZoneLogEntryEntity()
            {
                Created = DateTime.Now,
                EntryType = DnsZoneLogEntryType.ENTRY_DISCREPANCY,
                DnsZoneId = dnsZoneEntity.Id,
                Id = Guid.NewGuid(),
                Message = message
            };
        }

        private void UpdateZonesThatRequireProcessing(PnmtdDbContext dbContext)
        {
            var dnsZones = dbContext.DnsZones
                .Include(d => d.DnsZoneEntries)
                .Where(d => d.RequiresProcessing).ToList();

            foreach (var dnsZone in dnsZones)
            {
                DnsZoneFile zoneFile;
                try
                {
                    zoneFile = new DnsZoneFile(dnsZone.ZoneFileContent);
                }catch(Exception ex)
                {
                    logger.LogError(ex, $"Parse error {dnsZone.Id}");
                    continue;
                }
                

                var otherZoneFile = zoneFile.DnsZoneToEntity();

                if(zoneFile.Name == null)
                {
                    dnsZone.ZoneName = "invalid zone file";
                    dnsZone.RequiresProcessing = false;
                }
                // if Zone name changed delete all entities
                else if(otherZoneFile.ZoneName != dnsZone.ZoneName)
                {
                    dbContext.DnsZoneEntries.RemoveRange(dnsZone.DnsZoneEntries);
                    dbContext.DnsZoneEntries.AddRange(otherZoneFile.DnsZoneEntries.Select(e => {
                            e.DnsZoneId = dnsZone.Id;
                            e.DnsZone = dnsZone;
                            return e;
                        }).ToList());
                    dnsZone.ZoneName = otherZoneFile.ZoneName;
                    dnsZone.RequiresProcessing = false;
                    dnsZone.LastChecked = DateTime.MinValue;
                }
                else
                {
                    List<DnsZoneEntryEntity> newEntitiesToCreate = new List<DnsZoneEntryEntity>();
                    List<DnsZoneEntryEntity> notFoundExistingEntries = new List<DnsZoneEntryEntity>(dnsZone.DnsZoneEntries);

                    foreach (var otherEntity in otherZoneFile.DnsZoneEntries)
                    {
                        var relevantEntities = dnsZone.DnsZoneEntries
                            .Where(d => d.Name == otherEntity.Name && d.RecordType == otherEntity.RecordType && d.ReferenceValue == otherEntity.ReferenceValue)
                            .ToList();

                        if (relevantEntities.Any())
                        {
                            if (relevantEntities.Count > 1)
                            {
                                dbContext.DnsZoneEntries.RemoveRange(relevantEntities.Skip(1));
                            }
                            relevantEntities.ForEach(r => notFoundExistingEntries.Remove(r));
                        }
                        else
                        {
                            newEntitiesToCreate.Add(otherEntity);
                        }
                    }
                    dbContext.DnsZoneEntries.RemoveRange(notFoundExistingEntries);
                    dbContext.DnsZoneEntries.AddRange(newEntitiesToCreate.Select(e => {
                        e.DnsZoneId = dnsZone.Id;
                        e.DnsZone = dnsZone;
                        return e;
                    }).ToList());
                }

                dnsZone.RequiresProcessing = false;
            }

            dbContext.SaveChanges();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("DnsCheckTask is stopping.");

            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }

    }

    class DnsGroupingResult
    {
        public DateTime Updated { get; set; }

        public string Name { get; set; }

        public DnsZoneResourceType RecordType { get; set; }

        public List<DnsZoneEntryEntity> Entities { get; set; }
    }
}
