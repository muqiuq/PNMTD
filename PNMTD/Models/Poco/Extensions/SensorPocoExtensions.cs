using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class SensorPocoExtensions
    {
        public static SensorPoco ToPoco(this SensorEntity sensorEntity)
        {
            return new SensorPoco
            {
                Created = sensorEntity.Created,
                Enabled = sensorEntity.Enabled,
                Id = sensorEntity.Id,
                OlderSiblingId = sensorEntity.OlderSiblingId,
                GracePeriod = sensorEntity.GracePeriod,
                Interval = sensorEntity.Interval,
                Name = sensorEntity.Name,
                ParentId = sensorEntity.ParentId,
                TextId = sensorEntity.TextId,
                Type = sensorEntity.Type,
                Parameters = sensorEntity.Parameters,
            };
        }

        public static SensorEntity ToEntity(this SensorPoco sensorPoco, bool isNew)
        {
            var sensorEntity = new SensorEntity()
            {
                Created = DateTime.Now,
                Enabled = sensorPoco.Enabled,
                OlderSiblingId = sensorPoco.OlderSiblingId,
                GracePeriod = sensorPoco.GracePeriod,
                Interval = sensorPoco.Interval,
                Name = sensorPoco.Name,
                ParentId = sensorPoco.ParentId,
                TextId = sensorPoco.TextId,
                Type = sensorPoco.Type,
                Parameters = sensorPoco.Parameters
            };

            if (!isNew)
            {
                sensorEntity.Id = sensorPoco.Id;
            }
            else
            {
                sensorEntity.Id = Guid.NewGuid();
            }

            return sensorEntity;
        }
    }
}
