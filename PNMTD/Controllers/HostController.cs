using Microsoft.AspNetCore.Authorization;
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
        [Authorize]
        [HttpGet("hosts", Name = "Hosts")]
        public IResult GetHosts()
        {
            var hosts = db.Hosts.ToList();

            var hostStates = new List<HostStatePoco>();

            foreach (var host in hosts)
            {
                var sensorsWithLastState = db.GetLastSensorStatesForHosts(host);

                var hostState = new HostStatePoco()
                {
                    Created = host.Created,
                    Enabled = host.Enabled,
                    Id = host.Id,
                    Location = host.Location,
                    Name = host.Name,
                    Notes = host.Notes,
                    State = sensorsWithLastState.All(nw => nw.IsSuccess) ? HostState.Ok : HostState.Error,
                    Sensors = sensorsWithLastState
                };

                hostStates.Add(hostState);
            }

            return Results.Ok(hostStates);
        }

    }
}
