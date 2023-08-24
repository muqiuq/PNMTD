using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using System.ComponentModel;

namespace PNMTD.Data
{
    public static class KeyValueDbExtensions
    {


        public static void SetKeyValueEntryByEnum(this PnmtdDbContext db, KeyValueKeyEnums keyEnum, object value)
        {
            var keyStr = Enum.GetName<KeyValueKeyEnums>(keyEnum).ToLower();
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr).SingleOrDefault();
            if (entry == null)
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

        public static bool KeyValueKeyExists(this PnmtdDbContext db, KeyValueKeyEnums keyEnum)
        {
            var keyStr = Enum.GetName<KeyValueKeyEnums>(keyEnum).ToLower();
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr).SingleOrDefault();

            return entry != null;
        }

        public static T? GetKeyValueByEnum<T>(this PnmtdDbContext db, KeyValueKeyEnums keyEnum)
        {
            var keyStr = Enum.GetName<KeyValueKeyEnums>(keyEnum).ToLower();
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr).SingleOrDefault();

            if (entry == null)
            {
                return default(T);
            }
            try
            {
                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (converter != null)
                {
                    return (T)converter.ConvertFromString(entry.Value);
                }
            }
            catch (NotSupportedException ex)
            {
                return default(T);
            }
            return default(T);
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
