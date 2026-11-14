using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Core.Service;

/// <summary>
/// The heartbeat's values (docs/windows-sensor-plan.md §3.5; docs/linux-sensor-plan.md §5.3, the same record): CPU since the last one,
/// memory, the fullest volume, sessions, a pending reboot, the boot time and the last shutdown. Each platform reads its own: Windows'
/// system times, WTS and the registry; Linux's <c>/proc</c>, logind and the journal.
/// </summary>
public interface IHeartbeatSampler
{
    Heartbeat Sample(int configVersion, int problems, IReadOnlyDictionary<string, string> hashes);
}
