using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Services.DnsZones;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Tests
{
    internal class DbTestHelper
    {
        public PnmtdDbContext DbContext = new PnmtdDbContext(inMemory: true);


        public const int NUMBER_OF_HOST_ENTITIES = 3;
        public const int NUMBER_OF_NOTIFICATIONS = 1;
        public const int NUMBER_OF_SENSOR_PER_HOST_ENTITIES = 3;
        public const int NUMBER_OF_EVENTS_PER_HOST_ENTITIES = 3;

        public List<HostEntity> HostEntities = new List<HostEntity>();

        public List<SensorEntity> SensorEntities = new List<SensorEntity>();

        public List<EventEntity> EventEntities = new List<EventEntity>();

        private readonly IConfiguration Configuration;

        public DbTestHelper(IConfiguration configuration)
        {
            Configuration = configuration;
            var numberOfMigrations = DbContext.Database.GetPendingMigrations().Count();
            DbContext.Database.Migrate();
            if(numberOfMigrations > 0)
            {
                Populate();
            }
            HostEntities = DbContext.Hosts.ToList();
            SensorEntities = DbContext.Sensors.ToList();
            EventEntities = DbContext.Events.ToList();

            //DbContext.Database.ExecuteSqlRaw("CREATE TABLE PNMTDTESTS");
        }

        public void Populate()
        {
            DbTestHelper.Populate(DbContext, Configuration);
        }

        public static void Populate(PnmtdDbContext dbContext, IConfiguration Configuration)
        {
            if (Configuration["Development:Email"] == null || Configuration["Development:HttpNotificationUrl"] == null)
            {
                throw new Exception("Development:Email and Development:HttpNotificationUrl required in Configuration (Usually UserSecrets)");
            }
            var sensorTypes = Enum.GetValues(typeof(SensorType)).Cast<SensorType>().ToList();
            var random = new Random();
            bool skipFirstNoEventHappend = false;
            for (int hostEntityCounter = 0; hostEntityCounter < NUMBER_OF_HOST_ENTITIES; hostEntityCounter++)
            {
                var h1 = new HostEntity()
                {
                    Created = DateTime.Now,
                    Enabled = true,
                    Id = Guid.NewGuid(),
                    Name = $"Testhost {hostEntityCounter}"
                };

                dbContext.Hosts.Add(h1);

                for (int sensorEntityCounter = 0; sensorEntityCounter <= 6; sensorEntityCounter++)
                {
                    var s1 = new SensorEntity()
                    {
                        Id = Guid.NewGuid(),
                        Created = DateTime.Now,
                        Enabled = true,
                        Name = $"{sensorTypes[sensorEntityCounter % sensorTypes.Count]} {sensorEntityCounter}",
                        Parent = h1,
                        Type = sensorTypes[sensorEntityCounter % sensorTypes.Count],
                        Interval = 60,
                        GracePeriod = 60,
                    };
                    s1.SetNewSecretToken();
                    dbContext.Sensors.Add(s1);

                    if(skipFirstNoEventHappend)
                    {
                        var multidayStreekStart = random.Next(0, 100);
                        var multidayStreekEnd = multidayStreekStart - random.Next(10, 30);
                        for (int c = 100; c >= 5; c--)
                        {
                            if (c == 15) continue;
                            
                            var code = random.Next(0, 444);
                            if(c > multidayStreekStart && c < multidayStreekEnd)
                            {
                                code = 200;
                            }
                            var e1 = new EventEntity()
                            {
                                Id = Guid.NewGuid(),
                                Created = DateTime.Now - TimeSpan.FromDays(c),
                                Code = code,
                                Message = "",
                                Sensor = s1,
                                Source = "127.0.0.1"
                            };
                            dbContext.Events.Add(e1);
                        }
                    }
                    else
                    {
                        skipFirstNoEventHappend = true;
                    }

                    
                }
            }
            dbContext.SaveChanges();

            var eventEntity = dbContext.Events.First();
            var notificationPoco1 = new NotificationRulePoco()
            {
                Enabled = true,
                Name = "E-Mail Test",
                Recipient = Configuration["Development:Email"],
                SubscribedSensors = new List<Guid>() { eventEntity.Sensor.Id }
            };
            var notificationPoco2 = new NotificationRulePoco()
            {
                Enabled = true,
                Name = "HTTP Test",
                Recipient = Configuration["Development:HttpNotificationUrl"],
                SubscribedSensors = new List<Guid>() { eventEntity.Sensor.Id }
            };

            var dnsZoneFile = new DnsZoneFile(DbTestHelper.DnsExampleZoneFile);

            var dnsZoneEntity = dnsZoneFile.DnsZoneToEntity();
            dnsZoneEntity.Enabled = false;

            dbContext.DnsZones.Add(dnsZoneEntity);

            var dnsZoneFile2 = new DnsZoneFile(DbTestHelper.DnsExampleZoneFile.Replace("example.com.", "example.net."));

            var dnsZoneEntity2 = dnsZoneFile2.DnsZoneToEntity();
            dnsZoneEntity2.Enabled = false;

            dbContext.DnsZones.Add(dnsZoneEntity2);

            dbContext.CreateNewNotificationRule(notificationPoco1);
            dbContext.CreateNewNotificationRule(notificationPoco2);

            dbContext.SaveChanges();
        }


        public const string DnsExampleZoneFile = @";Configuration for DNS Zone example.com

;-----;example.com;
example.com. 300 IN SOA ns1.example.com. ns2.example.com. (
1681728879
3600
300
1814400
300
)
example.com. 300 IN A 203.0.113.12
cc.zh.example.com. 300 IN A 203.0.113.234
mail.example.com. 300 IN A 203.0.113.131
sc.zh.example.com. 300 IN A 203.0.113.132
www.example.com. 300 IN A 203.0.113.12
zh.example.com. 300 IN A 203.0.113.134
anliker-cloud.example.com. 300 IN CNAME cc.zh.example.com.
email.kundenmail.example.com. 300 IN CNAME eu.mailgun.org.
email.mailer.example.com. 300 IN CNAME eu.mailgun.org.
kundeninfo.example.com. 300 IN CNAME example.com.
nas.example.com. 300 IN CNAME cc.zh.example.com.
smart.example.com. 300 IN CNAME cc.zh.example.com.
technik.example.com. 300 IN CNAME cc.zh.example.com.
vpn.example.com. 300 IN CNAME cc.zh.example.com.
example.com. 300 IN MX 10 mx01.sui-inter.net.
kundenmail.example.com. 300 IN MX 10 mxb.eu.mailgun.org.
kundenmail.example.com. 300 IN MX 10 mxa.eu.mailgun.org.
mailer.example.com. 300 IN MX 10 mxa.eu.mailgun.org.
mailer.example.com. 300 IN MX 10 mxb.eu.mailgun.org.
example.com. 300 IN NS ns2.example.com.
example.com. 300 IN NS ns1.example.com.
example.com. 300 IN TXT ""qhs6cl0ltbo0n1fdfelikeh2si""
example.com. 300 IN TXT ""MS=392301766DEED43956824BED47A95D038148A6D""
example.com. 300 IN TXT ""v=spf1 a mx ~all""
example.com. 300 IN MX 20 mx02.sui-inter.net.
example.com. 300 IN MX 30 mail.example.com.
";
    }
}
