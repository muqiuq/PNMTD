using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Logic
{
    public static class NotificationRuleTriggerLogic
    {

        public static bool Eval(NotificationRuleType type, int oldStatusCode, int newStatusCode)
        {
            if (type == NotificationRuleType.ALWAYS) return true;
            if(newStatusCode > EventEntityPoco.END_OF_SUCCESS_CODES
                && type == NotificationRuleType.ONLY_FAILURES) return true;

            if(oldStatusCode != newStatusCode)
            {
                if (type == NotificationRuleType.ONLY_ON_CHANGE) return true;
                if (newStatusCode > EventEntityPoco.END_OF_SUCCESS_CODES
                && (type == NotificationRuleType.ONLY_FAILURES_ON_CHANGE 
                || type == NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK)) return true;

                if (newStatusCode <= EventEntityPoco.END_OF_SUCCESS_CODES &&
                    oldStatusCode > EventEntityPoco.END_OF_SUCCESS_CODES &&
                    type == NotificationRuleType.ONLY_FAILURES_ON_CHANGE_AND_BACK_TO_OK)
                {
                    return true;
                }
            }
            return false;
        }

    }
}
