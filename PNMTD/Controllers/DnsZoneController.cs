using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Migrations;
using PNMTD.Models.Db;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Models.Responses;

namespace PNMTD.Controllers
{
    [Route("dnszone")]
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
            return Db.DnsZones
                .Include(d => d.Host)
                .Include(d => d.DnsZoneEntries).Select(d => d.ToPoco()).ToList();
        }

        [HttpGet("logs")]
        public IActionResult GetAllLogs()
        {
            var dnsLogEntries = Db.DnsZoneLogEntries.OrderByDescending(d => d.Created).Take(30).Select(d => d.ToPoco()).ToList();
            return Ok(dnsLogEntries);
        }

        [HttpGet("logs/{id}")]
        public IActionResult GetLogs(Guid id)
        {
            var dnsZone = Db.DnsZones
                .Where(h => h.Id == id).SingleOrDefault();

            if (dnsZone != null)
            {
                var dnsLogEntries = Db.DnsZoneLogEntries.Where(d => d.DnsZoneId == dnsZone.Id).OrderByDescending(d => d.Created).Select(d => d.ToPoco()).ToList();
                return Ok(dnsLogEntries);
            }
            else
            {
                return NotFound();
            }
        }


        [HttpGet("{id}")]
        public IActionResult Get(Guid id)
        {
            var dnsZone = Db.DnsZones
                .Include(d => d.Host)
                .Include(d => d.DnsZoneEntries)
                .Where(h => h.Id == id);

            if (dnsZone.Any())
            {
                var dnsZonePoco = dnsZone.Single().ToPoco();
                return Ok(dnsZonePoco);
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
            entity.Id = Guid.NewGuid();
            entity.RequiresProcessing = true;
            entity.Interval = 600;
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
        public IActionResult Put([FromBody] DnsZonePoco dnsZonePoco)
        {
            var entityFormDb = Db.DnsZones.SingleOrDefault(d => d.Id == dnsZonePoco.Id);
            if (entityFormDb == null) return NotFound();

            entityFormDb.Enabled = dnsZonePoco.Enabled;
            entityFormDb.Interval = dnsZonePoco.Interval;
            entityFormDb.ForceUpdate = dnsZonePoco.ForceUpdate;
            entityFormDb.HostId = dnsZonePoco.HostId;
            if(entityFormDb.ZoneFileContent != dnsZonePoco.ZoneFileContent)
            {
                entityFormDb.RequiresProcessing = true;
                entityFormDb.ZoneFileContent = dnsZonePoco.ZoneFileContent;
            }
            
            var change = Db.Update<DnsZoneEntity>(entityFormDb);
            Db.SaveChanges();
            return Ok(new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Unchanged,
                Message = "",
                Data = entityFormDb.Id
            });
        }

        [HttpDelete("{id}")]
        public IActionResult Delete(Guid id)
        {
            var dnsZone = Db.DnsZones
                .Include(d => d.DnsZoneEntries)
                .Where(n => n.Id == id).SingleOrDefault();
            if(dnsZone == null) return NotFound();

            Db.DnsZoneLogEntries.RemoveRange(Db.DnsZoneLogEntries.Where(d => d.DnsZoneId == dnsZone.Id).ToList());

            dnsZone.Enabled = false;
            dnsZone.RequiresProcessing = false;

            Db.SaveChanges();

            // Optimistic wait for DnsCheckTask to finish
            Thread.Sleep(2000);

            dnsZone.DnsZoneEntries.ForEach(s => {
                Db.DnsZoneEntries.Remove(s);
                });
            var change = Db.DnsZones.Remove(dnsZone);
            Db.SaveChanges();

            return Ok(new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Detached,
                Message = "",
                Data = id
            });
        }
    }
}
