using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileSystemGlobbing.Internal;
using PNMTD.Data;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;
using PNMTD.Services.DnsZones;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PNMTD.Tests.Services
{
    [TestClass]
    public class DnsZoneTest
    {
        private HttpClient _client;

        public static readonly CustomWebApplicationFactory<Program> _factory = new CustomWebApplicationFactory<Program>();

        public static PnmtdDbContext Db
        {
            get
            {
                return _factory.DbTestHelper.DbContext;
            }
        }

        [TestInitialize]
        public void Init()
        {
            _client = _factory.CreateClient();
        }

        [TestMethod]
        public void ParseDnsZoneTest()
        {
            var dnsZoneFile = new DnsZoneFile(DbTestHelper.DnsExampleZoneFile);

            Assert.AreEqual(6, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.A).Count());
            Assert.AreEqual(8, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.CNAME).Count());
            Assert.AreEqual(2, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.NS).Count());
            Assert.AreEqual(3, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.TXT).Count());
            Assert.AreEqual(7, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.MX).Count());
            Regex NSRegex = new Regex(@"^ns[12]\.example\.com$");
            Assert.IsTrue(
                NSRegex.IsMatch(dnsZoneFile.Records.Where(d => d.RecordType == DnsZoneResourceType.NS).Select(d => d.Value).First()));
            Assert.IsTrue(
                NSRegex.IsMatch(dnsZoneFile.Records.Where(d => d.RecordType == DnsZoneResourceType.NS).Select(d => d.Value).Last()));
            Assert.AreEqual("cc.zh.example.com", 
                dnsZoneFile.Records.Where(r => r.Name == "nas.example.com").Select(r => r.Value).Single());
            Assert.AreEqual("mx02.sui-inter.net",
                dnsZoneFile.Records.Where(r => r.Name == "example.com" && r.Priority == 20).Select(r => r.Value).Single());

            Assert.AreEqual("example.com", dnsZoneFile.Name);
        }

        [TestMethod]
        public void ParseAndTransformTest()
        {
            var dnsZoneFile = new DnsZoneFile(DbTestHelper.DnsExampleZoneFile);

            var dnsZoneEntity = dnsZoneFile.DnsZoneToEntity();

            Assert.AreEqual(dnsZoneEntity.ZoneName, dnsZoneFile.Name);

            Assert.AreEqual(dnsZoneEntity.DnsZoneEntries.Count, dnsZoneFile.Records.Count);
        }

        [TestMethod]
        public void ParseTransformAndAddTest()
        {
            var dnsZoneFile = new DnsZoneFile(DbTestHelper.DnsExampleZoneFile);

            var dnsZoneEntity = dnsZoneFile.DnsZoneToEntity();

            Db.DnsZones.Add(dnsZoneEntity);

            Db.SaveChanges();

            var dnsZoneFromDb = Db.DnsZones
                .Include(d => d.DnsZoneEntries)
                .First(d => d.Id == dnsZoneEntity.Id);

            Assert.AreEqual(dnsZoneFile.Name, dnsZoneFromDb.ZoneName);

            Assert.AreEqual(dnsZoneFile.Records.Count, dnsZoneFromDb.DnsZoneEntries.Count);

            dnsZoneFromDb.DnsZoneEntries.ForEach(r => Db.DnsZoneEntries.Remove(r));

            Db.DnsZones.Remove(dnsZoneEntity);

            Db.SaveChanges();

            Assert.IsFalse(Db.DnsZones.Any(d => d.Id == dnsZoneEntity.Id));
        }

        [TestMethod]
        public void InlineSoaRecordTest()
        {
            var soaPart = "( 1681728879 3600 300 1814400 300 )";

            var inlinedDnsZoneFile = DnsZoneFile.InlineSoaRecord(DbTestHelper.DnsExampleZoneFile);

            Assert.IsTrue(inlinedDnsZoneFile.Contains(soaPart));

            Assert.AreEqual(DbTestHelper.DnsExampleZoneFile.Length - 6, inlinedDnsZoneFile.Length);
        }
    }
}
