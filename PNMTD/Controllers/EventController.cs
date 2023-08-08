using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using PNMTD.Data;
using PNMTD.Exceptions;
using PNMTD.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Requests;
using PNMTD.Models.Responses;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PNMTD.Controllers
{
    [ApiController]
    public class EventController : ControllerBase
    {
        private readonly PnmtdDbContext db;

        public EventController(PnmtdDbContext db) {
            this.db = db;
        }

        [AllowAnonymous]
        [HttpGet("event/{sensorId}/{code}/{message?}", Name = "Submit Event (Get)")]
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

        [AllowAnonymous]
        [HttpPost("event/{sensorId}/{code}", Name = "Submit Event (Post)")]
        public async Task<object> PostEvent(string sensorId, int code)
        {
            var message = (await Request.GetRequestBody()).Trim();
            var sensor = db.Sensors.First(s => s.Id == Guid.Parse(sensorId));

            // A $ indicated that the contect of the message is encoded with Base64
            if (message.StartsWith("$"))
            {
                message = Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(1)));
            }

            EventEntity eventEntity = null;

            if (sensor.Type == SensorType.ENCAPSULADED)
            {
                var encapsulatedEvents = JsonSerializer.Deserialize<IEnumerable<EncapsulatedEvent>>(message);
                foreach(var encapsulatedEvent in encapsulatedEvents) 
                {
                    var existingYoungerSibling = db.Sensors.Where(s => s.OlderSiblingId == sensor.Id && s.Name == encapsulatedEvent.Name);
                    SensorEntity siblingSensor;
                    if(!existingYoungerSibling.Any())
                    {
                        if(db.Sensors.Where(s => s.OlderSiblingId == sensor.Id).Count() > GlobalConfiguration.MAXIMUM_NUM_OF_YOUNGER_SIBLINGS_SENSORS)
                        {
                            throw new MaximumNumberOfSensorSiblingsExceeded();
                        }
                        siblingSensor = new SensorEntity()
                        {
                            Id = Guid.NewGuid(),
                            Created = DateTime.Now,
                            OlderSibling = sensor,
                            Enabled = true,
                            Name = encapsulatedEvent.Name,
                            Parent = sensor.Parent,
                            Type = SensorType.VALUECHECK,
                            Interval = sensor.Interval,
                            GracePeriod = sensor.GracePeriod,
                        };
                        db.Sensors.Add(siblingSensor);
                    }
                    else
                    {
                        siblingSensor = existingYoungerSibling.Single();
                    }

                    eventEntity = new EventEntity()
                    {
                        Code = encapsulatedEvent.Code,
                        Message = encapsulatedEvent.Message,
                        Created = DateTime.Now,
                        Sensor = siblingSensor
                    };
                    db.Events.Add(eventEntity);
                }
            }else
            {
                eventEntity = new EventEntity()
                {
                    Code = code,
                    Message = message,
                    Created = DateTime.Now,
                    Sensor = sensor
                };
                db.Events.Add(eventEntity);
            }

            db.Events.CleanUpEntitiesForHost(sensor.Id);

            db.SaveChanges();

            return new DefaultResponse() { Success = true, Data = eventEntity.Id };
        }

    }
}
