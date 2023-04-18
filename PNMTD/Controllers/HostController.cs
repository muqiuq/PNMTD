using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using PNMTD.Data;
using PNMTD.Models.Poco;

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
                var newestEvents = db.Events.Where(e => host.Sensors.Contains(e.Sensor))
                .GroupBy(e => e.Sensor)
                .Select(g => new { Sensor = g.Key, Event = g.OrderByDescending(e => e.Created).First() })
                .ToList()
                .Select(g => new LastSensorStatePoco()
                {
                    Id = g.Sensor.Id,
                    IsSuccess = g.Event.IsSuccess,
                    LastCode = g.Event.Code,
                    LastMessage = g.Event.Message,
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
