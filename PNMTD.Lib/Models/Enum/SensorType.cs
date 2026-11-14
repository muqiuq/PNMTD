using System;

namespace PNMTD.Lib.Models.Enum
{
    public enum SensorType
    {
        SIMPLE = 10,
        DNS = 11,
        HEARTBEAT = 20,
        HEARTBEAT_VALUECHECK = 25,
        PING = 30,
        VALUECHECK = 40,
        ENCAPSULADED = 50,
        ONE_WITHIN_TIMESPAN = 60,
        ALL_WITHIN_TIMESPAN = 61,
        /// <summary>A device reporting over PNMT-UDP (docs/udp-iot-plan.md §4.1), with heartbeat semantics.</summary>
        UDP_NODE = 70,
        /// <summary>A MikroTik router that PNMTD polls over SSH, or watches through its tunnel (docs/mikrotik-plan.md §9.1).</summary>
        MIKROTIK = 80,
        /// <summary>One of PNMT's own conditions (docs/ai-operations-plan.md §4): the uplink, the AI, the RouterOS advisories. PNMT writes its events.</summary>
        SYSTEM = 90,
        /// <summary>A Windows server or PC running the PNMT sensor service (docs/windows-sensor-plan.md §8.1); created by its join, with heartbeat semantics.</summary>
        WINDOWS = 100,
        /// <summary>A diagnostics set assigned to a server's agent (docs/windows-sensor-plan.md §5.2); created by the assignment.</summary>
        DIAGNOSTICS = 101,
        /// <summary>A Linux server running the PNMT agent for Linux (docs/linux-sensor-plan.md §9.1); created by its join, with heartbeat semantics.</summary>
        LINUX = 102
    }
}

