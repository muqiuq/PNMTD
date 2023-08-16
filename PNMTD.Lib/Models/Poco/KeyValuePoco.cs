using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models.Poco
{
    public class KeyValuePoco
    {
        public Guid Id { get; set; }

        public bool Enabled { get; set; }

        public string Note { get; set; }

        public string Key { get; set; }

        public string Value { get; set; }

        public bool IsReadOnly { get; set; }
    }
}
