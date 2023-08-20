using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models.Poco
{
    public class MailLogPoco
    {
        public Guid Id { get; set; }

        public bool Processed { get; set; }

        public string ProcessLog { get; set; }

        public Guid? ProcessedById { get; set; }

        public DateTime Created { get; set; }

        public DateTime MessageDate { get; set; }

        public string From { get; set; }

        public string To { get; set; }

        public string Subject { get; set; }
        public string Content { get; set; }
    }
}
