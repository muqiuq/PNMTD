using System.Text.Json;
using PNMTD.Agents;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Windows;

/// <summary>
/// The Windows differs (docs/windows-sensor-plan.md §4.9), moved out of WindowsChanges in L1 (docs/linux-sensor-plan.md
/// §3.3) and unchanged: <see cref="AgentChanges"/> stores and dispatches, these compare a Windows section with the one it
/// replaces. Notable (253): services added, removed or with a new start type or account; any change to remote access; the
/// local administrators; addresses, gateways or DNS servers; the OS build; disks or memory; the audit policy; scripts
/// registered, replaced or removed in the agent's window. Logins and event log entries are logs, not changes; a new Critical
/// entry writes 453 when the server asks for it (§4.8).
/// </summary>
public static class WindowsDiffers
{
    /// <summary>Keeps the list's summaries current from every stored section: the remote access open, the audit gaps.</summary>
    public static void Summaries(AgentEntity agent, SectionMessage message)
    {
        if (message.Name == SectionNames.RemoteAccess && AgentJson.Deserialize<RemoteAccessSection>(message.Data) is { } remote)
        {
            agent.RemoteAccessSummary = remote.Summary == "none open" ? null : Cuts.Cut(remote.Summary, 300);
        }
        if (message.Name == SectionNames.Access && AgentJson.Deserialize<AccessSection>(message.Data) is { } access)
        {
            agent.AuditGaps = access.Gaps.Count;
        }
    }

    public static IReadOnlyList<EventEntry> NewCritical(string before, string after)
    {
        var old = Parse<EventLogSection>(before)?.Entries.Select(e => (e.Log, e.RecordId)).ToHashSet() ?? [];
        var newest = old.Count == 0 ? DateTimeOffset.MinValue : Parse<EventLogSection>(before)!.Entries.Max(e => e.At);
        return Parse<EventLogSection>(after)?.Entries.Where(e => e.Level == "Critical" && !old.Contains((e.Log, e.RecordId)) && e.At >= newest).ToList() ?? [];
    }

    public static IReadOnlyList<AgentChange> Compare(string section, string before, string after) => section switch
    {
        SectionNames.System => SystemChanges(Parse<SystemSection>(before), Parse<SystemSection>(after)),
        SectionNames.Hardware => HardwareChanges(Parse<HardwareSection>(before), Parse<HardwareSection>(after)),
        SectionNames.Network => NetworkChanges(Parse<NetworkSection>(before), Parse<NetworkSection>(after)),
        SectionNames.Services => ServiceChanges(Parse<ServicesSection>(before), Parse<ServicesSection>(after)),
        SectionNames.RemoteAccess => RemoteChanges(Parse<RemoteAccessSection>(before), Parse<RemoteAccessSection>(after)),
        SectionNames.Access => AccessChanges(Parse<AccessSection>(before), Parse<AccessSection>(after)),
        SectionNames.Scripts => ScriptChanges(Parse<ScriptsSection>(before), Parse<ScriptsSection>(after)),
        _ => [],
    };

