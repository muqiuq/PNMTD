using PNMTD.Models.Poco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PNMTD.Lib.Models.Poco
{
    public class MailInputRulePoco
    {
        public Guid Id { get; set; }

        public bool Enabled { get; set; }

        public string Name { get; set; }

        public string? BodyTest { get; set; }

        public string? SubjectTest { get; set; }

        public string? ExtractMessageRegex { get; set; }

        public string? FromTest { get; set; }

        public int? OkCode { get; set; }

        public string? OkTest { get; set; }

        public int? FailCode { get; set; }

        public string? FailTest { get; set; }

        public int DefaultCode { get; set; }

        public Guid? SensorOutputId { get; set; }
    }
}
