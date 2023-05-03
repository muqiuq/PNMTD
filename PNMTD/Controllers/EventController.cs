using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
using PNMTD.Models.Db;
using PNMTD.Models.Responses;
using System.Text;

namespace PNMTD.Controllers
{
    [ApiController]
    public class EventController : ControllerBase
    {
        private readonly PnmtdDbContext db;

        public EventController(PnmtdDbContext db) {
            this.db = db;
        }

        [HttpGet("event/{sensorId}/{code}/{message?}", Name = "Submit Event")]
        public object GetEvent(string sensorId, int code, string? message)
        {
            var sensor = db.Sensors.First(s => s.Id == Guid.Parse(sensorId));

            // A $ indicated that the contect of the message is encoded with Base64
            if(message.StartsWith("$"))
            {
                message = Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(1)));
            }

            var eventEntity = new EventEntity()
            {
                Code = code,
                Message = message,
                Created = DateTime.Now,
                Sensor = sensor
            };
            db.Events.Add(eventEntity);

            db.Events.CleanUpEntitiesForHost(sensor.Id);

            db.SaveChanges();

            return new DefaultResponse() { Success = true, Data = eventEntity.Id };
        }

    }
}