    private static T? Parse<T>(string json) where T : class
    {
        try
        {
            return AgentJson.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // --- the differs ------------------------------------------------------------------------------------------

    private static List<AgentChange> SystemChanges(SystemSection? a, SystemSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        var buildA = a.Os.Ubr is { } ua ? $"{a.Os.Build}.{ua}" : a.Os.Build;
        var buildB = b.Os.Ubr is { } ub ? $"{b.Os.Build}.{ub}" : b.Os.Build;
        if (buildA != buildB || a.Os.Caption != b.Os.Caption)
        {
            changes.Add(new($"Windows updated: build {buildA} → {buildB}{(a.Os.Caption != b.Os.Caption ? $" ({b.Os.Caption})" : "")}", true, buildA, buildB));
        }
        if (!string.Equals(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase) || a.PartOfDomain != b.PartOfDomain)
        {
            changes.Add(new(b.PartOfDomain ? $"Joined the domain {b.Domain}" : $"Left the domain {a.Domain} (workgroup {b.Domain})", true, a.Domain, b.Domain));
        }
        Sets(changes, a.LocalAdministrators.Select(x => x.Name), b.LocalAdministrators.Select(x => x.Name),
            n => $"New local administrator {n}", n => $"Local administrator {n} removed", notable: true);
        foreach (var profile in b.Security?.Firewall ?? [])
        {
            var old = a.Security?.Firewall.FirstOrDefault(p => p.Profile == profile.Profile);
            if (old is not null && old.Enabled != profile.Enabled)
            {
                changes.Add(new($"Firewall {profile.Profile} profile turned {(profile.Enabled ? "on" : "off")}", false));
            }
        }
        if (a.Security?.Defender?.RealTimeProtection is { } rta && b.Security?.Defender?.RealTimeProtection is { } rtb && rta != rtb)
        {
            changes.Add(new($"Defender real-time protection turned {(rtb ? "on" : "off")}", false));
        }
        foreach (var volume in b.Security?.BitLocker ?? [])
        {
            var old = a.Security?.BitLocker.FirstOrDefault(v => v.Volume == volume.Volume);
            if (old is not null && old.Protection != volume.Protection)
            {
                changes.Add(new($"BitLocker on {volume.Volume}: {old.Protection} → {volume.Protection}", false));
            }
        }
        return changes;
    }

    private static List<AgentChange> HardwareChanges(HardwareSection? a, HardwareSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        if (Gib(a.Memory.TotalBytes) != Gib(b.Memory.TotalBytes))
        {
            changes.Add(new($"Memory {Gib(a.Memory.TotalBytes)} GiB → {Gib(b.Memory.TotalBytes)} GiB", true));
        }
        static string Disk(DiskInfo d) => $"{d.Model} ({(d.SizeBytes is { } s ? Gib(s) + " GiB" : "?")}{(d.SerialNumber is { } serial ? ", " + serial : "")})";
        Sets(changes, a.Disks.Select(Disk), b.Disks.Select(Disk), d => $"Disk added: {d}", d => $"Disk removed: {d}", notable: true);
        foreach (var disk in b.Disks)
        {
            var old = a.Disks.FirstOrDefault(d => Disk(d) == Disk(disk));
            if (old is not null && old.Health != disk.Health)
            {
                changes.Add(new($"Disk {Disk(disk)}: {old.Health} → {disk.Health}", true));
            }
            if (old is not null && old.PredictFailure != true && disk.PredictFailure == true)
            {
                changes.Add(new($"Disk {Disk(disk)}: SMART predicts a failure", true));
            }
        }
        Sets(changes, a.Volumes.Select(v => v.Mount), b.Volumes.Select(v => v.Mount), v => $"Volume {v} added", v => $"Volume {v} removed", notable: false);
        return changes;
    }

    private static long Gib(long bytes) => (long)Math.Round(bytes / (1024.0 * 1024 * 1024));

    private static List<AgentChange> NetworkChanges(NetworkSection? a, NetworkSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        Sets(changes, a.Adapters.Select(x => x.Name), b.Adapters.Select(x => x.Name), n => $"Network adapter {n} added", n => $"Network adapter {n} removed", notable: true);
        foreach (var adapter in b.Adapters)
        {
            var old = a.Adapters.FirstOrDefault(x => x.Name == adapter.Name);
            if (old is null)
            {
                continue;
            }
            var removed = old.Addresses.Except(adapter.Addresses).Where(x => !x.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase)).ToList();
            var added = adapter.Addresses.Except(old.Addresses).Where(x => !x.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase)).ToList();
            if (removed.Count == 1 && added.Count == 1)
            {
                changes.Add(new($"{removed[0]} → {added[0]} on {adapter.Name}", true, removed, added));
            }
            else
            {
                changes.AddRange(added.Select(x => new AgentChange($"Address {x} added on {adapter.Name}", true)));
                changes.AddRange(removed.Select(x => new AgentChange($"Address {x} removed from {adapter.Name}", true)));
            }
            if (!old.Gateways.Order().SequenceEqual(adapter.Gateways.Order()))
            {
                changes.Add(new($"Gateway on {adapter.Name}: {List(old.Gateways)} → {List(adapter.Gateways)}", true));
            }
            if (!old.DnsServers.SequenceEqual(adapter.DnsServers))
            {
                changes.Add(new($"DNS servers on {adapter.Name}: {List(old.DnsServers)} → {List(adapter.DnsServers)}", true));
            }
        }
        static string Listener(ListenerInfo l) => $"{l.Port}/{l.Protocol}";
        var oldListeners = a.Listeners.Select(Listener).ToHashSet();
        foreach (var listener in b.Listeners.Where(l => !oldListeners.Contains(Listener(l))).DistinctBy(Listener))
        {
            changes.Add(new($"Port {Listener(listener)} now listens ({listener.Process ?? "?"}{(listener.Services.Count > 0 ? ": " + string.Join(", ", listener.Services) : "")})", false));
        }
        var newListeners = b.Listeners.Select(Listener).ToHashSet();
        foreach (var listener in a.Listeners.Where(l => !newListeners.Contains(Listener(l))).DistinctBy(Listener))
        {
            changes.Add(new($"Port {Listener(listener)} no longer listens", false));
        }
        if (a.Proxy != b.Proxy)
        {
            changes.Add(new($"WinHTTP proxy: {a.Proxy ?? "direct"} → {b.Proxy ?? "direct"}", false));
        }
        return changes;
    }

