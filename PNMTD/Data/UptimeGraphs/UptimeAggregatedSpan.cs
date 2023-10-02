namespace PNMTD.Data.UptimeGraphs
{
    public class UptimeAggregatedSpan
    {

        public DateTime TimePeriodBegin { get; set; }

        public TimeSpan Duration { get; set; }

        public decimal Uptime { get; set; }

        public UptimeAggregatedSpan() { }

    }
}
