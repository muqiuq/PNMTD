using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using System.Runtime.CompilerServices;

namespace PNMTD.Models.Poco.Extensions
{
    public static class EventEntityPocoExtensions
    {

        public static EventEntityPoco ToPoco(this EventEntity entity)
        {
            return new EventEntityPoco()
            {
                Id = entity.Id,
                SensorId = entity.Sensor.Id,
                StatusCode = entity.Code,
                Message = entity.Message,
                Created = entity.Created,
                SensorName = entity.Sensor.Name,
                IsSuccess = entity.IsSuccess,
                HostName = entity.Sensor.Name,
                HostId = entity.Sensor.ParentId,
                Source = entity.Source
            };
        }

    }
}
