using PNMTD.Models.Poco.Extensions;

namespace PNMTD.Data.CustomMigrations
{
    public class CustomMigration_SetSecretReadToken : ICustomMigration
    {
        public bool ShouldRun(PnmtdDbContext dbContext)
        {
            return dbContext.Sensors.Any(s => s.SecretReadToken == null);
        }

        public void Run(PnmtdDbContext dbContext)
        {
            dbContext.Sensors.Where(s => s.SecretReadToken == null)
                .ToList()
                .ForEach(s => s.SetRandomSecretReadToken());

            dbContext.SaveChanges();
        }
    }
}
