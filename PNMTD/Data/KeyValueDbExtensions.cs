using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using System.ComponentModel;
using Microsoft.EntityFrameworkCore;

namespace PNMTD.Data
{
    public static class KeyValueDbExtensions
    {


        /// <summary>
        /// Writes a value (and enables the entry) in one statement, with no SaveChanges: the tasks
        /// call this every few seconds, and two of them may write the same key at once.
        /// </summary>
        public static void SetKeyValueEntryByEnum(this PnmtdDbContext db, KeyValueKeyEnums keyEnum, object value, bool readOnly = true)
        {
            var keyStr = KeyOf(keyEnum);
            var text = value.ToString() ?? "";
            db.Database.ExecuteSql($"""
                INSERT INTO keyvalues ("Id", "Enabled", "Note", "Key", "Value", "IsReadOnly")
                VALUES ({Guid.NewGuid()}, TRUE, '', {keyStr}, {text}, {readOnly})
                ON CONFLICT ("Key") DO UPDATE SET "Value" = EXCLUDED."Value", "Enabled" = TRUE
                """);
        }

        public static string KeyOf(KeyValueKeyEnums keyEnum) => Enum.GetName(keyEnum)!.ToLowerInvariant();

        public static bool KeyValueKeyExists(this PnmtdDbContext db, KeyValueKeyEnums keyEnum)
        {
            var keyStr = KeyOf(keyEnum);
            var entry = db.KeyValues.Where(kv => kv.Key == keyStr).SingleOrDefault();

            return entry != null;
        }

        public static T? GetKeyValueByEnum<T>(this PnmtdDbContext db, KeyValueKeyEnums keyEnum)
        {
            var keyStr = KeyOf(keyEnum);
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
            var keyStr = KeyOf(keyEnum);
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
            var keyStr = KeyOf(keyEnum);
            // ISO 8601 in UTC ("2026-11-11T09:15:00.1234567Z"); v0.12 wrote local time without an offset.
            var timestamp = Clock.UtcNow.ToString("o");
            db.Database.ExecuteSql($"""
                INSERT INTO keyvalues ("Id", "Enabled", "Note", "Key", "Value", "IsReadOnly")
                VALUES ({Guid.NewGuid()}, TRUE, '', {keyStr}, {timestamp}, TRUE)
                ON CONFLICT ("Key") DO UPDATE SET "Value" = EXCLUDED."Value"
                """);
        }

    }
}
