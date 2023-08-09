namespace PNMTD.Lib.Models.Poco
{
    public class EventEntityPoco
    {
        public const int END_OF_SUCCESS_CODES = 399;
        public Guid SensorId { get; set; }
        public int StatusCode { get; set; }
        public string? Message { get; set; }
        public string Source { get; set; }
        public DateTime Created { get; set; }
        public string SensorName { get; set; }
        public bool IsSuccess { get; set; }
        public string HostName { get; set; }
        public Guid HostId { get; set; }
    }
}
