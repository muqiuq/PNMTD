using PNMTD.Lib.Models.Poco;
using PNMTD.Models.Db;

namespace PNMTD.Models.Poco.Extensions
{
    public static class KeyValuePocoExtension
    {
        public static KeyValuePoco ToPoco(this KeyValueEntity keyValueEntity)
        {
            return new KeyValuePoco
            {
                Id = keyValueEntity.Id,
                Enabled = keyValueEntity.Enabled,
                Note = keyValueEntity.Note,
                IsReadOnly = keyValueEntity.IsReadOnly,
                Key = keyValueEntity.Key,
                Value = keyValueEntity.Value
            };
        }

        public static KeyValueEntity ToEntity(this KeyValuePoco keyValuePoco, bool isNew)
        {
            var keyValueEntity = new KeyValueEntity()
            {
                Id = keyValuePoco.Id,
                Enabled = keyValuePoco.Enabled,
                Note = keyValuePoco.Note, 
                IsReadOnly = keyValuePoco.IsReadOnly, 
                Key = keyValuePoco.Key,
                Value = keyValuePoco.Value
            };

            if (!isNew)
            {
                keyValueEntity.Id = keyValuePoco.Id;
            }
            else
            {
                keyValueEntity.Id = Guid.NewGuid();
            }

            return keyValueEntity;
        }
    }
}
