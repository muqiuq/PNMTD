using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PNMTD.Data;
using PNMTD.Models.Poco;
using System.Diagnostics;

namespace PNMTD.Controllers
{
    [ApiController]
    public class HostController : Controller
    {

        private readonly PnmtdDbContext db;

        public HostController(PnmtdDbContext db)
        {
            this.db = db;
        }


        [HttpGet("hosts", Name = "Hosts")]
        public IResult GetHosts()
        {
            var hosts = db.Hosts.ToList();

            var hostStates = new List<HostStatePoco>();

            foreach (var host in hosts)
            {
                var newestEvents = host.Sensors
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
                                    Name = g.Sensor.Name
                                })
                                .ToList();

                var hostState = new HostStatePoco()
                {
                    Created = host.Created,
                    Enabled = host.Enabled,
                    Id = host.Id,
                    Location = host.Location,
                    Name = host.Name,
                    Notes = host.Notes,
                    State = newestEvents.All(nw => nw.IsSuccess) ? HostState.Ok : HostState.Error,
                    Sensors = newestEvents
                };

                hostStates.Add(hostState);
            }

            return Results.Ok(hostStates);
        }

    }
}
