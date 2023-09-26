using PNMTD.Lib.Models.Enum;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models.Poco
{
    public class DnsZoneLogEntryPoco
    {
        public Guid Id { get; set; }

        public DateTime Created { get; set; }

        public Guid? DnsZoneId { get; set; }

        public DnsZoneLogEntryType EntryType { get; set; }

        public string Message { get; set; }
    }
}
