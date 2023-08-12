using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;

namespace PNMTD.Models.Poco
{
    public class SensorPoco
    {
        public Guid Id { get; set; }

        public Guid? OlderSiblingId { get; set; }

        public Guid ParentId { get; set; }

        public SensorType Type { get; set; }

        public string Name { get; set; }

        public string? SecretToken { get; set; }

        public string? TextId { get; set; }

        public DateTime Created { get; set; }

        public bool Enabled { get; set; }

        public int Interval { get; set; }

        public int GracePeriod { get; set; }

        public string? Parameters { get; set; }

        public string? Source { get; set; }

        public HostPoco? Parent { get; set; }
    }
}
