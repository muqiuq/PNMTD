using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;

namespace PNMTD.Data
{
    public static class DbSetHostEntityExtensions
    {

        public static decimal CalculateUpdateFor(this PnmtdDbContext db, SensorEntity sensorEntity, TimeSpan spanFromNow)
        {
            var nowMinus30Days = DateTime.Now - spanFromNow;
            var events = db.Events
                .Where(e => e.SensorId == sensorEntity.Id && e.Created > nowMinus30Days)
                .OrderBy(e => e.Created)
                .ToList();
            DateTime? lastDate = null;
            bool lastState = false;

            TimeSpan online = TimeSpan.Zero;
            TimeSpan offline = TimeSpan.Zero;

            events.Add(new EventEntity()
            {
                Created = DateTime.Now,
                Code = 0,
                Message = ""
            });

            foreach (var e in events)
            {
                if (lastDate == null)
                {
                    lastDate = e.Created;
                    lastState = e.IsSuccess;
                }
                else
                {
                    var diff = e.Created - lastDate;
                    if (lastState)
                    {
                        online += diff.Value;
                    }
                    else
                    {
                        offline += diff.Value;
                    }

                    lastDate = e.Created;
                    lastState = e.IsSuccess;
                }
            }
            var totalTimeSpan = (decimal)offline.TotalSeconds + (decimal)online.TotalSeconds;
            if (totalTimeSpan == 0) return 100;
            return ((decimal)online.TotalSeconds) / (totalTimeSpan) * 100 ;
        }

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
                    Type = g.Sensor.Type,
                    Enabled = g.Sensor.Enabled,
                    UpTime30days = db.CalculateUpdateFor(g.Sensor, TimeSpan.FromDays(30)),
                    UpTime24h = db.CalculateUpdateFor(g.Sensor, TimeSpan.FromHours(24)),
                    Ignore = g.Sensor.Ignore
                })
                .ToList();
        }

    }
}
