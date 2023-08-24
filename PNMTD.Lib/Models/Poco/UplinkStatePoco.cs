using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models.Poco
{
    public class UplinkStatePoco
    {
        public bool IsUp { get; set; }

        public string? LastRun { get; set; }

        public string? LastSuccessfullRun { get; set; }

    }
}
