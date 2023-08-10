using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using System.Text.RegularExpressions;

namespace PNMTD.Helper
{
    public static class SensorTypeActionHelper
    {

        public static bool IsContentAMatch(string content, string parameters)
        {
            Regex rx = new Regex(parameters, RegexOptions.Compiled);

            MatchCollection matches = rx.Matches(content);

            return matches.Count > 0;
        }

        public static void AdjustCodeForValueCheckSensorTypeInEvent(EventEntity eventEntity, SensorEntity sensor)
        {
            if (sensor.Type != SensorType.VALUECHECK) return;

            var checkResult = IsContentAMatch(eventEntity.Message, sensor.Parameters);

            eventEntity.Code = checkResult ? PNMTStatusCodes.VALUECHECK_OK : PNMTStatusCodes.VALUECHECK_FAILED;
        }

    }
}
