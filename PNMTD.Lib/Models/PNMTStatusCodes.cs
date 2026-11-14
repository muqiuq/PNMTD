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

        /// <summary>PNMT's own conditions, on its system sensors (docs/ai-operations-plan.md §4).</summary>
        public const int SYSTEM_OK = 220;
        public const int AI_FINDING_INFO = 221;
        public const int UPLINK_DOWN = 420;
        public const int AI_FAILING = 421;
        public const int ROUTEROS_SECURITY_UPDATE = 422;
        public const int AI_FINDING_CRITICAL = 423;
        public const int AI_FINDING_WARNING = 424;
        public const int AI_BUDGET_80 = 425;
        public const int AI_BUDGET_90 = 426;
        public const int AI_BUDGET_REACHED = 427;

        /// <summary>Windows servers (docs/windows-sensor-plan.md §8.2): the heartbeat, a restart, a notable change, admin logins; the agent's problems, critical entries, login failures.</summary>
        public const int WINDOWS_OK = 250;
        public const int WINDOWS_RESTARTED = 251;
        public const int WINDOWS_CHANGED = 253;
        public const int WINDOWS_ADMIN_LOGIN = 254;
        public const int WINDOWS_AGENT_PROBLEM = 451;
        public const int WINDOWS_CRITICAL_EVENT = 453;
        public const int WINDOWS_LOGIN_FAILURES = 454;

        /// <summary>
        /// Linux servers (docs/linux-sensor-plan.md §9.2): the heartbeat, a restart, a notable change, admin logins; the agent's
        /// problems, pending security updates, critical journal entries, login failures, and keys that aren't registered as active.
        /// </summary>
        public const int LINUX_OK = 260;
        public const int LINUX_RESTARTED = 261;
        public const int LINUX_CHANGED = 263;
        public const int LINUX_ADMIN_LOGIN = 264;
        public const int LINUX_AGENT_PROBLEM = 461;
        public const int LINUX_SECURITY_UPDATES = 462;
        public const int LINUX_CRITICAL_ENTRY = 463;
        public const int LINUX_LOGIN_FAILURES = 464;
        public const int LINUX_KEY_ADDED = 465;

        /// <summary>Diagnostics (docs/windows-sensor-plan.md §5.4): every check passed, or at least one failed.</summary>
        public const int DIAGNOSTICS_OK = 252;
        public const int DIAGNOSTICS_FAILED = 452;

        public static readonly int[] ONE_OR_ALL_WITHIN_CODES = new int[]{ ONE_WITHIN_TIMESPAN_FAILED, ONE_WITHIN_TIMESPAN_OK, ALL_WITHIN_TIMESPAN_OK, ALL_WITHIN_TIMESPAN_FAILED };
    }
}
