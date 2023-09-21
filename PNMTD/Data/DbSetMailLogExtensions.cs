using Microsoft.EntityFrameworkCore;
using PNMTD.Models.Db;

namespace PNMTD.Data
{
    public static class DbSetMailLogExtensions
    {

        public static void CleanUpMailLog(this DbSet<MailLogEntity> mailLogEntityDbSet)
        {
            var listOfMailLogEntriesToDelete = mailLogEntityDbSet.OrderByDescending(e => e.Created)
                .Skip(GlobalConfiguration.MAXIMUM_NUM_OF_MAILLOG_ENTRIES).ToList();

            mailLogEntityDbSet.RemoveRange(listOfMailLogEntriesToDelete);
        }

    }
}
