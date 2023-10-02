using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco.Extensions;
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
        public IEnumerable<DnsZonePoco> Get()
        {
            return Db.DnsZones.Include(d => d.DnsZoneEntries).Select(d => d.ToPoco()).ToList();
        }

        [HttpGet("{id}")]
        public IActionResult Get(Guid id)
        {
            var host = Db.DnsZones.Where(h => h.Id == id);
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
        public DefaultResponse Post([FromBody] DnsZonePoco dnsZonePoco)
        {
            var entity = dnsZonePoco.ToEntity();
            var change = Db.DnsZones.Add(entity);
            Db.SaveChanges();
            return new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Unchanged,
                Message = "",
                Data = entity.Id
            };
        }

        [HttpPut]
        public DefaultResponse Put([FromBody] DnsZonePoco dnsZonePoco)
        {
            var entity = dnsZonePoco.ToEntity();
            Db.DnsZones.Attach(entity);
            var change = Db.Update<DnsZoneEntity>(entity);
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
            var dnsZone = Db.DnsZones
                .Include(d => d.DnsZoneEntries)
                .ThenInclude(d => d.DnsZoneLogEntries)
                .Where(n => n.Id == id).Single();
            dnsZone.DnsZoneEntries.ForEach(s => {
                s.DnsZoneLogEntries.ForEach(e => { Db.DnsZoneLogEntries.Remove(e);  });
                Db.DnsZoneEntries.Remove(s);
                });
            var change = Db.DnsZones.Remove(dnsZone);
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
