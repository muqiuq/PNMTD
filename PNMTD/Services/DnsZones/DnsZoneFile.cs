using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Enums;

namespace PNMTD.Services.DnsZones
{
    public class DnsZoneFile
    {
        public IReadOnlyList<DnsZoneResourceRecord> Records;
        public readonly string Raw;

        public DnsZoneFile(string raw) {
            this.Raw = raw;
            parse();
        }

        public string Name
        {
            get
            {
                return Records.First(p => p.RecordType == DnsZoneResourceType.SOA).Name;
            }
        }

        private void parse()
        {
            String[] lines = Raw.Split('\n');

            var records = new List<DnsZoneResourceRecord>();

            foreach(var line in lines)
            {
                var parts = line.Split(" ", 5);
                var typeNames = Enum.GetNames(typeof(DnsZoneResourceType));
                if (parts.Length == 5 && parts[2] == "IN")
                {
                    var typeString = parts[3].Trim();
                    var value = parts[4].Trim();
                    if (!typeNames.Contains(typeString)) continue;
                    var typeVal = (DnsZoneResourceType) Enum.Parse(typeof(DnsZoneResourceType), typeString);
                    int priority = -1;
                    if (typeVal == DnsZoneResourceType.MX)
                    {
                        var mxParts = parts[4].Split(" ");
                        if(mxParts.Length != 2) continue;
                        value = mxParts[1].Trim();
                        if (!Int32.TryParse(mxParts[0].Trim(), out priority)) continue;
                    }
                    if (!Int32.TryParse(parts[1].Trim(), out int timeout)) continue;
                    var record = new DnsZoneResourceRecord()
                    {
                        Name = parts[0].Trim(),
                        Timeout = timeout,
                        RecordType = typeVal,
                        Value = value,
                        Priority = priority == -1 ? null : priority
                    };
                    records.Add(record);
                }
            }
            Records = records.AsReadOnly();
        }

    }
}
