using System.Text.Json;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Linux;
using PNMTD.Services.Events;
using PNMTD.Windows;

namespace PNMTD.Agents;

/// <summary>A readable change line, and whether it is notable (253, 263).</summary>
public sealed record AgentChange(string Summary, bool Notable, object? Before = null, object? After = null);

/// <summary>
/// Changes (docs/windows-sensor-plan.md §4.9; docs/linux-sensor-plan.md §5.8), of every platform: a section with a new hash
/// is compared with the stored one by its platform's differs (<see cref="WindowsDiffers"/>, <see cref="LinuxDiffers"/>), whose
/// readable lines go to agent_changes; notable ones write the platform's change code (253, 263) with a summary. A new
/// critical entry writes the platform's code for it (453 for the event log, 463 for the journal) when the server asks for it.
/// </summary>
public sealed class AgentChanges
{
    /// <summary>Called for every stored section: keeps the list's summaries current, and compares when there was one before.</summary>
    public async Task StoredAsync(PnmtdDbContext db, AgentEntity agent, SectionMessage message, byte[]? previous, CancellationToken ct)
    {
        if (agent.Platform == AgentPlatform.Windows)
        {
            WindowsDiffers.Summaries(agent, message);
        }
        else
        {
            LinuxDiffers.Summaries(agent, message, Clock.UtcNow);
        }
        // L5 (docs/linux-sensor-plan.md §6.3): the ssh-keys section goes to the key manager here, for every stored section with a
        // new hash, the first included: SshKeyAuthorizations (where each key is authorized, 465 for keys not registered as active,
        // 263 for registered ones). LinuxDiffers.SshKeyChanges is its differ.
        if (previous is null)
        {
            return;
        }
        var platform = AgentPlatformInfo.Of(agent.Platform);
        var before = AgentSections.Decompress(previous);
        var after = message.Data.GetRawText();
        var changes = Compare(agent.Platform, message.Name, before, after);
        var now = Clock.UtcNow;
        foreach (var change in changes.Take(200))
        {
            db.AgentChanges.Add(new AgentChangeEntity
            {
                Id = Guid.NewGuid(),
                SensorId = agent.SensorId,
                Section = message.Name,
                At = now,
                Summary = Cuts.Cut(change.Summary, 1000),
                Details = change.Before is null && change.After is null ? null : JsonSerializer.Serialize(new { change.Before, change.After }, AgentJson.Options),
                Notable = change.Notable,
            });
        }
        var write = agent.Sensor.Enabled && agent.Sensor.Parent.Enabled;
        var notable = changes.Where(c => c.Notable).ToList();
        if (write && notable.Count > 0)
        {
            var summary = notable.Count == 1 ? notable[0].Summary : $"{notable.Count} changes: {string.Join("; ", notable.Select(c => c.Summary))}";
            await SensorEventWriter.WriteAsync(db, agent.SensorId, platform.ChangedCode, Cuts.Cut(summary, 1000), platform.EventSource, ct);
        }
        if (write && agent.CriticalEvents)
        {
            foreach (var entry in NewCritical(agent.Platform, message.Name, before, after).Take(5))
            {
                await SensorEventWriter.WriteAsync(db, agent.SensorId, platform.CriticalCode, Cuts.Cut(entry, 1000), platform.EventSource, ct);
            }
        }
    }

    /// <summary>The readable changes between two sections of a platform.</summary>
    public static IReadOnlyList<AgentChange> Compare(AgentPlatform platform, string section, string before, string after) => platform switch
    {
        AgentPlatform.Windows => WindowsDiffers.Compare(section, before, after),
        AgentPlatform.Linux => LinuxDiffers.Compare(section, before, after),
        _ => [],
    };

    /// <summary>
    /// The new critical entries of a log section, as the events' messages: Windows' event log (453), Linux's journal at emerg, alert
    /// and crit (463).
    /// </summary>
    private static IEnumerable<string> NewCritical(AgentPlatform platform, string section, string before, string after) => (platform, section) switch
    {
        (AgentPlatform.Windows, SectionNames.EventLog) => WindowsDiffers.NewCritical(before, after).Select(e => $"{e.Source} {e.EventId}: {e.Message}"),
        (AgentPlatform.Linux, SectionNames.Journal) => LinuxDiffers.NewCritical(before, after),
        _ => [],
    };
}
