using PNMTD.Models.Db;

namespace PNMTD.Models.Poco
{
    public class HostStatePoco
    {

        public Guid Id { get; set; }

        public string Name { get; set; }

        public string? Location { get; set; }

        public string? Notes { get; set; }

        public DateTime Created { get; set; }

        public bool Enabled { get; set; }

        public HostState State { get; set; }

        public List<LastSensorStatePoco> Sensors { get; set; } = new List<LastSensorStatePoco>();

    }
}
