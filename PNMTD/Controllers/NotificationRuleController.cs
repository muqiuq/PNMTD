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


        [HttpDelete("{idNotificationRule}/unlink-sensor/{idSensor}")]
        public DefaultResponse DeleteNotificationRuleSensor(Guid idNotificationRule, Guid idSensor)
        {
            var notificationRuleSensors = Db.NotificationRuleSensor
                .Where(nrs => nrs.SensorId == idSensor && nrs.NotificationRuleId == idNotificationRule).ToList();

            Db.NotificationRuleSensor.RemoveRange(notificationRuleSensors);
            Db.SaveChanges();

            return new DefaultResponse() { Success = true, Message = "", Data = null };
        }

        [HttpPost("{idNotificationRule}/link-sensor/{idSensor}")]
        public DefaultResponse CreateNotificationRuleSensor(Guid idNotificationRule, Guid idSensor)
        {
            var sensor = Db.Sensors.Single(s => s.Id == idSensor);
            var notificationRule = Db.NotificationRules.Single(s => s.Id == idNotificationRule);

            if(Db.NotificationRuleSensor
                .Where(nrs => nrs.SensorId == idSensor && nrs.NotificationRuleId == idNotificationRule).Any())
            {
                return new DefaultResponse() { Success = false, Message = "Already exists", Data = null };
            }

            var notificationRuleSensor = new NotificationRuleSensorEntity()
            {
                Id = Guid.NewGuid(),
                SensorId = sensor.Id,
                NotificationRuleId = notificationRule.Id,
            };

            Db.NotificationRuleSensor.Add(notificationRuleSensor);
            int numOfEntriesWritten = Db.SaveChanges();

            return new DefaultResponse() { Success = numOfEntriesWritten == 1, Message = "", Data = notificationRuleSensor.Id };
        }

        [HttpGet("forsensor/{id}")]
        public IEnumerable<NotificationRulePoco> GetForSensor(Guid id)
        {
            var rv =  Db.NotificationRules
                .Where(n => n.SubscribedSensors.Where(x => x.NotificationRuleId == n.Id && x.SensorId == id).Any())
                .Select(n => n.ToPoco()).ToList();
            return rv;
        }

        [HttpGet]
        public IEnumerable<NotificationRulePoco> Get()
        {
            return Db.NotificationRules.Select(n => n.ToPoco()).ToList();
        }

        // GET api/<NotificationController>/5
        [HttpGet("{id}")]
        public NotificationRulePoco Get(Guid id)
        {
            return Db.NotificationRules.Where(n => n.Id == id).Select(x => x.ToPoco()).Single();
        }

        // POST api/<NotificationController>
        [HttpPost]
        public DefaultResponse Post([FromBody] NotificationRulePoco notificationPoco)
        {
            var notificationEntity = Db.CreateNewNotificationRule(notificationPoco);
            Db.SaveChanges();
            return new DefaultResponse() { Success = true, Message = "", Data = notificationEntity.Id };
        }

        // PUT api/<NotificationController>
        [HttpPut]
        public DefaultResponse Put([FromBody] NotificationRulePoco notificationPoco)
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
