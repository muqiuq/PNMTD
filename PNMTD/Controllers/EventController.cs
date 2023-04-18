using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
using PNMTD.Models.Db;

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
        public IResult GetEvent(string sensorId, int code, string? message)
        {
            var sensor = db.Sensors.First(s => s.Id == Guid.Parse(sensorId));

            var eventEntity = new EventEntity()
            {
                Code = code,
                Message = message,
                Created = DateTime.Now,
                Sensor = sensor
            };
            db.Events.Add(eventEntity);

            db.SaveChanges();

            return Results.Ok(eventEntity.Id);
        }

    }
}
