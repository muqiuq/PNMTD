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
        
        public const int VALUECHECK_OK = 275;
        public const int VALUECHECK_FAILED = 475;

    }
}
