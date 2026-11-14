using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PNMTD.Data;

namespace PNMTD.Tests;

/// <summary>Databases of their own, next to the run's pnmtd_test, for tests that need an empty one.</summary>
internal static class TestDatabases
{
    public static string ConnectionString(string name)
    {
        var server = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PNMTD_TEST_DB") ?? TestDefaults.DefaultServer)
        {
            Database = name,
            Pooling = false,
        };
        return server.ConnectionString;
    }

    /// <summary>Dropped if it exists, and created empty, without the migrations (an upgrade's starting point).</summary>
    public static PnmtdDbContext Empty(string name)
    {
        var server = new NpgsqlConnectionStringBuilder(ConnectionString("postgres"));
        using (var connection = new NpgsqlConnection(server.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE); CREATE DATABASE {name};";
            command.ExecuteNonQuery();
        }
        return PnmtdDbContext.Create(ConnectionString(name));
    }

    /// <summary>Dropped if it exists, created and migrated.</summary>
    public static PnmtdDbContext Fresh(string name)
    {
        var server = new NpgsqlConnectionStringBuilder(ConnectionString("postgres"));
        using (var connection = new NpgsqlConnection(server.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS {name} WITH (FORCE); CREATE DATABASE {name};";
            command.ExecuteNonQuery();
        }
        var db = PnmtdDbContext.Create(ConnectionString(name));
        DatabaseMigrator.Migrate(db, NullLogger.Instance);
        return db;
    }
}
