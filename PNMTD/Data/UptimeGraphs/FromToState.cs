namespace PNMTD.Data.UptimeGraphs
{
    public class FromToState
    {
        public FromToState(DateTime from, DateTime to, bool? state)
        {
            From = from;
            To = to;
            State = state;
        }

        public DateTime From { get; set; }

        public DateTime To { get; set; }

        public TimeSpan Diff
        {
            get
            {
                return To - From;
            }
        }

        public bool? State { get; set; }

    }
}
