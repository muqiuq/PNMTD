using PNMTD.Lib.Helper;
using PNMTD.Data;

namespace PNMTD.Tasks.Helpers
{
    public class TaskRunDecisionMaker
    {

        bool uplinkWasNoAvailable = false;
        DateTime? waitUntil;

        public bool DetermineIfTaskShouldRun(PnmtdDbContext dbContext)
        {
            if(!dbContext.KeyValueKeyExists(Models.Enums.KeyValueKeyEnums.UPLINK_OK))
            {
                return true;
            }
            var uplinkAvailable = dbContext.GetKeyValueByEnum<bool>(Models.Enums.KeyValueKeyEnums.UPLINK_OK);

            if (uplinkAvailable == false)
            {
                uplinkWasNoAvailable = true;
                //if no uplink is available no heartbeats will arrive at PNMT
                return false;
            }
            else if (uplinkAvailable == true && uplinkWasNoAvailable)
            {
                uplinkWasNoAvailable = false;
                //After Uplink returns wait for at least 5 minutes
                waitUntil = Clock.UtcNow.AddMinutes(5);
            }
            if (waitUntil.HasValue && waitUntil.Value > Clock.UtcNow)
            {
                return false;
            }

            return true;
        }
    }
}
