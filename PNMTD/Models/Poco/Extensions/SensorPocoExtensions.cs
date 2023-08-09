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
                Parent = sensorEntity.Parent != null ? sensorEntity.Parent.ToPoco() : null,
                SecretToken = sensorEntity.SecretToken,
            };
        }

        public static void SetNewSecretToken(this SensorEntity sensorEntity)
        {
            string beginOfSecretToken;
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(sensorEntity.Id.ToString());
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                beginOfSecretToken = Convert.ToHexString(hashBytes);
            }

            sensorEntity.SecretToken = beginOfSecretToken.Substring(0, 7) + "-" +Guid.NewGuid().ToString();
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
                Parameters = sensorPoco.Parameters,
                SecretToken = sensorPoco.SecretToken
            };

            if (!isNew)
            {
                sensorEntity.Id = sensorPoco.Id;
            }
            else
            {
                sensorEntity.Id = Guid.NewGuid();
                sensorEntity.SetNewSecretToken();
            }

            return sensorEntity;
        }
    }
}
