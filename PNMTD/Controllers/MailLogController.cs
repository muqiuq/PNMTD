using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Models.Responses;

namespace PNMTD.Controllers
{
    [ApiController]
    [Route("maillog")]
    public class MailLogController : Controller
    {
        private PnmtdDbContext Db;

        public MailLogController(PnmtdDbContext db)
        {
            this.Db = db;
        }

        [HttpGet]
        public IEnumerable<MailLogPoco> Get()
        {
            return Db.MailLogs.Take(1000).OrderByDescending(ml => ml.Created).Select(n => n.ToPoco()).ToList();
        }

        [HttpGet("{id}")]
        public MailLogPoco Get(Guid id)
        {
            return Db.MailLogs.Where(n => n.Id == id).Select(x => x.ToPoco()).Single();
        }

        [HttpPost]
        public DefaultResponse Post([FromBody] MailLogPoco mailLogPoco)
        {
            var entity = mailLogPoco.ToEntity(true);
            Db.MailLogs.Add(entity);
            var changes = Db.SaveChanges();
            return new DefaultResponse()
            {
                Success = changes == 1,
                Message = "",
                Data = entity.Id
            };
        }

        [HttpPut]
        public DefaultResponse Put([FromBody] MailLogPoco mailLogPoco)
        {
            var entity = mailLogPoco.ToEntity(false);
            Db.MailLogs.Attach(entity);
            Db.Update<MailLogEntity>(entity);
            var changes = Db.SaveChanges();
            return new DefaultResponse()
            {
                Success = changes == 1,
                Message = "",
                Data = entity.Id
            };
        }

        [HttpDelete("{id}")]
        public DefaultResponse Delete(Guid id)
        {
            Db.MailLogs.Remove(Db.MailLogs.Where(n => n.Id == id).Single());
            Db.SaveChanges();

            return new DefaultResponse() { Success = true, Message = "", Data = id };
        }
    }
}
