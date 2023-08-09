using Microsoft.AspNetCore.Mvc;
using PNMTD.Data;
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
            return new DefaultResponse()
            {
                Success = true,
                Data = sensor.SecretToken
            };
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
        public DefaultResponse Put([FromBody] SensorPoco sensorPoco)
        {
            var entity = sensorPoco.ToEntity(false);
            Db.Sensors.Attach(entity);
            var change = Db.Update<SensorEntity>(entity);
            Db.SaveChanges();
            return new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Unchanged,
                Message = "",
                Data = entity.Id
            };
        }

        [HttpDelete("{id}")]
        public DefaultResponse Delete(Guid id)
        {
            var change = Db.Sensors.Remove(Db.Sensors.Where(n => n.Id == id).Single());
            Db.SaveChanges();

            return new DefaultResponse()
            {
                Success = change.State == Microsoft.EntityFrameworkCore.EntityState.Detached,
                Message = "",
                Data = id
            };
        }
    }
}
