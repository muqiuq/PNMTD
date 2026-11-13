using PNMTD.Tasks.Helpers;
using PNMTD.UplinkCheck;

namespace PNMTD.Tasks
{
    /// <summary>
    /// Runs <see cref="UplinkMonitor"/> every 120 s (docs/ai-operations-plan.md §7). The check itself, its record and
    /// the uplink's state are the monitor's, so that <c>POST /api/v1/uplink/check</c> runs the same.
    /// </summary>
    public sealed class UplinkCheckTask(UplinkMonitor monitor, ILogger<UplinkCheckTask> logger) : IHostedService, IDisposable
    {
        private Timer? timer;
        private readonly RunGuard runGuard = new();
        private readonly CancellationTokenSource stopping = new();

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Starting UplinkCheckTask");
            await monitor.LoadAsync(cancellationToken);
            if (!monitor.Configured)
            {
                logger.LogWarning("The uplink check needs UplinkCheck:SharedKey and at least two of UplinkCheck:Url1..3; it does not run");
                return;
            }
            logger.LogInformation("UplinkCheckTask is running with {Count} endpoints", monitor.Endpoints.Count);
            timer = new Timer(_ => Run(), null, TimeSpan.Zero, TimeSpan.FromSeconds(UplinkMonitor.IntervalSeconds));
        }

        private void Run()
        {
            if (!runGuard.TryEnter())
            {
                return;
            }
            try
            {
                monitor.CheckAsync(stopping.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "UplinkCheckTask");
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                runGuard.Exit();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("UplinkCheckTask is stopping.");
            timer?.Change(Timeout.Infinite, 0);
            stopping.Cancel();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            timer?.Dispose();
            stopping.Dispose();
        }
    }
}
