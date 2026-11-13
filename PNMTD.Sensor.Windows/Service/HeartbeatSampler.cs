using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Collectors;
using PNMTD.Sensor.Windows.Native;

namespace PNMTD.Sensor.Windows.Service;

/// <summary>
/// The heartbeat's values (docs/windows-sensor-plan.md §3.5): CPU since the last one, memory, the fullest volume, sessions,
/// a pending reboot, the boot time and the last shutdown. About 500 bytes.
/// </summary>
internal sealed class HeartbeatSampler
{
    private long lastIdle;
    private long lastTotal;
    private string? lastShutdown;
    private DateTimeOffset lastShutdownReadAt = DateTimeOffset.MinValue;

    public Heartbeat Sample(int configVersion, int problems, IReadOnlyDictionary<string, string> hashes)
    {
        var now = DateTimeOffset.UtcNow;
        var boot = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        double? cpu = null;
        if (Win32.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Kernel time includes idle time.
            var total = kernel + user;
            if (lastTotal > 0 && total > lastTotal)
            {
                cpu = Math.Round(100.0 * (1 - (double)(idle - lastIdle) / (total - lastTotal)), 1);
            }
            lastIdle = idle;
            lastTotal = total;
        }
        var memory = Win32.Memory();
        (string Name, double Free)? lowest = null;
        try
        {
            lowest = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady && d.TotalSize > 0)
                .Select(d => (Name: d.Name.TrimEnd('\\'), Free: Math.Round(100.0 * d.TotalFreeSpace / d.TotalSize, 1)))
                .OrderBy(d => d.Free).FirstOrDefault();
        }
        catch (IOException)
        {
        }
        List<SessionInfo> sessions;
        try
        {
            sessions = SystemCollector.Sessions();
        }
        catch (Exception)
        {
            sessions = [];
        }
        var rebootPending = false;
        try
        {
            rebootPending = SystemCollector.RebootReasons().Count > 0;
        }
        catch (Exception)
        {
        }
        if (now - lastShutdownReadAt > TimeSpan.FromHours(1))
        {
            lastShutdownReadAt = now;
            try
            {
                var shutdown = SystemCollector.LastShutdown();
                lastShutdown = shutdown is null ? null
                    : shutdown.Unexpected ? $"unexpected shutdown before {shutdown.At:yyyy-MM-dd HH:mm}"
                    : $"shut down by {shutdown.User ?? "?"}: {shutdown.Reason}";
            }
            catch (Exception)
            {
                lastShutdown = null;
            }
        }
        return new Heartbeat(now, boot, cpu, memory.MemoryLoad, lowest?.Name, lowest?.Free, sessions.Take(20).ToList(), rebootPending, hashes,
            configVersion, AgentInfo.Version, problems, Environment.MachineName, lastShutdown);
    }
}
