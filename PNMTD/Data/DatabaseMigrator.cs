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

    /// <summary>
    /// Registered before every other hosted service, so they start on a migrated database. Then, on
    /// a new installation, the first admin (§4.8).
    /// </summary>
    public sealed class DatabaseMigrationService(IDbContextFactory<PnmtdDbContext> factory, IConfiguration configuration,
        IServiceProvider services, ILogger<DatabaseMigrationService> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            DatabaseMigrator.Migrate(db, logger);
            var (admin, generated) = await new Auth.UserAccounts(db).SeedAsync(configuration, cancellationToken);
            if (admin is not null)
            {
                if (generated is not null)
                {
                    // Once: Auth:InitialAdmin:Password was not set (deploy/init.sh sets it).
                    logger.LogWarning("Created the first admin {Username} with the password {Password}. Change it at the first sign-in.",
                        admin.Username, generated);
                }
                else
                {
                    logger.LogInformation("Created the first admin {Username} with Auth:InitialAdmin:Password.", admin.Username);
                }
            }
            // The item sensors follow Licenses__Enabled (docs/licences-plan.md §13.3): removed while off, made again when on.
            await Licenses.LicenseSensorSync.RunAsync(db, services, logger, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
