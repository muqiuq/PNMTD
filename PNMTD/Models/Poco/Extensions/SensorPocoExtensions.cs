using PNMTD.Models.Db;
using System.Text;

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
                SecretWriteToken = sensorEntity.SecretWriteToken,
                SecretReadToken = sensorEntity.SecretReadToken,
                Source = sensorEntity.Source,   
                Ignore = sensorEntity.Ignore,
                Status = sensorEntity.Status,
            };
        }

        public static void SetRandomSecretReadToken(this SensorEntity sensorEntity)
        {
            sensorEntity.SecretReadToken = GetRandomSecretToken(sensorEntity.Id, offset: 3);
        }

        private static string GetRandomSecretToken(Guid seedId, int offset = 0)
        {
            if (offset < 0 || offset > 3)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "offset must be in between (inclusive) 0 and 3");
            }
            string beginOfSecretToken;
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] inputBytes = System.Text.Encoding.ASCII.GetBytes(seedId.ToString());
                byte[] hashBytes = md5.ComputeHash(inputBytes);

                beginOfSecretToken = Convert.ToHexString(hashBytes);
            }

            return (beginOfSecretToken.Substring(offset, 7) + "-" + Guid.NewGuid().ToString()).ToLower();
        }

        public static void SetNewSecretToken(this SensorEntity sensorEntity)
        {
            sensorEntity.SecretWriteToken = GetRandomSecretToken(sensorEntity.Id);
            sensorEntity.SecretReadToken = GetRandomSecretToken(sensorEntity.Id, offset: 3);
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
                SecretWriteToken = sensorPoco.SecretWriteToken,
                SecretReadToken = sensorPoco.SecretReadToken,
                Source = sensorPoco.Source,
                Ignore = sensorPoco.Ignore,
                Status = sensorPoco.Status,
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

            sensorEntity.SecretReadToken ??= GetRandomSecretToken(sensorEntity.Id, offset: 3);

            return sensorEntity;
        }
    }
}