    private static string List(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(", ", values);

    private static string ClassLabel(ServiceClass c) => c switch
    {
        ServiceClass.ThirdParty => "third-party",
        ServiceClass.Pnmt => "PNMT",
        _ => c.ToString().ToLowerInvariant(),
    };

    private static List<AgentChange> ServiceChanges(ServicesSection? a, ServicesSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        var old = a.Services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var current = b.Services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var service in b.Services.Where(s => !old.ContainsKey(s.Name)))
        {
            changes.Add(new($"Service {service.DisplayName} added ({ClassLabel(service.Class)}, {service.StartType.ToLowerInvariant()}, {service.State.ToLowerInvariant()})", true));
        }
        foreach (var service in a.Services.Where(s => !current.ContainsKey(s.Name)))
        {
            changes.Add(new($"Service {service.DisplayName} removed", true));
        }
        foreach (var service in b.Services)
        {
            if (!old.TryGetValue(service.Name, out var before))
            {
                continue;
            }
            if (before.StartType != service.StartType)
            {
                changes.Add(new($"Service {service.DisplayName}: start type {before.StartType} → {service.StartType}", true));
            }
            if (!string.Equals(before.Account, service.Account, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new($"Service {service.DisplayName}: account {before.Account ?? "–"} → {service.Account ?? "–"}", true));
            }
            if (before.Class != service.Class)
            {
                changes.Add(new($"Service {service.DisplayName}: now {ClassLabel(service.Class)} (was {ClassLabel(before.Class)})", service.Class is ServiceClass.Unsigned or ServiceClass.ThirdParty));
            }
            if (!string.Equals(before.Path, service.Path, StringComparison.OrdinalIgnoreCase) || !string.Equals(before.ServiceDll, service.ServiceDll, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new($"Service {service.DisplayName}: binary {service.ServiceDll ?? service.Path}", false, before.ServiceDll ?? before.Path, service.ServiceDll ?? service.Path));
            }
        }
        return changes;
    }

