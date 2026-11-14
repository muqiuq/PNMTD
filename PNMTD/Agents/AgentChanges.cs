using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Linux;
using PNMTD.Services.Events;
using PNMTD.Windows;

namespace PNMTD.Agents;

/// <summary>
/// A readable change line, and whether it is notable (253, 263). <see cref="Key"/>: the fingerprint of the SSH key it is about
/// (docs/linux-sensor-plan.md §6.3), kept in the details so a key's history finds it.
/// </summary>
public sealed record AgentChange(string Summary, bool Notable, object? Before = null, object? After = null, string? Key = null);

/// <summary>
/// Changes (docs/windows-sensor-plan.md §4.9; docs/linux-sensor-plan.md §5.8), of every platform: a section with a new hash
/// is compared with the stored one by its platform's differs (<see cref="WindowsDiffers"/>, <see cref="LinuxDiffers"/>), whose
/// readable lines go to agent_changes; notable ones write the platform's change code (253, 263) with a summary. A new
/// critical entry writes the platform's code for it (453 for the event log, 463 for the journal) when the server asks for it.
/// </summary>
public sealed class AgentChanges
{
    /// <summary>
    /// Called for every stored section: keeps the list's summaries current, and compares when there was one before.
    /// <paramref name="changed"/>: its hash was new (or it is the first). A Linux server's ssh-keys section also goes to the key
    /// manager (docs/linux-sensor-plan.md §6.3), the first included.
    /// </summary>
    public async Task StoredAsync(PnmtdDbContext db, AgentEntity agent, SectionMessage message, byte[]? previous, bool changed, CancellationToken ct)
    {
        if (agent.Platform == AgentPlatform.Windows)
        {
            WindowsDiffers.Summaries(agent, message);
        }
        else
        {
            LinuxDiffers.Summaries(agent, message, Clock.UtcNow);
        }
        var before = previous is null ? null : AgentSections.Decompress(previous);
        var keyChanges = agent.Platform == AgentPlatform.Linux && message.Name == SectionNames.SshKeys
            ? await KeysAsync(db, agent, message, before, changed, ct)
            : [];
        if (before is null && keyChanges.Count == 0)
        {
            return;
        }
        var platform = AgentPlatformInfo.Of(agent.Platform);
        var after = message.Data.GetRawText();
        var changes = (before is null ? [] : Compare(agent.Platform, message.Name, before, after)).Concat(keyChanges).ToList();
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
                Details = change.Before is null && change.After is null && change.Key is null ? null
                    : JsonSerializer.Serialize(new { change.Before, change.After, change.Key }, AgentJson.Options),
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
        if (write && agent.CriticalEvents && before is not null)
        {
            foreach (var entry in NewCritical(agent.Platform, message.Name, before, after).Take(5))
            {
                await SensorEventWriter.WriteAsync(db, agent.SensorId, platform.CriticalCode, Cuts.Cut(entry, 1000), platform.EventSource, ct);
            }
        }
    }

    /// <summary>
    /// The key manager's part (docs/linux-sensor-plan.md §6.3): every ssh-keys section with a new hash rebuilds where each key is
    /// authorized, and words the keys added and removed by their registration (465, 263); an unchanged one only while the server has
    /// no rows yet (a section stored before L5), quietly.
    /// </summary>
    private static async Task<List<AgentChange>> KeysAsync(PnmtdDbContext db, AgentEntity agent, SectionMessage message, string? before, bool changed,
        CancellationToken ct)
    {
        if (!changed && await db.SshKeyAuthorizations.AnyAsync(a => a.SensorId == agent.SensorId, ct))
        {
            return [];
        }
        var section = Read<SshKeysSection>(message.Data.GetRawText());
        if (section is null)
        {
            return [];
        }
        return await SshKeyManager.SshKeyAuthorizations.StoredAsync(db, agent, section, before is null ? null : Read<SshKeysSection>(before), !changed, Clock.UtcNow, ct);
    }

    private static T? Read<T>(string json) where T : class
    {
        try
        {
            return AgentJson.Deserialize<T>(json);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
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
