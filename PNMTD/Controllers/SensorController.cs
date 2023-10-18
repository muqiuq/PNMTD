using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
using PNMTD.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Poco;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Models.Responses;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace PNMTD.Controllers
{
    [Route("sensor")]
    [ApiController]
    public class SensorController : ControllerBase
    {
        public PnmtdDbContext Db { get; }

        public SensorController(PnmtdDbContext Db)
        {
            this.Db = Db;
        }
        [HttpGet]
        public IEnumerable<SensorPoco> Get()
        {
            return Db.Sensors.Select(h => h.ToPoco()).ToList();
        }

        [HttpGet("{id}")]
        public SensorPoco Get(Guid id)
        {
            return Db.Sensors.Where(h => h.Id == id).Single().ToPoco();
        }

        [HttpGet("byhost/{id}")]
        public IEnumerable<SensorPoco> GetByHostId(Guid id)
        {
            return Db.Sensors.Where(s => s.ParentId == id).Select(s => s.ToPoco()).ToList();
        }

        [HttpGet("bytype/{sensorType}")]
        public IEnumerable<SensorPoco> GetByHostId(SensorType sensorType)
        {
            return Db.Sensors.Where(s => s.Type == sensorType).Select(s => s.ToPoco()).ToList();
        }

        [HttpGet("bysecrettoken/{secretToken}")]
        public object GetBySecretToken(string secretToken)
        {
            return HttpResultHelper.ReturnSinglePocoOrNotFound<SensorEntity, SensorPoco>(
                Db.Sensors.Where(s => s.SecretToken == secretToken), 
                t => t.ToPoco()
                );
        }

        [HttpGet("newsecrettoken/{id}")]
        public object NewSecretToken(Guid id)
        {
            var sensor = Db.Sensors.Where(h => h.Id == id).SingleOrDefault();
            if(sensor == null)
            {
                return NotFound();
            }
            sensor.SetNewSecretToken();
            Db.SaveChanges();
            return Ok(new DefaultResponse()
            {
                Success = true,
                Data = sensor.SecretToken
            });
        }

        [HttpPost]
        public DefaultResponse Post([FromBody] SensorPoco sensorPoco)
        {
            var entity = sensorPoco.ToEntity(true);
            var change = Db.Sensors.Add(entity);
            Db.SaveChanges();
            return new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Unchanged,
                Message = "",
                Data = entity.Id
            };
        }

        [HttpPut]
        public IActionResult Put([FromBody] SensorPoco sensorPoco)
        {
            if (!Db.Sensors.Any(s => s.Id == sensorPoco.Id)) NotFound();

            var entity = sensorPoco.ToEntity(false);
            Db.Sensors.Attach(entity);
            var change = Db.Update<SensorEntity>(entity);
            Db.SaveChanges();
            return Ok(new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Unchanged,
                Message = "",
                Data = entity.Id
            });
        }

        [HttpDelete("{id}")]
        public IActionResult Delete(Guid id)
        {
            var sensor = Db.Sensors.Where(n => n.Id == id).SingleOrDefault();
            if (sensor == null) return NotFound();
            var change = Db.Sensors.Remove(sensor);
            Db.SaveChanges();

            return Ok(new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Detached,
                Message = "",
                Data = id
            });
        }
    }
}
