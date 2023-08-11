using PNMTD.Lib.Models.Enum;

namespace PNMTD.Lib.Models.Poco
{
    public class LastSensorStatePoco
    {

        public Guid Id { get; set; }

        public string Name { get; set; }

        public bool Enabled { get; set; }

        public SensorType Type { get; set; }

        public int LastCode { get; set; }

        public string? LastMessage { get; set; }

        public decimal? UpTime30days { get; set; }

        public decimal? UpTime24h { get; set; }

        public bool IsSuccess { get; set; }

        public DateTime? Since { get; set; }

    }
}
