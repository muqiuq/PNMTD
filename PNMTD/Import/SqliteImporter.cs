using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using PNMTD.Data;
using PNMTD.Lib.Helper;

namespace PNMTD.Import
{
    public sealed class SqliteImportOptions
    {
        public required string SqlitePath { get; init; }
        public string? UsersJsonPath { get; init; }
        /// <summary>The zone v0.12 wrote its timestamps in: DateTime.Now in its container (TZ, else UTC).</summary>
        public string? SourceTimeZone { get; init; }
        public bool Reset { get; init; }
    }

    public sealed class ImportReport
    {
        public bool Succeeded { get; set; }
        public string? Failure { get; set; }
        public string SourceTimeZone { get; set; } = "UTC";
        public List<(string Table, long Rows)> Tables { get; } = [];
        public List<string> Warnings { get; } = [];
        /// <summary>The users imported from users.json, with what happens at their first sign-in.</summary>
        public List<string> Users { get; } = [];
        public TimeSpan Duration { get; set; }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Succeeded ? "Import succeeded: every check passed, the data is committed." : "Import FAILED: nothing was committed.");
            if (Failure is not null) sb.AppendLine($"  Reason: {Failure}");
            sb.AppendLine($"  Source time zone: {SourceTimeZone} (timestamps converted to UTC)");
            foreach (var (table, rows) in Tables)
            {
                sb.AppendLine($"  {table,-24} {rows,10} rows");
            }
            foreach (var user in Users)
            {
                sb.AppendLine($"  User {user}");
            }
            foreach (var warning in Warnings)
            {
                sb.AppendLine($"  Warning: {warning}");
            }
            sb.AppendLine($"  Took {Duration.TotalSeconds:0.0} s");
            return sb.ToString();
        }
    }

    /// <summary>
    /// One-time copy of a v0.12 SQLite database into PostgreSQL (docs/webui-migration-plan.md §3.4).
    /// <list type="number">
    /// <item>Works on a copy of the file, opened read-only, with plain ADO.NET: independent of the EF model.</item>
    /// <item>Refuses anything but the v0.12 schema, and a target that already holds data (unless --reset).</item>
    /// <item>Copies every table with COPY, in foreign-key order, in one transaction, converting
    ///   timestamps from the source zone to UTC (and to PostgreSQL's microseconds).</item>
    /// <item>Before committing, reads everything back and compares: rows per table, a digest of every
    ///   table, events per sensor and their newest timestamp, and the token sets. Any difference rolls back.</item>
    /// </list>
    /// </summary>
    public sealed partial class SqliteImporter(PnmtdDbContext db, ILogger<SqliteImporter> logger)
    {
        /// <summary>The newest migration of v0.12.x: the only schema this importer reads.</summary>
        public const string ExpectedSourceMigration = "20250404182653_DoneZoneHostLink";

        /// <summary>Source table → target table, parents before children.</summary>
        private static readonly (string Source, string Target)[] TableOrder =
        [
            ("hosts", "hosts"),
            ("sensors", "sensors"),
            ("events", "events"),
            ("notificationrules", "notificationrules"),
            ("NotificationRuleSensor", "notificationrulesensor"),
            ("notificationruleevent", "notificationruleevent"),
            ("mailinputrules", "mailinputrules"),
            ("maillogs", "maillogs"),
            ("keyvalues", "keyvalues"),
            ("dnszones", "dnszones"),
            ("dnszoneentries", "dnszoneentries"),
            ("dnszonelogentries", "dnszonelogentries"),
        ];

        private sealed record Column(string Name, string PgType, bool Nullable);

        public ImportReport Import(SqliteImportOptions options)
        {
            var stopwatch = Stopwatch.StartNew();
            var report = new ImportReport();
            var scratch = Path.Combine(Path.GetTempPath(), "pnmtd-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                var users = options.UsersJsonPath is null ? null : UsersJsonImporter.Read(options.UsersJsonPath);
                var sourceZone = ResolveZone(options.SourceTimeZone);
                report.SourceTimeZone = sourceZone.Id;

                var copy = CopySource(options.SqlitePath, scratch);
                using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                source.Open();
                CheckSourceSchema(source);

                var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                db.Database.OpenConnection();
                using var transaction = connection.BeginTransaction();

                PrepareTarget(connection, transaction, options.Reset);
                if (users is not null)
                {
                    PrepareUsers(connection, transaction, options.Reset);
                }

                var expectedDigests = new Dictionary<string, string>();
                foreach (var (sourceTable, targetTable) in TableOrder)
                {
                    var columns = TargetColumns(connection, transaction, targetTable);
                    CheckColumns(source, sourceTable, targetTable, columns);
                    var (rows, digest) = CopyTable(source, connection, sourceTable, targetTable, columns, sourceZone);
                    expectedDigests[targetTable] = digest;
                    report.Tables.Add((targetTable, rows));
                    logger.LogInformation("Copied {Rows} rows into {Table}", rows, targetTable);
                }

                Verify(source, connection, transaction, expectedDigests, sourceZone, report);
                if (users is not null)
                {
                    InsertUsers(connection, transaction, users, report);
                }
                DuplicateTokenWarnings(connection, transaction, report);

                transaction.Commit();
                report.Succeeded = true;
            }
            catch (Exception ex) when (ex is ImportException or SqliteException or PostgresException)
            {
                report.Succeeded = false;
                report.Failure = ex.Message;
                logger.LogError("Import failed: {Message}", ex.Message);
            }
            finally
            {
                db.Database.CloseConnection();
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
                report.Duration = stopwatch.Elapsed;
            }
            return report;
        }

        private static TimeZoneInfo ResolveZone(string? name)
        {
            try
            {
                return Clock.FromName(name);
            }
            catch (TimeZoneNotFoundException)
            {
                throw new ImportException($"Unknown time zone '{name}' (an IANA name such as Europe/Zurich).");
            }
        }

        /// <summary>The database file and its WAL, copied: the original is never opened.</summary>
        private static string CopySource(string path, string scratch)
        {
            if (!File.Exists(path))
            {
                throw new ImportException($"{path} does not exist.");
            }
            Directory.CreateDirectory(scratch);
            var target = Path.Combine(scratch, "pnmtd.db");
            File.Copy(path, target);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(path + suffix)) File.Copy(path + suffix, target + suffix);
            }
            return target;
        }

        private static void CheckSourceSchema(SqliteConnection source)
        {
            using var command = source.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
            string? newest;
            try
            {
                newest = command.ExecuteScalar() as string;
            }
            catch (SqliteException)
            {
                throw new ImportException("This is not a PNMTD database (no __EFMigrationsHistory).");
            }
            if (newest != ExpectedSourceMigration)
            {
                throw new ImportException($"The database is at migration {newest ?? "(none)"}, not {ExpectedSourceMigration} (v0.12.x). " +
                    "Run v0.12.1 on it once to bring it up to date, then import again.");
            }
        }

        /// <summary>
        /// Users are only imported into an empty users table (a server that started first created its
        /// admin there); --reset empties it first. Without --users the users stay as they are.
        /// </summary>
        private void PrepareUsers(NpgsqlConnection connection, NpgsqlTransaction transaction, bool reset)
        {
            if (reset)
            {
                Execute(connection, transaction, "TRUNCATE users");
                return;
            }
            var existing = Scalar<long>(connection, transaction, "SELECT COUNT(*) FROM users");
            if (existing > 0)
            {
                throw new ImportException($"--users: the database has {existing} user(s) already (a server that started before the import " +
                    "creates the first admin). Import into a new database, or add --reset to replace them.");
            }
        }

        private void InsertUsers(NpgsqlConnection connection, NpgsqlTransaction transaction, List<UsersJsonImporter.ImportedUser> users,
            ImportReport report)
        {
            foreach (var (user, note) in users)
            {
                using var command = new NpgsqlCommand("""
                    INSERT INTO users ("Id", "Username", "NormalizedUsername", "DisplayName", "PasswordHash", "Role", "IsActive",
                        "MustChangePassword", "SecurityStamp", "IsTotpEnabled", "FailedLoginCount", "LockoutCount", "CreatedAt")
                    VALUES (@id, @username, @normalized, @display, @hash, @role, TRUE, @mustChange, @stamp, FALSE, 0, 0, @created);
                    INSERT INTO auth_log ("Id", "CreatedAt", "UserId", "Username", "EventType", "Detail")
                    VALUES (@logId, @created, @id, @username, 'UserCreated', 'import of users.json: ' || @role);
                    """, connection, transaction);
                command.Parameters.AddWithValue("id", user.Id);
                command.Parameters.AddWithValue("username", user.Username);
                command.Parameters.AddWithValue("normalized", user.NormalizedUsername);
                command.Parameters.AddWithValue("display", user.DisplayName);
                command.Parameters.AddWithValue("hash", user.PasswordHash);
                command.Parameters.AddWithValue("role", user.Role);
                command.Parameters.AddWithValue("mustChange", user.MustChangePassword);
                command.Parameters.AddWithValue("stamp", user.SecurityStamp);
                command.Parameters.AddWithValue("created", user.CreatedAt);
                command.Parameters.AddWithValue("logId", Guid.NewGuid());
                command.ExecuteNonQuery();
                report.Users.Add($"{user.Username} ({user.Role}): {note}");
            }
            // Read back: every user, with the hash it was given.
            var stored = new Dictionary<Guid, string>();
            using (var read = new NpgsqlCommand("""SELECT "Id", "PasswordHash" FROM users""", connection, transaction))
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read())
                {
                    stored[reader.GetGuid(0)] = reader.GetString(1);
                }
            }
            if (stored.Count != users.Count || users.Any(u => !stored.TryGetValue(u.User.Id, out var hash) || hash != u.User.PasswordHash))
            {
                throw new ImportException("users: what was written differs from users.json.");
            }
            report.Tables.Add(("users", users.Count));
        }

        private void PrepareTarget(NpgsqlConnection connection, NpgsqlTransaction transaction, bool reset)
        {
            var tables = string.Join(", ", TableOrder.Select(t => Quote(t.Target)));
            if (reset)
            {
                Execute(connection, transaction, $"TRUNCATE {tables} CASCADE");
                logger.LogInformation("--reset: emptied the imported tables");
                return;
            }
            foreach (var (_, target) in TableOrder)
            {
                if (Scalar<long>(connection, transaction, $"SELECT COUNT(*) FROM {Quote(target)}") > 0)
                {
                    throw new ImportException($"The PostgreSQL database already holds data ({target}). An import goes into an empty database; --reset replaces an earlier import.");
                }
            }
        }

        private static List<Column> TargetColumns(NpgsqlConnection connection, NpgsqlTransaction transaction, string table)
        {
            using var command = new NpgsqlCommand(
                "SELECT column_name, data_type, is_nullable = 'YES' FROM information_schema.columns " +
                "WHERE table_schema = current_schema() AND table_name = @t ORDER BY ordinal_position", connection, transaction);
            command.Parameters.AddWithValue("t", table);
            using var reader = command.ExecuteReader();
            var columns = new List<Column>();
            while (reader.Read())
            {
                columns.Add(new Column(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
            }
            if (columns.Count == 0)
            {
                throw new ImportException($"Table {table} is missing in PostgreSQL: run `pnmtd db migrate` first.");
            }
            return columns;
        }

        /// <summary>Every source column must have a target column, or its data would be lost.</summary>
        private static void CheckColumns(SqliteConnection source, string sourceTable, string targetTable, List<Column> target)
        {
            var sourceColumns = SourceColumns(source, sourceTable);
            var missing = sourceColumns.Where(c => target.All(t => t.Name != c)).ToList();
            if (missing.Count > 0)
            {
                throw new ImportException($"{sourceTable} has column(s) PostgreSQL does not: {string.Join(", ", missing)}.");
            }
            var unfillable = target.Where(t => !t.Nullable && !sourceColumns.Contains(t.Name)).ToList();
            if (unfillable.Count > 0)
            {
                throw new ImportException($"{targetTable} requires column(s) the v0.12 database does not have: {string.Join(", ", unfillable.Select(c => c.Name))}.");
            }
        }

        private static List<string> SourceColumns(SqliteConnection source, string table)
        {
            using var command = source.CreateCommand();
            command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            if (names.Count == 0) throw new ImportException($"The v0.12 database has no table {table}.");
            return names;
        }

        private (long Rows, string Digest) CopyTable(SqliteConnection source, NpgsqlConnection target, string sourceTable, string targetTable,
            List<Column> columns, TimeZoneInfo zone)
        {
            var sourceColumns = SourceColumns(source, sourceTable);
            var copied = columns.Where(c => sourceColumns.Contains(c.Name)).ToList();
            using var read = source.CreateCommand();
            // Siblings reference their older sensor: rows without one first.
            var order = sourceTable == "sensors" ? "ORDER BY OlderSiblingId IS NOT NULL" : "";
            read.CommandText = $"SELECT {string.Join(", ", copied.Select(c => Quote(c.Name)))} FROM {Quote(sourceTable)} {order}";
            using var reader = read.ExecuteReader();

            var rows = new List<object?[]>();
            using (var writer = target.BeginBinaryImport(
                $"COPY {Quote(targetTable)} ({string.Join(", ", copied.Select(c => Quote(c.Name)))}) FROM STDIN (FORMAT BINARY)"))
            {
                while (reader.Read())
                {
                    var values = new object?[copied.Count];
                    writer.StartRow();
                    for (var i = 0; i < copied.Count; i++)
                    {
                        var value = Convert(reader, i, copied[i], targetTable, zone);
                        values[i] = value;
                        if (value is null) writer.WriteNull();
                        else writer.Write(value, NpgsqlType(copied[i]));
                    }
                    rows.Add(values);
                }
                writer.Complete();
            }
            return (rows.Count, Digest(rows, copied));
        }

        [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(\.\d+)?$")]
        private static partial Regex V012Timestamp();

        private static object? Convert(SqliteDataReader reader, int i, Column column, string table, TimeZoneInfo zone)
        {
            if (reader.IsDBNull(i)) return null;
            switch (column.PgType)
            {
                case "uuid":
                    return Guid.Parse(reader.GetString(i));
                case "timestamp with time zone":
                    return ToUtc(reader.GetString(i), zone);
                case "boolean":
                    return reader.GetInt64(i) != 0;
                case "integer":
                    return checked((int)reader.GetInt64(i));
                case "bigint":
                    return reader.GetInt64(i);
                case "text":
                    var text = reader.GetString(i);
                    // The LAST_* key values: v0.12 wrote local time without an offset; 1.0 writes ISO 8601 UTC.
                    if (table == "keyvalues" && column.Name == "Value" && V012Timestamp().IsMatch(text))
                    {
                        return ToUtc(text, zone).ToString("o", CultureInfo.InvariantCulture);
                    }
                    return text;
                default:
                    throw new ImportException($"{table}.{column.Name}: no conversion for PostgreSQL type {column.PgType}.");
            }
        }

        /// <summary>v0.12's "yyyy-MM-dd HH:mm:ss.FFFFFFF" (local, no offset) as UTC, at PostgreSQL's microseconds.</summary>
        private static DateTime ToUtc(string text, TimeZoneInfo zone)
        {
            var local = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
            if (local.Year == 1)
            {
                return Clock.Never; // "never" (DateTime.MinValue); converting it would underflow
            }
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) local = local.AddHours(1);
            var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
            return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
        }

        private static NpgsqlDbType NpgsqlType(Column column) => column.PgType switch
        {
            "uuid" => NpgsqlDbType.Uuid,
            "timestamp with time zone" => NpgsqlDbType.TimestampTz,
            "boolean" => NpgsqlDbType.Boolean,
            "integer" => NpgsqlDbType.Integer,
            "bigint" => NpgsqlDbType.Bigint,
            _ => NpgsqlDbType.Text,
        };

        /// <summary>Order-independent: rows sorted by their canonical text first.</summary>
        private static string Digest(IEnumerable<object?[]> rows, List<Column> columns)
        {
            var lines = rows.Select(r => string.Join('\u001f', r.Select(Canonical))).ToList();
            lines.Sort(StringComparer.Ordinal);
            return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', lines)))) + $":{columns.Count}";
        }

        private static string Canonical(object? value) => value switch
        {
            null => "␀",
            DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D"),
            bool b => b ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        private void Verify(SqliteConnection source, NpgsqlConnection target, NpgsqlTransaction transaction,
            Dictionary<string, string> expectedDigests, TimeZoneInfo zone, ImportReport report)
        {
            // 1. Every table read back from PostgreSQL, as it will be used.
            foreach (var (sourceTable, targetTable) in TableOrder)
            {
                var columns = TargetColumns(target, transaction, targetTable)
                    .Where(c => SourceColumns(source, sourceTable).Contains(c.Name)).ToList();
                using var command = new NpgsqlCommand($"SELECT {string.Join(", ", columns.Select(c => Quote(c.Name)))} FROM {Quote(targetTable)}", target, transaction);
                using var reader = command.ExecuteReader();
                var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var values = new object?[columns.Count];
                    for (var i = 0; i < columns.Count; i++)
                    {
                        values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    rows.Add(values);
                }
                if (Digest(rows, columns) != expectedDigests[targetTable])
                {
                    throw new ImportException($"Verification failed: {targetTable} reads back differently from what was copied.");
                }
            }

            // 2. Rows per table, counted on both sides.
            foreach (var (sourceTable, targetTable) in TableOrder)
            {
                var expected = SqliteScalar(source, $"SELECT COUNT(*) FROM {Quote(sourceTable)}");
                var actual = Scalar<long>(target, transaction, $"SELECT COUNT(*) FROM {Quote(targetTable)}");
                if (expected != actual)
                {
                    throw new ImportException($"Verification failed: {targetTable} has {actual} rows, the v0.12 database {expected}.");
                }
            }

            // 3. Events per sensor and the newest of them: what the sensor API and the dashboard read.
            var sourceStats = new Dictionary<Guid, (long Count, DateTime Newest)>();
            using (var command = source.CreateCommand())
            {
                command.CommandText = "SELECT SensorId, COUNT(*), MAX(Created) FROM events GROUP BY SensorId";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    sourceStats[Guid.Parse(reader.GetString(0))] = (reader.GetInt64(1), ToUtc(reader.GetString(2), zone));
                }
            }
            using (var command = new NpgsqlCommand("SELECT \"SensorId\", COUNT(*), MAX(\"Created\") FROM events GROUP BY \"SensorId\"", target, transaction))
            using (var reader = command.ExecuteReader())
            {
                var seen = 0;
                while (reader.Read())
                {
                    seen++;
                    var sensor = reader.GetGuid(0);
                    var stats = (reader.GetInt64(1), DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
                    if (!sourceStats.TryGetValue(sensor, out var expected) || expected != stats)
                    {
                        throw new ImportException($"Verification failed: the events of sensor {sensor} differ.");
                    }
                }
                if (seen != sourceStats.Count)
                {
                    throw new ImportException("Verification failed: sensors with events differ.");
                }
            }

            // 4. The tokens, exactly: hundreds of applications use them.
            var sourceTokens = new HashSet<string>();
            using (var command = source.CreateCommand())
            {
                command.CommandText = "SELECT Id, SecretWriteToken, SecretReadToken FROM sensors";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    sourceTokens.Add($"{Guid.Parse(reader.GetString(0))}|{(reader.IsDBNull(1) ? "" : reader.GetString(1))}|{(reader.IsDBNull(2) ? "" : reader.GetString(2))}");
                }
            }
            var targetTokens = new HashSet<string>();
            using (var command = new NpgsqlCommand("SELECT \"Id\", \"SecretWriteToken\", \"SecretReadToken\" FROM sensors", target, transaction))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    targetTokens.Add($"{reader.GetGuid(0)}|{(reader.IsDBNull(1) ? "" : reader.GetString(1))}|{(reader.IsDBNull(2) ? "" : reader.GetString(2))}");
                }
            }
            if (!sourceTokens.SetEquals(targetTokens))
            {
                throw new ImportException("Verification failed: the sensor tokens differ.");
            }
            report.Warnings.Add("Timestamps are stored at microsecond precision now (v0.12: 100 ns).");
        }

        /// <summary>Kept as they are (§3.3): they answered 500 in v0.12 and still do.</summary>
        private static void DuplicateTokenWarnings(NpgsqlConnection connection, NpgsqlTransaction transaction, ImportReport report)
        {
            foreach (var column in new[] { "SecretWriteToken", "SecretReadToken" })
            {
                using var command = new NpgsqlCommand(
                    $"SELECT string_agg(\"Name\", ', ' ORDER BY \"Name\"), COUNT(*) FROM sensors WHERE {Quote(column)} IS NOT NULL " +
                    $"GROUP BY {Quote(column)} HAVING COUNT(*) > 1", connection, transaction);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    report.Warnings.Add($"{reader.GetInt64(1)} sensors share one {column} ({reader.GetString(0)}): requests with it fail with 500, as in v0.12. Rotate their tokens in the UI.");
                }
            }
        }

        private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

        private static void Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
        {
            using var command = new NpgsqlCommand(sql, connection, transaction);
            command.ExecuteNonQuery();
        }

        private static T Scalar<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
        {
            using var command = new NpgsqlCommand(sql, connection, transaction);
            return (T)command.ExecuteScalar()!;
        }

        private static long SqliteScalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }
    }

    public sealed class ImportException(string message) : Exception(message);
}
