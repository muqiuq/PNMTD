using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;
using PNMTD.Models.Poco.Extensions;
using PNMTD.Models.Responses;

namespace PNMTD.Controllers
{
    [ApiController]
    [Route("keyvalue")]
    public class KeyValueController : Controller
    {

        private PnmtdDbContext Db;

        public KeyValueController(PnmtdDbContext db)
        {
            this.Db = db;
        }

        [HttpGet]
        public IEnumerable<KeyValuePoco> Get()
        {
            return Db.KeyValues.Select(n => n.ToPoco()).ToList();
        }

        [HttpGet("{id}")]
        public object Get(Guid id)
        {
            var keyValue = Db.KeyValues.Where(h => h.Id == id);
            if (keyValue.Any())
            {
                return Ok(keyValue.Single().ToPoco());
            }
            else
            {
                return NotFound();
            }
        }

        [HttpPost]
        public DefaultResponse Post([FromBody] KeyValuePoco keyValuePoco)
        {
            var entity = keyValuePoco.ToEntity(true);
            entity.Key = entity.Key.ToLower();
            Db.KeyValues.Add(entity);
            int changes = -1;
            try
            {
                changes = Db.SaveChanges();
            }catch(DbUpdateException ex)
            {
                return new DefaultResponse()
                {
                    Success = false,
                    Message = ex.Message
                };
            }
            
            return new DefaultResponse()
            {
                Success = changes == 1,
                Message = "",
                Data = entity.Id
            };
        }

        // PUT api/<HostController>/5
        [HttpPut]
        public DefaultResponse Put([FromBody] KeyValuePoco keyValuePoco)
        {
            var entity = keyValuePoco.ToEntity(false);
            entity.Key = entity.Key.ToLower();
            Db.KeyValues.Attach(entity);
            Db.Update<KeyValueEntity>(entity);
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
            Db.KeyValues.Remove(Db.KeyValues.Where(n => n.Id == id).Single());
            Db.SaveChanges();

            return new DefaultResponse() { Success = true, Message = "", Data = id };
        }
    }
}
