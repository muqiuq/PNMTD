using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using PNMTD.Data;
using PNMTD.Exceptions;
using PNMTD.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco.Extensions;
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

        [HttpGet("events/lasterrors", Name = "Get last few error events (Get)")]
        public object GetLastErrorEvents()
        {
            var events = db.Events.Where(e => e.Code > EventEntityPoco.END_OF_SUCCESS_CODES 
                && e.Sensor.Type != SensorType.ONE_WITHIN_TIMESPAN && e.Sensor.Enabled && !e.Sensor.Ignore)
                .Include(e => e.Sensor)
                .ThenInclude(e => e.Parent)
                .OrderByDescending(e => e.Created)
                .Take(10).ToList();

            return events.Select(e => e.ToPoco()).ToList();
        }

        [HttpGet("events/sensor/{sensorIdStr}", Name = "Get Events (Get)")]
        public object GetEvents(string sensorIdStr)
        {
            var sensorId = Guid.Parse(sensorIdStr);

            var sensor = db.Sensors.Where(s => s.Id == sensorId).SingleOrDefault();

            if(sensor == null)
            {
                return NotFound();
            }

            var events = db.Events
                .Where(e => e.SensorId == sensorId)
                .Include(e => e.Sensor)
                .ThenInclude(e => e.Parent)
                .ToList();

            return events.Select(e => e.ToPoco()).ToList();
        }

        [AllowAnonymous]
        [HttpGet("event/{secretToken}/{code}/{message?}", Name = "Submit Event (Get)")]
        public object SubmitEvent(string secretToken, int code, string? message)
        {
            var sensorR = db.Sensors.Where(s => s.SecretToken == secretToken && s.Enabled);

            if (!sensorR.Any())
            {
                return NotFound();
            }

            var sensor = sensorR.Single();
            var sensorIdGuid = sensor.Id;

            // A $ indicated that the contect of the message is encoded with Base64
            if (message != null && message.StartsWith("$"))
            {
                message = Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(1)));
            }

            var lastEvent = db.Events.Where(e => e.SensorId == sensorIdGuid)
                .OrderByDescending(e => e.Created).FirstOrDefault();

            if (lastEvent != null)
            {
                if ((DateTime.Now - lastEvent.Created) < TimeSpan.FromSeconds(GlobalConfiguration.MINIMUM_TIME_DIFFERENCE_BETWEEN_EVENTS_IN_SECONDS)
                    && !Global.IsDevelopment
                    )
                {
                    return Problem("Too many requesteds", statusCode: 425);
                }
            }

            EventEntity eventEntity = createNewEntity(code, message, sensor);

            db.Events.CleanUpEntitiesForHost(sensor.Id);

            db.SaveChanges();

            return new DefaultResponse() { Success = true, Data = eventEntity.Id };
        }

        private EventEntity createNewEntity(int code, string? message, SensorEntity sensor)
        {
            var eventEntity = new EventEntity()
            {
                Code = code,
                Message = message,
                Created = DateTime.Now,
                Sensor = sensor,
                Source = this.GetRemoteIpAddressOrDefault()
            };
            if (sensor.Type == SensorType.VALUECHECK || sensor.Type == SensorType.HEARTBEAT_VALUECHECK)
            {
                SensorTypeActionHelper.AdjustCodeForValueCheckSensorTypeInEvent(eventEntity, sensor);
            }

            db.Events.Add(eventEntity);

            return eventEntity;
        }

        [AllowAnonymous]
        [HttpPost("event/{secretToken}/{code}", Name = "Submit Event (Post)")]
        public async Task<object> PostEvent(string secretToken, int code)
        {
            var sensorR = db.Sensors.Where(s => s.SecretToken == secretToken && s.Enabled);

            if (!sensorR.Any())
            {
                return NotFound();
            }

            var sensor = sensorR.Single();
            var sensorIdGuid = sensor.Id;

            var message = (await Request.GetRequestBody()).Trim();

            // A $ indicated that the contect of the message is encoded with Base64
            if (message != null && message.StartsWith("$"))
            {
                message = Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(1)));
            }
            EventEntity eventEntity = null;

            if (sensor.Type == SensorType.ENCAPSULADED && message != null)
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
                            Type = SensorType.SIMPLE,
                            Interval = sensor.Interval,
                            GracePeriod = sensor.GracePeriod,
                            Parameters = "",
                        };
                        siblingSensor.SetNewSecretToken();
                        db.Sensors.Add(siblingSensor);
                    }
                    else
                    {
                        siblingSensor = existingYoungerSibling.Single();
                    }

                    eventEntity = createNewEntity(encapsulatedEvent.Code, encapsulatedEvent.Message, siblingSensor);
                }
            }else
            {
                eventEntity = createNewEntity(code, message, sensor);
            }

            db.Events.CleanUpEntitiesForHost(sensor.Id);

            db.SaveChanges();

            return new DefaultResponse() { Success = true, Data = eventEntity.Id };
        }

    }
}
