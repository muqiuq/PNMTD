using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Responses;

namespace PNMTD.Controllers
{
    [ApiController]
    public class DnsZoneController : ControllerBase
    {

        private readonly PnmtdDbContext Db;

        public DnsZoneController(PnmtdDbContext db)
        {
            this.Db = db;
        }

        [HttpGet]
        public IEnumerable<HostPoco> Get()
        {
            return Db.DnsZoneEntries.Include(d => d.DnsZone).Select(d => d.ToPoco()).ToList();
        }

        [HttpGet("{id}")]
        public IActionResult Get(Guid id)
        {
            var host = Db.Hosts.Where(h => h.Id == id);
            if (host.Any())
            {
                return Ok(host.Single().ToPoco());
            }
            else
            {
                return NotFound();
            }
        }

        [HttpPost]
        public DefaultResponse Post([FromBody] HostPoco hostPoco)
        {
            var entity = hostPoco.ToEntity(true);
            var change = Db.Hosts.Add(entity);
            Db.SaveChanges();
            return new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Unchanged,
                Message = "",
                Data = entity.Id
            };
        }

        [HttpPut]
        public DefaultResponse Put([FromBody] HostPoco hostPoco)
        {
            var entity = hostPoco.ToEntity(false);
            Db.Hosts.Attach(entity);
            var change = Db.Update<HostEntity>(entity);
            Db.SaveChanges();
            return new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Unchanged,
                Message = "",
                Data = entity.Id
            };
        }

        [HttpDelete("{id}")]
        public DefaultResponse Delete(Guid id)
        {
            var host = Db.Hosts.Where(n => n.Id == id).Single();
            host.Sensors.ForEach(s => Db.Sensors.Remove(s));
            var change = Db.Hosts.Remove(Db.Hosts.Where(n => n.Id == id).Single());
            Db.SaveChanges();

            return new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Detached,
                Message = "",
                Data = id
            };
        }
    }
}
