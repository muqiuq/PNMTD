using Microsoft.EntityFrameworkCore;

namespace PNMTD.Data
{
    /// <summary>
    /// Brings the database to this version's schema: at startup (<see cref="DatabaseMigrationService"/>)
    /// and from `pnmtd db migrate`. A database that a newer version migrated is refused: going back
    /// means restoring the backup taken before the update (deploy/update.sh takes one).
    /// </summary>
    public static class DatabaseMigrator
    {
        public static void Migrate(PnmtdDbContext db, ILogger logger)
        {
            var known = db.Database.GetMigrations().ToHashSet();
            var unknown = db.Database.GetAppliedMigrations().Where(m => !known.Contains(m)).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The database was migrated by a newer PNMTD ({string.Join(", ", unknown)}). " +
                    "Run that version, or restore the backup taken before the update.");
            }
            var pending = db.Database.GetPendingMigrations().ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                db.Database.Migrate();
            }
            db.RunCustomMigrations();
        }
    }

    /// <summary>Registered before every other hosted service, so they start on a migrated database.</summary>
    public sealed class DatabaseMigrationService(IDbContextFactory<PnmtdDbContext> factory, ILogger<DatabaseMigrationService> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            using var db = factory.CreateDbContext();
            DatabaseMigrator.Migrate(db, logger);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
