using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Lib.Agent;
using PNMTD.Network;

namespace PNMTD.Data.CustomMigrations
{
    /// <summary>
    /// The servers' MACs for the network map (docs/network-map-plan.md §6.2), from the network sections stored before 1.17: each
    /// new section writes them itself. Runs while a network section has no rows; a server without a MAC costs one read per start.
    /// </summary>
    public class CustomMigration_AgentAdapterMacs : ICustomMigration
    {
        public bool ShouldRun(PnmtdDbContext dbContext) => Candidates(dbContext).Any();

        private static IQueryable<Guid> Candidates(PnmtdDbContext db) => db.AgentSections
            .Where(s => s.Section == SectionNames.Network && !db.AgentAdapterMacs.Any(m => m.SensorId == s.SensorId))
            .Select(s => s.SensorId);

        public void Run(PnmtdDbContext dbContext)
        {
            foreach (var sensorId in Candidates(dbContext).ToList())
            {
                var row = dbContext.AgentSections.AsNoTracking().Single(s => s.SensorId == sensorId && s.Section == SectionNames.Network);
                foreach (var mac in AgentMacIndex.Rows(sensorId, AgentSections.Parse<NetworkSection>(row.Content)))
                {
                    dbContext.AgentAdapterMacs.Add(mac);
                }
            }
            dbContext.SaveChanges();
        }
    }
}
