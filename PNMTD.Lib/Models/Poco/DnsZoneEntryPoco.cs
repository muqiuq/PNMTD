using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models.Poco
{
    public class DnsZoneEntryPoco
    {
        public Guid Id { get; set; }

        public bool Ignore { get; set; }

        public Guid DnsZoneId { get; set; }

        public int TTL { get; set; }

        public string Type { get; set; }

        public string ReferenceValue { get; set; }

        public string ActualValue { get; set; }

        public DateTime Updated { get; set; }

        [NotMapped]
        public bool IsMatch
        {
            get
            {
                return ActualValue == ReferenceValue;
            }
        }
    }
}
