using PNMTD.Data.CustomMigrations;

namespace PNMTD.Data
{
    public static class CustomMigration
    {
        public static void RunCustomMigrations(this PnmtdDbContext dbContext)
        {
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(s => s.GetTypes())
                .Where(p => typeof(ICustomMigration).IsAssignableFrom(p));

            var migrations = types
                .Where(t => t.IsClass && t.Namespace == "PNMTD.Data.CustomMigrations" && t.GetInterfaces().Contains(typeof(ICustomMigration)))
                .ToList();

            foreach (var migration in migrations)
            {
                var migrationInstance = (ICustomMigration) Activator.CreateInstance(migration);
                if (migrationInstance.ShouldRun(dbContext))
                {
                    migrationInstance.Run(dbContext);
                }
            }
        }
    }
}
