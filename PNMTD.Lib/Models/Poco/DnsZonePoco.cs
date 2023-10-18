using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models.Poco
{
    public class DnsZonePoco
    {
        public Guid Id { get; set; }

        public bool Enabled { get; set; }

        public bool RequiresProcessing { get; set; }

        public DateTime LastChecked { get; set; }

        public bool RecordsMatch { get; set; }
        public string ZoneName { get; set; }

        public string ZoneFileContent { get; set; }

        public List<DnsZoneEntryPoco> ZoneEntries { get; set; } = new List<DnsZoneEntryPoco>();
    }
}
