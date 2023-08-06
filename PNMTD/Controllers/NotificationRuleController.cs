using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Lib.Models.Poco.Extensions;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Models.Responses;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace PNMTD.Controllers
{
    [Route("notificationrule")]
    [ApiController]
    public class NotificationRuleController : ControllerBase
    {
        private PnmtdDbContext Db;

        public NotificationRuleController(PnmtdDbContext db)
        {
            this.Db = db;
        }

        // GET: api/<NotificationController>
        [HttpGet]
        public IEnumerable<NotificationPoco> Get()
        {
            return Db.NotificationRules.Select(n => n.ToPoco()).ToList();
        }

        // GET api/<NotificationController>/5
        [HttpGet("{id}")]
        public NotificationPoco Get(Guid id)
        {
            return Db.NotificationRules.Where(n => n.Id == id).Select(x => x.ToPoco()).Single();
        }

        // POST api/<NotificationController>
        [HttpPost]
        public DefaultResponse Post([FromBody] NotificationPoco notificationPoco)
        {
            var notificationEntity = Db.CreateNewNotificationRule(notificationPoco);
            Db.SaveChanges();
            return new DefaultResponse() { Success = true, Message = "", Data = notificationEntity.Id };
        }

        // PUT api/<NotificationController>/5
        [HttpPut]
        public DefaultResponse Put([FromBody] NotificationPoco notificationPoco)
        {
            var notificationEntity = notificationPoco.ToEntity(isNew: false);
            Db.NotificationRules.Attach(notificationEntity);
            Db.Update<NotificationRuleEntity>(notificationEntity);
            Db.SaveChanges();
            return new DefaultResponse() { Success = true, Message = "", Data = notificationPoco.Id };
        }

        // DELETE api/<NotificationController>/5
        [HttpDelete("{id}")]
        public DefaultResponse Delete(Guid id)
        {
            Db.NotificationRules.Remove(Db.NotificationRules.Where(n => n.Id == id).Single());
            Db.SaveChanges();

            return new DefaultResponse() { Success = true, Message = "", Data = id };
        }
    }
}
