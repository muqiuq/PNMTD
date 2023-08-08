using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;

namespace PNMTD.Data
{
    public static class DbSetHostEntityExtensions
    {

        public static List<LastSensorStatePoco> GetLastSensorStatesForHosts(this PnmtdDbContext db, HostEntity hostEntity)
        {
            return hostEntity.Sensors
                .GroupJoin(
                    db.Events, sensor => sensor, e => e.Sensor,
                    (sensor, events) => new { Sensor = sensor, Events = events }
                )
                .SelectMany(
                    se => se.Events.DefaultIfEmpty(),
                    (se, e) => new { Sensor = se.Sensor, Event = e }
                )
                .GroupBy(se => se.Sensor)
                .Select(g => new
                {
                    Sensor = g.Key,
                    Event = g.OrderByDescending(se => se.Event?.Created).FirstOrDefault()
                })
                .ToList()
                .Select(g => new LastSensorStatePoco()
                {
                    Id = g.Sensor.Id,
                    IsSuccess = g.Event?.Event?.IsSuccess ?? true,
                    LastCode = g.Event?.Event?.Code ?? -1,
                    LastMessage = g.Event?.Event?.Message,
                    Since = g.Event?.Event?.Created,
                    Name = g.Sensor.Name,
                    Type = g.Sensor.Type
                })
                .ToList();
        }

    }
}
