using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;

namespace PNMTD.Controllers
{
    [ApiController]
    public class MiscController : ControllerBase
    {
        private PnmtdDbContext db;

        public MiscController(PnmtdDbContext db)
        {
            this.db = db;
        }

        [HttpGet("state", Name = "Get Uplink State")]
        public object GetEvents()
        {
            if (!db.KeyValueKeyExists(Models.Enums.KeyValueKeyEnums.UPLINK_OK))
            {
                return new UplinkStatePoco()
                {
                    IsUp = true,
                    LastRun = "",
                    LastSuccessfullRun = ""
                };
            }

            var uplinkState = db.GetKeyValueByEnum<bool>(Models.Enums.KeyValueKeyEnums.UPLINK_OK);
            var lastRun = db.GetKeyValueByEnum<string>(Models.Enums.KeyValueKeyEnums.LAST_UPLINK_CHECK);
            var successfullRun = db.GetKeyValueByEnum<string>(Models.Enums.KeyValueKeyEnums.LAST_UPLINK_CHECK_SUCCESSFULL);


            return new UplinkStatePoco()
            {
                IsUp = uplinkState,
                LastRun = lastRun,
                LastSuccessfullRun = successfullRun
            };
        }
    }
}
