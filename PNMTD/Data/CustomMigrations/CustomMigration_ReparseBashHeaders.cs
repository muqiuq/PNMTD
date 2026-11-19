using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Data.CustomMigrations
{
    /// <summary>
    /// Bash headers read again once (docs/connectors-plan.md §8.3): a parse from before 1.15 has no <c>functions</c>, and PNMTD reads bash
    /// headers itself, so the library lists a script's functions without anyone saving it again. The agents' findings stay.
    /// </summary>
    public class CustomMigration_ReparseBashHeaders : ICustomMigration
    {
        public bool ShouldRun(PnmtdDbContext dbContext) => Candidates(dbContext).Any();

        private static IQueryable<AgentScriptVersionEntity> Candidates(PnmtdDbContext db) => db.AgentScriptVersions
            .Where(v => v.Parsed != null && !EF.Functions.JsonExists(v.Parsed, "functions"))
            .Join(db.AgentScripts.Where(s => s.Language == ScriptLanguage.Bash), v => v.ScriptId, s => s.Id, (v, _) => v);

        public void Run(PnmtdDbContext dbContext)
        {
            foreach (var version in Candidates(dbContext).ToList())
            {
                var before = JsonSerializer.Deserialize<ParsedScript>(version.Parsed!, AgentJson.Options);
                var header = BashScriptHeader.Parse(version.Text);
                version.Parsed = JsonSerializer.Serialize(AgentScriptResults.Capped(AgentScriptResults.Merge(header, before)), AgentJson.Options);
            }
            dbContext.SaveChanges();
        }
    }
}
