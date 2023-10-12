using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;
using System.Diagnostics;

namespace PNMTD.Controllers
{
    [ApiController]
    public class HostsController : Controller
    {

        private readonly PnmtdDbContext db;

        public HostsController(PnmtdDbContext db)
        {
            this.db = db;
        }

        private HostStatePoco getHostState(HostEntity host)
        {
            var sensorsWithLastState = db.GetLastSensorStatesForHosts(host);

            var uptimeOverAll = sensorsWithLastState
                .Where(x => x.Enabled && !x.Ignore && !x.UptimePerDay.All(t => t.Value == -1))
                .SelectMany(x => x.UptimePerDay)
                .GroupBy(x => x.Key)
                .ToDictionary(x => x.Key, i => Decimal.Round(i.Average(x => x.Value), 2));

            var hostState = new HostStatePoco()
            {
                Created = host.Created,
                Enabled = host.Enabled,
                Id = host.Id,
                Location = host.Location,
                Name = host.Name,
                Notes = host.Notes,
                State = sensorsWithLastState.Where(nw => nw.Enabled && !nw.Ignore).All(nw => nw.IsSuccess) ? HostState.Ok : HostState.Error,
                Sensors = sensorsWithLastState,
                UptimePerDay = uptimeOverAll,
                UpTime30days = sensorsWithLastState.Where(x => x.Enabled && !x.Ignore).Average(x => x.UpTime30days)
            };

            return hostState;
        }

        [HttpGet("hosts", Name = "Hosts")]
        public IResult GetHosts()
        {
            var hosts = db.Hosts.ToList();

            var hostStates = new List<HostStatePoco>();

            foreach (var host in hosts)
            {
                hostStates.Add(getHostState(host));
            }

            return Results.Ok(hostStates);
        }

        [HttpGet("hosts/{id}", Name = "Host by Id")]
        public IResult GetHosts(Guid id)
        {
            var host = db.Hosts.Single(h => h.Id == id);


            var hostState = getHostState(host);

            return Results.Ok(hostState);
        }

    }
}
