using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using System.ComponentModel;

namespace PNMTD.Data
{
    public static class KeyValueDbExtensions
    {


        public static void SetKeyValueEntryByEnum(this PnmtdDbContext db, KeyValueKeyEnums keyEnum, object value, bool readOnly = true)
        {
            var keyStr = Enum.GetName<KeyValueKeyEnums>(keyEnum).ToLower();
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr).SingleOrDefault();
            if (entry == null)
            {
                entry = new KeyValueEntity()
                {
                    Id = Guid.NewGuid(),
                    Enabled = true,
                    IsReadOnly = readOnly,
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
                entry.Enabled = true;
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
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr && kv.Enabled).SingleOrDefault();

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


        public static bool TryGetKeyValueByEnumSetIfFailed<T>(this PnmtdDbContext db, KeyValueKeyEnums keyEnum, T setValue, out T? outValue, bool readOnly = true)
        {
            if(TryGetKeyValueByEnum(db, keyEnum, out outValue))
            {
                return true;
            }
            else
            {
                SetKeyValueEntryByEnum(db, keyEnum, setValue, readOnly);
                return false;
            }
        }

        public static bool TryGetKeyValueByEnum<T>(this PnmtdDbContext db, KeyValueKeyEnums keyEnum, out T? outValue)
        {
            var keyStr = Enum.GetName<KeyValueKeyEnums>(keyEnum).ToLower();
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr && kv.Enabled).SingleOrDefault();

            if (entry == null)
            {
                outValue = default(T);
                return false;
            }
            try
            {
                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (converter != null)
                {
                    outValue = (T)converter.ConvertFromString(entry.Value);
                    return true;
                }
            }
            catch (NotSupportedException ex)
            {
                outValue = default(T);
                return false;
            }
            outValue = default(T);
            return false;
        }

        public static void UpdateKeyValueTimestampToNow(this PnmtdDbContext db, KeyValueKeyEnums keyEnum)
        {
            var keyStr = Enum.GetName<KeyValueKeyEnums>(keyEnum).ToLower();
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr && kv.Enabled).SingleOrDefault();
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
