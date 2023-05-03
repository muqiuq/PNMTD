using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Models.Responses;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace PNMTD.Controllers
{
    [Route("notifications")]
    [ApiController]
    public class NotificationController : ControllerBase
    {
        private PnmtdDbContext Db;

        public NotificationController(PnmtdDbContext db)
        {
            this.Db = db;
        }

        // GET: api/<NotificationController>
        [HttpGet]
        public IEnumerable<NotificationPoco> Get()
        {
            return Db.Notifications.Select(n => n.ToPoco()).ToList();
        }

        // GET api/<NotificationController>/5
        [HttpGet("{id}")]
        public NotificationPoco Get(Guid id)
        {
            return Db.Notifications.Where(n => n.Id == id).Select(x => x.ToPoco()).Single();
        }

        // POST api/<NotificationController>
        [HttpPost]
        public DefaultResponse Post([FromBody] NotificationPoco notificationPoco)
        {
            notificationPoco.Id = Guid.NewGuid();
            Db.Notifications.Add(notificationPoco.ToEntity(Db));
            Db.SaveChanges();
            return new DefaultResponse() { Success = true, Message = "", Data = notificationPoco.Id };
        }

        // PUT api/<NotificationController>/5
        [HttpPut]
        public DefaultResponse Put([FromBody] NotificationPoco notificationPoco)
        {
            var notificationEntity = notificationPoco.ToEntity(Db);
            Db.Notifications.Attach(notificationEntity);
            Db.Update<NotificationEntity>(notificationEntity);
            Db.SaveChanges();
            return new DefaultResponse() { Success = true, Message = "", Data = notificationPoco.Id };
        }

        // DELETE api/<NotificationController>/5
        [HttpDelete("{id}")]
        public DefaultResponse Delete(Guid id)
        {
            Db.Notifications.Remove(Db.Notifications.Where(n => n.Id == id).Single());
            Db.SaveChanges();

            return new DefaultResponse() { Success = true, Message = "", Data = id };
        }
    }
}
