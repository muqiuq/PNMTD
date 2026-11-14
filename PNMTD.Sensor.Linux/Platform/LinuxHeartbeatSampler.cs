using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Service;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>
/// The heartbeat's values on Linux (docs/linux-sensor-plan.md §5.3, the end): CPU from /proc/stat's delta since the last one, memory
/// from <c>MemAvailable</c>, the fullest real file system, the sessions from logind, a pending reboot, the boot time (/proc/stat's
/// <c>btime</c>) and the last shutdown (from the journal, read once an hour: it doesn't change while the machine runs). Each value is
/// read on its own; one that fails is left out, never the heartbeat.
/// </summary>
internal sealed class LinuxHeartbeatSampler : IHeartbeatSampler
{
    private long lastIdle;
    private long lastTotal;
    private string? lastShutdown;
    private DateTimeOffset lastShutdownReadAt = DateTimeOffset.MinValue;

    public Heartbeat Sample(int configVersion, int problems, IReadOnlyDictionary<string, string> hashes)
    {
        var now = DateTimeOffset.UtcNow;
        var boot = Try(() => (DateTimeOffset?)LinuxFacts.BootTime()) ?? now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        double? cpu = null;
        if (Try(() => LinuxFacts.ParseCpu(File.ReadAllText("/proc/stat"))) is { } jiffies)
        {
            var (idle, total) = jiffies;
            if (lastTotal > 0 && total > lastTotal)
            {
                cpu = Math.Round(100.0 * (1 - (double)(idle - lastIdle) / (total - lastTotal)), 1);
            }
            lastIdle = idle;
            lastTotal = total;
        }
        var memory = Try(() => LinuxFacts.MemoryPercent(LinuxFacts.ParseMeminfo(File.ReadAllText("/proc/meminfo"))));
        var lowest = Try(() => LinuxFacts.RealMounts()
            .Select(m => (m.MountPoint, Usage: LinuxFacts.Usage(m.MountPoint)))
            .Where(m => m.Usage is not null)
            .Select(m => (Name: m.MountPoint, Free: m.Usage!.Value.FreePercent))
            .OrderBy(m => m.Free)
            .Cast<(string Name, double Free)?>()
            .FirstOrDefault());
        var sessions = Try(() => LinuxFacts.Sessions(boot)) ?? [];
        var rebootPending = Try(() => LinuxFacts.RebootReasons().Count > 0);
        if (now - lastShutdownReadAt > TimeSpan.FromHours(1))
        {
            lastShutdownReadAt = now;
            lastShutdown = Try(() => LinuxFacts.Describe(LinuxFacts.LastShutdown()));
        }
        return new Heartbeat(now, boot, cpu, memory, lowest?.Name, lowest?.Free, sessions.Take(20).ToList(), rebootPending, hashes, configVersion,
            AgentInfo.Version, problems, Tool.ReadValue("/proc/sys/kernel/hostname") ?? Environment.MachineName, lastShutdown);
    }

    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return default;
        }
    }
}
