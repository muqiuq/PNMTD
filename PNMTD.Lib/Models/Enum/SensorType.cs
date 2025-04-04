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
        ALL_WITHIN_TIMESPAN = 61
    }
}

