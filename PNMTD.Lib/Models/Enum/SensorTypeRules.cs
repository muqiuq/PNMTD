using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models.Enum
{
    public static class SensorTypeRules
    {
        public static bool IsEditable(this SensorType sensorType)
        {
            if (sensorType == SensorType.DNS) return false;
            return true;
        }

        public static bool HideParameters(this SensorType sensorType)
        {
            if (sensorType == SensorType.DNS) return true;
            return false;
        }

        public static bool HideTextId(this SensorType sensorType)
        {
            if (sensorType == SensorType.DNS) return true;
            return false;
        }

        public static bool UsesIntervalAndGracePeriod(this SensorType sensorType)
        {
            if (sensorType == SensorType.DNS) return false;
            return true;
        }
    }
}
