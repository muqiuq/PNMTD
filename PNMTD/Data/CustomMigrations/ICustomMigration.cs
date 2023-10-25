namespace PNMTD.Data.CustomMigrations
{
    public interface ICustomMigration
    {

        public bool ShouldRun(PnmtdDbContext dbContext);

        public void Run(PnmtdDbContext dbContext);

    }
}
