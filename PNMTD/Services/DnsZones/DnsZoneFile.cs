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

        public static string InlineSoaRecord(string rawZoneFile)
        {
            var returnStr = "";
            var soaStart = rawZoneFile.IndexOf("IN SOA");
            if (soaStart == -1) return rawZoneFile;
            returnStr = rawZoneFile.Substring(0, soaStart);
            int endPos = -1;
            bool lastCharacterWasWhitespace = false;
            for(int a = soaStart; a < rawZoneFile.Length; a++)
            {
                if(rawZoneFile[a] != '\n' && rawZoneFile[a] != '\r')
                {
                    returnStr += rawZoneFile[a];
                }
                else if (rawZoneFile[a] == '\n')
                {
                    if(!lastCharacterWasWhitespace)
                    {
                        returnStr += ' ';
                    }
                }
                if (rawZoneFile[a] == ')')
                {
                    endPos = a;
                    break;
                }
                lastCharacterWasWhitespace = rawZoneFile[a] == ' ';
            }
            if(endPos != -1)
            {
                endPos += 1;
                returnStr += rawZoneFile.Substring(endPos, rawZoneFile.Length - endPos);
            }
            return returnStr;
        }

        private void parse()
        {
            String[] lines = InlineSoaRecord(Raw).Split('\n');

            var records = new List<DnsZoneResourceRecord>();

            foreach(var line in lines)
            {
                var parts = line.Split(" ", 5);
                var typeNames = Enum.GetNames(typeof(DnsZoneResourceType));
                if (parts.Length == 5 && parts[2] == "IN")
                {
                    var typeString = parts[3].Trim();
                    var value = parts[4].Trim();
                    var name = parts[0].Trim();
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
                    if(typeVal == DnsZoneResourceType.CNAME 
                        || typeVal == DnsZoneResourceType.NS 
                        || typeVal == DnsZoneResourceType.MX)
                    {
                        if(value.EndsWith(".")) value = value.Substring(0, value.Length - 1);
                    }
                    if (name.EndsWith(".")) name = name.Substring(0, name.Length - 1);
                    if (!Int32.TryParse(parts[1].Trim(), out int timeout)) continue;
                    var record = new DnsZoneResourceRecord()
                    {
                        Name = name,
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