    /// <summary>Scripts registered in the agent's window (§7.2): what PNMT may run there, and as whom. Task runs are not changes.</summary>
    private static List<AgentChange> ScriptChanges(ScriptsSection? a, ScriptsSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        static string Mode(ScriptsSection s) => !s.Allowed ? "not allowed" : s.AnyScript ? "any script" : "registered scripts only";
        if (Mode(a) != Mode(b))
        {
            changes.Add(new($"Scripts from PNMT: {Mode(a)} → {Mode(b)}", true));
        }
        static string Who(RegisteredScript s) => s.RunAs == RunAsKind.Service ? "this service" : $"{s.Account}";
        var old = a.Scripts.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var current = b.Scripts.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var script in current.Values.Where(s => !old.ContainsKey(s.Name)))
        {
            changes.Add(new($"Script {script.Name} registered, as {Who(script)}{(script.Schedule is { } when ? $", {when}" : "")}", true));
        }
        foreach (var script in old.Values.Where(s => !current.ContainsKey(s.Name)))
        {
            changes.Add(new($"Script {script.Name} removed", true));
        }
        foreach (var script in current.Values)
        {
            if (!old.TryGetValue(script.Name, out var before))
            {
                continue;
            }
            if (before.Sha256 != script.Sha256)
            {
                changes.Add(new($"Script {script.Name} replaced ({before.Sha256[..12]} → {script.Sha256[..12]})", true, before.Sha256, script.Sha256));
            }
            if (before.RunAs != script.RunAs || !string.Equals(before.Account, script.Account, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new($"Script {script.Name}: runs as {Who(script)} (was {Who(before)})", true));
            }
            if (before.Schedule != script.Schedule)
            {
                changes.Add(new($"Script {script.Name}: schedule {before.Schedule ?? "none"} → {script.Schedule ?? "none"}", false));
            }
            if (before.Matches && !script.Matches)
            {
                changes.Add(new($"Script {script.Name}: its copy was changed after the registration", true));
            }
        }
        return changes;
    }

    private static List<AgentChange> RemoteChanges(RemoteAccessSection? a, RemoteAccessSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        foreach (var feature in b.Features)
        {
            var old = a.Features.FirstOrDefault(f => f.Key == feature.Key);
            var ports = feature.Ports.Count > 0 ? $"port {string.Join(", ", feature.Ports)}" : null;
            var notes = string.Join(", ", new[] { ports }.Concat(feature.Notes).Where(n => !string.IsNullOrEmpty(n)));
            if (old is null)
            {
                if (feature.Active)
                {
                    changes.Add(new($"{feature.Name} enabled{(notes.Length > 0 ? $" ({notes})" : "")}", true));
                }
                continue;
            }
            if (old.Active != feature.Active)
            {
                changes.Add(new($"{feature.Name} {(feature.Active ? "enabled" : "disabled")}{(feature.Active && notes.Length > 0 ? $" ({notes})" : "")}", true));
            }
            else if (feature.Active && old.FirewallOpen != feature.FirewallOpen)
            {
                changes.Add(new($"{feature.Name}: the firewall {(feature.FirewallOpen ? $"now lets it in ({string.Join(", ", feature.FirewallProfiles)})" : "no longer lets it in")}", true));
            }
            if (feature.Active && !old.Ports.SequenceEqual(feature.Ports))
            {
                changes.Add(new($"{feature.Name}: port {string.Join(", ", old.Ports)} → {string.Join(", ", feature.Ports)}", true));
            }
            foreach (var note in feature.Notes.Except(old.Notes).Where(n => !n.StartsWith("shares:", StringComparison.Ordinal)))
            {
                changes.Add(new($"{feature.Name}: {note}", true));
            }
        }
        foreach (var gone in a.Features.Where(f => f.Active && b.Features.All(n => n.Key != f.Key)))
        {
            changes.Add(new($"{gone.Name} removed", true));
        }
        return changes;
    }

    private static List<AgentChange> AccessChanges(AccessSection? a, AccessSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        foreach (var setting in b.AuditPolicy)
        {
            var old = a.AuditPolicy.FirstOrDefault(s => s.Guid == setting.Guid);
            if (old is null)
            {
                continue;
            }
            if (old.Success != setting.Success)
            {
                changes.Add(new($"{setting.Subcategory} success auditing turned {(setting.Success ? "on" : "off")}", true));
            }
            if (old.Failure != setting.Failure)
            {
                changes.Add(new($"{setting.Subcategory} failure auditing turned {(setting.Failure ? "on" : "off")}", true));
            }
        }
        foreach (var log in b.Logs)
        {
            var old = a.Logs.FirstOrDefault(l => l.Name == log.Name);
            if (old is not null && old.Enabled != log.Enabled)
            {
                changes.Add(new($"{log.Name} turned {(log.Enabled ? "on" : "off")}", true));
            }
        }
        return changes;
    }

    private static void Sets(List<AgentChange> changes, IEnumerable<string> before, IEnumerable<string> after, Func<string, string> added, Func<string, string> removed, bool notable)
    {
        var a = before.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = after.ToHashSet(StringComparer.OrdinalIgnoreCase);
        changes.AddRange(b.Except(a, StringComparer.OrdinalIgnoreCase).Order().Select(x => new AgentChange(added(x), notable)));
        changes.AddRange(a.Except(b, StringComparer.OrdinalIgnoreCase).Order().Select(x => new AgentChange(removed(x), notable)));
    }
}
