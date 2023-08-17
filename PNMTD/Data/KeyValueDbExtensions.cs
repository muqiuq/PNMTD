using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Data
{
    public static class KeyValueDbExtensions
    {


        public static void SetKeyValueEntryByEnum(this PnmtdDbContext db, KeyValueKeyEnums keyEnum, object value)
        {
            var keyStr = Enum.GetName<KeyValueKeyEnums>(keyEnum).ToLower();
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr).SingleOrDefault();
            if(entry == null )
            {
                entry = new KeyValueEntity()
                {
                    Id = Guid.NewGuid(),
                    Enabled = true,
                    IsReadOnly = true,
                    Key = keyStr,
                    Value = value.ToString(),
                    Note = ""
                };
                db.KeyValues.Add(entry);
                db.SaveChanges();
            }
            else
            {
                entry.Value = value.ToString();
                db.SaveChanges();
            }
        }

        public static void UpdateKeyValueTimestampToNow(this PnmtdDbContext db, KeyValueKeyEnums keyEnum)
        {
            var keyStr = Enum.GetName<KeyValueKeyEnums>(keyEnum).ToLower();
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr).SingleOrDefault();
            if (entry == null)
            {
                entry = new KeyValueEntity()
                {
                    Id = Guid.NewGuid(),
                    Enabled = true,
                    IsReadOnly = true,
                    Key = keyStr,
                    Value = timestamp,
                    Note = ""
                };
                db.KeyValues.Add(entry);
                db.SaveChanges();
            }
            else
            {
                entry.Value = timestamp;
                db.SaveChanges();
            }
        }

    }
}
