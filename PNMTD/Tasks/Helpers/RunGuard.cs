namespace PNMTD.Tasks.Helpers
{
    /// <summary>
    /// One run at a time: a timer callback that finds the previous run still going returns at once
    /// (v0.12's timers could overlap, and NotificationTask then sent notifications twice).
    /// </summary>
    public sealed class RunGuard
    {
        private int running;

        public bool TryEnter() => Interlocked.CompareExchange(ref running, 1, 0) == 0;

        public void Exit() => Volatile.Write(ref running, 0);
    }
}
