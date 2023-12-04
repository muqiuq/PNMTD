using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PNMTD.Lib.Helper
{
    public class RemoteAddressHelper
    {

        public static string StripPortFromRemoteAddressIfAny(string remoteAddress)
        {
            var portEnding = new Regex(@":\d+$");
            var portEndingMatch = portEnding.Match(remoteAddress);
            if (portEndingMatch.Success)
            {
                return remoteAddress.Substring(0, portEndingMatch.Groups[0].Index);
            }
            return remoteAddress;
        }

        public static bool TryGetRemoteIp(string remoteAddressWithPort, out string? remoteIP)
        {
            var ipv4MappedToIpv6WithPort = new Regex(@"^::ffff:\d+\.\d+\.\d+\.\d+\:\d+$");
            var ipv4WithPort = new Regex(@"^\d+\.\d+\.\d+\.\d+\:\d+$");

            var ipv4MappedToIpv6WithPortMatch = ipv4MappedToIpv6WithPort.Match(remoteAddressWithPort);
            var ipv4WithPortMatch = ipv4WithPort.Match(remoteAddressWithPort);
            if (ipv4MappedToIpv6WithPortMatch.Success || ipv4WithPortMatch.Success)
            {
                var portEnding = new Regex(@":\d+$");
                var portEndingMatch = portEnding.Match(remoteAddressWithPort);
                if (portEndingMatch.Success)
                {
                    remoteIP = remoteAddressWithPort.Substring(0, portEndingMatch.Groups[0].Index);
                    if (IPAddress.TryParse(remoteIP, out var ipAddr))
                    {
                        if (ipAddr.IsIPv4MappedToIPv6)
                        {
                            remoteIP = ipAddr.MapToIPv4().ToString();
                        }
                    }
                    return true;
                }
            }

            remoteIP = null;
            return false;
        }


        public static string ParseRemoteIpOrReturnValue(string remoteAddressWithPort)
        {
            if (TryGetRemoteIp(remoteAddressWithPort, out var remoteIP))
            {
                return remoteIP;
            }

            return remoteAddressWithPort;
        }
    }
}
