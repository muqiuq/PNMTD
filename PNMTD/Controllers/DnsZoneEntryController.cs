using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Models.Responses;

namespace PNMTD.Controllers
{
    [Route("dnszoneentry")]
    [ApiController]
    public class DnsZoneEntryController : ControllerBase
    {

        private readonly PnmtdDbContext Db;

        public DnsZoneEntryController(PnmtdDbContext db)
        {
            this.Db = db;
        }

        [HttpGet("{id}")]
        public IActionResult Get(Guid id)
        {
            var dnsZoneEntry = Db.DnsZoneEntries
                .Where(h => h.Id == id);

            if (dnsZoneEntry.Any())
            {
                var DnsZoneEntryPoco = dnsZoneEntry.Single().ToPoco();
                return Ok(DnsZoneEntryPoco);
            }
            else
            {
                return NotFound();
            }
        }

        [HttpPut]
        public IActionResult Put([FromBody] DnsZoneEntryPoco dnsZoneEntryPoco)
        {
            var entityFormDb = Db.DnsZoneEntries.SingleOrDefault(d => d.Id == dnsZoneEntryPoco.Id);
            if (entityFormDb == null) return NotFound();

            entityFormDb.Ignore = dnsZoneEntryPoco.Ignore;

            var change = Db.Update<DnsZoneEntryEntity>(entityFormDb);
            Db.SaveChanges();

            return Ok(new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Unchanged,
                Message = "",
                Data = entityFormDb.Id
            });
        }
    }
}
