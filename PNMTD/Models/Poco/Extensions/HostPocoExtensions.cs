using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class HostPocoExtensions
    {
        public static HostPoco ToPoco(this HostEntity hostEntity)
        {
            return new HostPoco
            {
                Created = hostEntity.Created,
                Enabled = hostEntity.Enabled,
                Id = hostEntity.Id,
                Location = hostEntity.Location,
                Name = hostEntity.Name,
                Notes = hostEntity.Notes,
                Sensors = hostEntity.Sensors.Select(x => x.Id).ToList(),
            };
        }

        public static HostEntity ToEntity(this HostPoco hostPoco, bool isNew)
        {
            var hostEntity = new HostEntity()
            {
                Created = DateTime.Now,
                Enabled = hostPoco.Enabled,
                Location = hostPoco.Location,
                Name = hostPoco.Name,
                Notes = hostPoco.Notes,
            };

            if (!isNew)
            {
                hostEntity.Id = hostPoco.Id;
            }
            else
            {
                hostEntity.Id = Guid.NewGuid();
            }

            return hostEntity;
        }
    }
}
