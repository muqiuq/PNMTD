﻿using Microsoft.Extensions.FileSystemGlobbing.Internal;
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
        [TestMethod]
        public void ParseDnsZoneTest()
        {
            var dnsZoneFile = new DnsZoneFile(DbTestHelper.DnsExampleZoneFile);

            Assert.AreEqual(6, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.A).Count());
            Assert.AreEqual(8, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.CNAME).Count());
            Assert.AreEqual(2, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.NS).Count());
            Assert.AreEqual(3, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.TXT).Count());
            Assert.AreEqual(7, dnsZoneFile.Records.Where(r => r.RecordType == DnsZoneResourceType.MX).Count());
            Regex NSRegex = new Regex(@"^ns[12]\.example\.com\.$");
            Assert.IsTrue(
                NSRegex.IsMatch(dnsZoneFile.Records.Where(d => d.RecordType == DnsZoneResourceType.NS).Select(d => d.Value).First()));
            Assert.IsTrue(
                NSRegex.IsMatch(dnsZoneFile.Records.Where(d => d.RecordType == DnsZoneResourceType.NS).Select(d => d.Value).Last()));
            Assert.AreEqual("cc.zh.example.com.", 
                dnsZoneFile.Records.Where(r => r.Name == "nas.example.com.").Select(r => r.Value).Single());
            Assert.AreEqual("mx02.sui-inter.net.",
                dnsZoneFile.Records.Where(r => r.Name == "example.com." && r.Priority == 20).Select(r => r.Value).Single());
        }
    }
}
