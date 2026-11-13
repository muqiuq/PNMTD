using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models
{
    public static class PNMTStatusCodes
    {
        public const int OK = 200;
        public const int ERROR = 500;

        public const int HEARTBEAT_MISSING = 460;
        public const int PING_FAILED = 470;
        public const int PING_SUCCESSFULL = 270;

        public const int DNS_OK = 211;
        public const int DNS_FAILED = 411;

        public const int VALUECHECK_OK = 275;
        public const int VALUECHECK_FAILED = 475;

        public const int ONE_WITHIN_TIMESPAN_OK = 280;
        public const int ONE_WITHIN_TIMESPAN_FAILED = 480;

        public const int ALL_WITHIN_TIMESPAN_OK = 281;
        public const int ALL_WITHIN_TIMESPAN_FAILED = 481;

        /// <summary>UDP nodes (docs/udp-iot-plan.md §4.2): alive, restarted, data.</summary>
        public const int UDP_ALIVE = 230;
        public const int UDP_BOOT = 231;
        public const int UDP_DATA = 232;

        /// <summary>MikroTik routers (docs/mikrotik-plan.md §5.3): the current condition, and changes as single events.</summary>
        public const int MIKROTIK_OK = 240;
        public const int MIKROTIK_RESTARTED = 241;
        public const int MIKROTIK_UPDATED = 242;
        public const int MIKROTIK_CONFIG_CHANGED = 243;
        public const int MIKROTIK_UPDATE_AVAILABLE = 244;
        public const int MIKROTIK_UNREACHABLE = 440;
        public const int MIKROTIK_ACCESS_DENIED = 441;
        public const int MIKROTIK_BACKUP_FAILED = 442;
        public const int MIKROTIK_UPDATE_FAILED = 443;

        public static readonly int[] ONE_OR_ALL_WITHIN_CODES = new int[]{ ONE_WITHIN_TIMESPAN_FAILED, ONE_WITHIN_TIMESPAN_OK, ALL_WITHIN_TIMESPAN_OK, ALL_WITHIN_TIMESPAN_FAILED };
    }
}
