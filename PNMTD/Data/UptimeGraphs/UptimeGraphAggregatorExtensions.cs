using PNMTD.Models.Db;

namespace PNMTD.Data.UptimeGraphs
{
    public static class UptimeGraphAggregatorExtensions
    {

        public static List<decimal> AggregateStatesForTimeperiodInDays(this SensorEntity sensor, PnmtdDbContext dbContext, int periodInDays = 30)
        {
            var latestPointInTime = DateTime.Now.Date - TimeSpan.FromDays(30);
            var events = dbContext.Events.Where(e => e.SensorId == sensor.Id && e.Created > latestPointInTime)
                .OrderByDescending(e => e.Created).ToList();
            var uptimeSpans = new List<UptimeAggregatedSpan>();
            var eventsEnumerator = events.GetEnumerator();
            for(int daysSinceNow = 0; daysSinceNow <= periodInDays; daysSinceNow++)
            {
                DateTime relevantDay = DateTime.Now.Date.AddDays(-daysSinceNow);
                do
                {
                    //eventsEnumerator.Current.IsSuccess
                } while (eventsEnumerator.Current.Created.Date == relevantDay && eventsEnumerator.MoveNext());
            }
            return null;
        
        }

    }
}
