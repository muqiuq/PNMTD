using PNMTD.Data;

namespace PNMTD.Services.Helpers
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
                waitUntil = DateTime.Now.AddMinutes(5);
            }
            if (waitUntil.HasValue && waitUntil.Value > DateTime.Now)
            {
                return false;
            }

            return true;
        }
    }
}
