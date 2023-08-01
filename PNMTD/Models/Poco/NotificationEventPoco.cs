using PNMTD.Models.Db;
using PNMTD.Models.Helper;
using System.Security.Cryptography.X509Certificates;

namespace PNMTD.Models.Poco
{
    public class EventEntityPoco
    {
        public EventEntityPoco(EventEntity ee) {
            this.SensorId = ee.Sensor.Id;
            this.StatusCode = ee.Code;
            this.Message = ee.Message;
            this.Created = ee.Created;
            this.SensorName = ee.Sensor.Name;
            this.IsSuccess = ee.IsSuccess;
            this.HostName = ee.Sensor.Parent.Name;
            this.HostId = ee.Sensor.Parent.Id;
        }

        public Guid SensorId { get; private set; }
        public int StatusCode { get; private set; }
        public string? Message { get; private set; }
        public DateTime Created { get; private set; }
        public string SensorName { get; private set; }
        public bool IsSuccess { get; private set; }
        public string HostName { get; private set; }
        public Guid HostId { get; private set; }
    }
}
