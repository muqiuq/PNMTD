using PNMTD.Models.Db;

namespace PNMTD.Data.UptimeGraphs
{
    public static class UptimeGraphAggregatorExtensions
    {

        public static List<FromToState> GetTimespansWithDayForPeriod(this SensorEntity sensor, PnmtdDbContext dbContext, int periodInDays = 90, bool useNoEventBefore = false)
        {
            var latestPointInTime = DateTime.Now.Date - TimeSpan.FromDays(periodInDays);
            var events = dbContext.Events.Where(e => e.SensorId == sensor.Id && e.Created >= latestPointInTime)
                .OrderBy(e => e.Created).ToList();
            var eventBefore = dbContext.Events.Where(e => e.SensorId == sensor.Id && e.Created < latestPointInTime).OrderByDescending(e => e.Created).Take(1).FirstOrDefault();
            if(useNoEventBefore)
            {
                eventBefore = null;
            }
            DateTime lastRelevantPointInTime = latestPointInTime.AddDays(-1);
            bool? lastState = null;
            if(eventBefore != null)
            {
                lastRelevantPointInTime = eventBefore.Created;
                lastState = eventBefore.IsSuccess;
            }
            var listOfTimeSpans = new List<FromToState>();
            foreach(var e in events)
            {
                var fromToS = new FromToState(lastRelevantPointInTime, e.Created, lastState);
                lastState = e.IsSuccess;
                lastRelevantPointInTime = e.Created;
                listOfTimeSpans.Add(fromToS);
            }
            var listOfTimeSpansSplitted = new List<FromToState>();
            foreach (var l in listOfTimeSpans)
            {
                if(l.From.Date != l.To.Date)
                {
                    var fi = new FromToState(l.From, l.From.Date.AddDays(1).AddSeconds(-1), l.State);
                    var fo = new FromToState(l.To.Date, l.To, l.State);
                    listOfTimeSpansSplitted.Add(fi);
                    var numOfDaysInBetween = (l.To.Date - l.From.Date).TotalDays - 1;
                    for(int a = 1; a <= numOfDaysInBetween; a++)
                    {
                        var today = fi.From.Date.AddDays(a);
                        listOfTimeSpansSplitted.Add(new FromToState(today, today.AddDays(1).AddSeconds(-1), l.State));
                    }
                    listOfTimeSpansSplitted.Add(fo);
                }
                else
                {
                    listOfTimeSpansSplitted.Add(l);
                }
            }
            return listOfTimeSpansSplitted;
        }

        public static List<UptimeAggregatedSpan> AggregateStatesForTimeperiodInDays(this SensorEntity sensor, PnmtdDbContext dbContext, int periodInDays = 90, bool useNoEventBefore = false)
        {
            var uptimeSpans = new List<UptimeAggregatedSpan>();
            var allFromToStates = GetTimespansWithDayForPeriod(sensor, dbContext, periodInDays, useNoEventBefore);
            bool hasStarted = false;
            bool lastState = false;
            for (int daysSinceNow = periodInDays; daysSinceNow >= 0; daysSinceNow--)
            {
                DateTime startOfRelevantDay = DateTime.Now.Date.AddDays(-daysSinceNow);
                DateTime endOfRelevantDay = startOfRelevantDay.AddDays(1).AddSeconds(-1);
                var allFromToStatesForToday = allFromToStates.Where(s => s.From.Date == startOfRelevantDay.Date).OrderBy(s => s.From).ToList();
                var sortedAndSumedByState = allFromToStatesForToday
                    .Where(i => i.State.HasValue)
                    .GroupBy(i => i.State.Value)
                    .ToDictionary(i => i.Key, i => i.Sum(x => x.Diff.TotalSeconds));
                
                if(!sortedAndSumedByState.Any())
                {
                    if(!hasStarted)
                    {
                        uptimeSpans.Add(new UptimeAggregatedSpan()
                        {
                            Day = startOfRelevantDay.Date,
                            Uptime = -1
                        });
                    }
                    else
                    {
                        uptimeSpans.Add(new UptimeAggregatedSpan()
                        {
                            Day = startOfRelevantDay.Date,
                            Uptime = lastState ? 1 : 0,
                        });
                    }
                }
                else
                {
                    decimal totalUpTime = 0;
                    hasStarted = true;
                    if (sortedAndSumedByState.ContainsKey(true))
                    {
                        totalUpTime = (decimal)sortedAndSumedByState[true];
                        lastState = true;
                    }
                    else
                    {
                        lastState = false;
                    }
                    uptimeSpans.Add(new UptimeAggregatedSpan()
                    {
                        Day = startOfRelevantDay.Date,
                        Uptime = Decimal.Abs(Decimal.Round((totalUpTime / (24 * 60 * 60 - 1)), 2))
                    });
                }
                
            }
            return uptimeSpans;
        }
    }
}
