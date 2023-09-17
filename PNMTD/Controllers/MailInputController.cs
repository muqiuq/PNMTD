using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Models.Responses;

namespace PNMTD.Controllers
{
    [ApiController]
    [Route("mailinput")]
    public class MailInputController : Controller
    {

        private PnmtdDbContext Db;

        public MailInputController(PnmtdDbContext db)
        {
            this.Db = db;
        }

        [HttpGet]
        public IEnumerable<MailInputRulePoco> Get()
        {
            return Db.MailInputs.Select(n => n.ToPoco()).ToList();
        }

        [HttpGet("{id}")]
        public MailInputRulePoco Get(Guid id)
        {
            return Db.MailInputs.Where(n => n.Id == id).Select(x => x.ToPoco()).Single();
        }

        [HttpPost]
        public DefaultResponse Post([FromBody] MailInputRulePoco mailInputPoco)
        {
            var entity = mailInputPoco.ToEntity(true);
            if(entity.ExtractMessageRegex != null) entity.ExtractMessageRegex = entity.ExtractMessageRegex.Trim();
            Db.MailInputs.Add(entity);
            var changes = Db.SaveChanges();
            return new DefaultResponse()
            {
                Success = changes == 1,
                Message = "",
                Data = entity.Id
            };
        }

        [HttpPut]
        public DefaultResponse Put([FromBody] MailInputRulePoco mailInputPoco)
        {
            var entity = mailInputPoco.ToEntity(false);
            if (entity.ExtractMessageRegex != null) entity.ExtractMessageRegex = entity.ExtractMessageRegex.Trim();
            Db.MailInputs.Attach(entity);
            Db.Update<MailInputRuleEntity>(entity);
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
            Db.MailInputs.Remove(Db.MailInputs.Where(n => n.Id == id).Single());
            Db.SaveChanges();

            return new DefaultResponse() { Success = true, Message = "", Data = id };
        }
    }
}
