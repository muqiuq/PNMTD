using System.Text.Json;
using PNMTD.Agents;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;
using PNMTD.Windows;

namespace PNMTD.Linux;

/// <summary>
/// The Linux differs (docs/linux-sensor-plan.md §5.8): <see cref="AgentChanges"/> stores and dispatches, these compare a Linux
/// section with the one it replaces. Notable (263): services, timers and cron entries added or removed, or with a new start
/// state, class or account; any change to remote access or sshd's settings; local administrators and sudoers rules; accounts
/// that can log in with a password; addresses, gateways or DNS servers; the distribution's point release and the kernel; disks
/// or memory (the shared records, compared as on Windows); the firewall's state or policy, and AppArmor; the logging check.
/// Logins and journal entries are logs, not changes; a new entry at emerg, alert or crit writes 463 when the server asks for it
/// (§5.7). The ssh-keys section is compared by L5 (<see cref="SshKeyChanges"/>).
/// </summary>
public static class LinuxDiffers
{
    /// <summary>
    /// Keeps the agent row current from every stored section (§9.3): the kernel, the distribution and the pending security updates
    /// for the list and 462, the remote access open, and the logging check's gaps.
    /// </summary>
    public static void Summaries(AgentEntity agent, SectionMessage message, DateTime now)
    {
        switch (message.Name)
        {
            case SectionNames.System when Parse<LinuxSystemSection>(message.Data) is { } system:
                agent.KernelRelease = Cuts.CutOrNull(system.Os.KernelRelease, 100) ?? agent.KernelRelease;
                agent.Distribution = Cuts.CutOrNull(system.Os.Distribution, 200) ?? Cuts.CutOrNull(system.Os.PrettyName, 200) ?? agent.Distribution;
                if (system.Updates is { } updates)
                {
                    agent.SecurityUpdates = Math.Max(0, updates.Security);
                    agent.SecurityUpdatesSince = updates.Security > 0 ? agent.SecurityUpdatesSince ?? now : null;
                    agent.SecurityPackages = updates.Security > 0 && updates.SecurityPackages.Count > 0
                        ? Cuts.Cut(string.Join(", ", updates.SecurityPackages.Take(8)) + (updates.SecurityPackages.Count > 8 || updates.Security > 8 ? ", …" : ""), 500)
                        : null;
                }
                break;
            case SectionNames.RemoteAccess when Parse<LinuxRemoteAccessSection>(message.Data) is { } remote:
                agent.RemoteAccessSummary = string.IsNullOrWhiteSpace(remote.Summary) || remote.Summary is "none" or "none open" ? null : Cuts.Cut(remote.Summary, 300);
                break;
            case SectionNames.Access when Parse<LinuxAccessSection>(message.Data) is { } access:
                agent.AuditGaps = access.Gaps.Count;
                break;
        }
    }

    /// <summary>New journal entries at emerg, alert or crit (priority 2 or less, §5.7), as the events' messages: "nginx.service: …".</summary>
    public static IReadOnlyList<string> NewCritical(string before, string after)
    {
        var old = Parse<JournalSection>(before);
        var seen = old?.Entries.Select(e => e.Cursor).ToHashSet(StringComparer.Ordinal) ?? [];
        var newest = old is { Entries.Count: > 0 } ? old.Entries.Max(e => e.At) : DateTimeOffset.MinValue;
        return Parse<JournalSection>(after)?.Entries
            .Where(e => e.Priority <= 2 && !seen.Contains(e.Cursor) && e.At >= newest)
            .OrderBy(e => e.At)
            .Select(e => $"{e.Unit ?? e.Identifier ?? "journal"} ({e.Level}): {e.Message}")
            .ToList() ?? [];
    }

    public static IReadOnlyList<AgentChange> Compare(string section, string before, string after) => section switch
    {
        SectionNames.System => SystemChanges(Parse<LinuxSystemSection>(before), Parse<LinuxSystemSection>(after)),
        SectionNames.Hardware => WindowsDiffers.HardwareChanges(Parse<HardwareSection>(before), Parse<HardwareSection>(after)),
        SectionNames.Network => WindowsDiffers.NetworkChanges(Parse<NetworkSection>(before), Parse<NetworkSection>(after), "Proxy"),
        SectionNames.Services => ServiceChanges(Parse<LinuxServicesSection>(before), Parse<LinuxServicesSection>(after)),
        SectionNames.RemoteAccess => RemoteChanges(Parse<LinuxRemoteAccessSection>(before), Parse<LinuxRemoteAccessSection>(after)),
        SectionNames.Access => AccessChanges(Parse<LinuxAccessSection>(before), Parse<LinuxAccessSection>(after)),
        SectionNames.SshKeys => SshKeyChanges(Parse<SshKeysSection>(before), Parse<SshKeysSection>(after)),
        _ => [],
    };

    /// <summary>
    /// The ssh-keys section (§6.3): <b>L5's</b>. Keys added and removed write 263 when they are registered as active and 465 when
    /// they aren't (by the key manager, which needs the database: <c>SshKeyAuthorizations</c>, called from
    /// <see cref="AgentChanges.StoredAsync"/>), and key files' permissions are notable. Until then the section is stored and
    /// served, and compared by nobody.
    /// </summary>
    public static List<AgentChange> SshKeyChanges(SshKeysSection? a, SshKeysSection? b) => [];

    private static T? Parse<T>(string json) where T : class
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

    private static T? Parse<T>(JsonElement element) where T : class
    {
        try
        {
            return AgentJson.Deserialize<T>(element);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static string OnOff(bool value) => value ? "on" : "off";

    // --- system (§5.2) ------------------------------------------------------------------------------------------

    private static List<AgentChange> SystemChanges(LinuxSystemSection? a, LinuxSystemSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        if (a.Os.PrettyName != b.Os.PrettyName)
        {
            changes.Add(new($"Distribution upgraded: {a.Os.PrettyName} → {b.Os.PrettyName}", true, a.Os.PrettyName, b.Os.PrettyName));
        }
        else if ((a.Os.Distribution ?? a.Os.DebianVersion) != (b.Os.Distribution ?? b.Os.DebianVersion))
        {
            var before = a.Os.Distribution ?? a.Os.DebianVersion ?? "?";
            var after = b.Os.Distribution ?? b.Os.DebianVersion ?? "?";
            changes.Add(new($"Point release: {before} → {after}", true, before, after));
        }
        if (a.Os.KernelRelease != b.Os.KernelRelease)
        {
            changes.Add(new($"Kernel updated: {a.Os.KernelRelease} → {b.Os.KernelRelease}", true, a.Os.KernelRelease, b.Os.KernelRelease));
        }
        if (a.Proxmox?.Version is { } pa && b.Proxmox?.Version is { } pb && pa != pb && a.Os.Distribution == b.Os.Distribution)
        {
            changes.Add(new($"Proxmox VE updated: {pa} → {pb}", true));
        }
        if (!string.Equals(a.Realm, b.Realm, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(new(b.Realm is null ? $"Left the realm {a.Realm}" : a.Realm is null ? $"Joined the realm {b.Realm}" : $"Realm {a.Realm} → {b.Realm}", true, a.Realm, b.Realm));
        }

        // Local administrators (§5.2): by account and by why.
        static string Key(LinuxAdmin x) => $"{x.Name}\u0001{x.Via}";
        var adminsBefore = a.Administrators.Select(Key).ToHashSet(StringComparer.Ordinal);
        var adminsAfter = b.Administrators.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var admin in b.Administrators.Where(x => !adminsBefore.Contains(Key(x))).DistinctBy(Key))
        {
            changes.Add(new(AdminAdded(admin), true));
        }
        foreach (var admin in a.Administrators.Where(x => !adminsAfter.Contains(Key(x))).DistinctBy(Key))
        {
            changes.Add(new(AdminRemoved(admin), true));
        }

        // Sudoers rules that give ALL.
        static string Rule(SudoRule r) => $"{r.Who} in {r.File}{(r.NoPassword ? " (NOPASSWD)" : "")}";
        WindowsDiffers.Sets(changes, a.SudoRules.Select(Rule), b.SudoRules.Select(Rule), r => $"Sudoers rule giving ALL added: {r}", r => $"Sudoers rule giving ALL removed: {r}", notable: true);

        // Accounts: who can log in with a password is notable; accounts with a login shell coming and going are not.
        static bool Password(LinuxAccount x) => x.LoginShell && x.PasswordSet == true && x.Locked != true;
        WindowsDiffers.Sets(changes, a.Accounts.Where(Password).Select(x => x.Name), b.Accounts.Where(Password).Select(x => x.Name),
            n => $"{n} can now log in with a password", n => $"{n} can no longer log in with a password", notable: true);
        string Added(string name) => b.Accounts.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is { } account
            ? $"Account {name} added (uid {account.Uid}, {account.Shell ?? "no shell"})" : $"Account {name} added";
        WindowsDiffers.Sets(changes, a.Accounts.Where(x => x.LoginShell).Select(x => x.Name), b.Accounts.Where(x => x.LoginShell).Select(x => x.Name),
            Added, n => $"Account {n} removed", notable: false);

        // Security (§5.2): the firewall and AppArmor are notable.
        var fa = a.Security?.Firewall;
        var fb = b.Security?.Firewall;
        if (fa is not null && fb is not null)
        {
            if (fa.Kind != fb.Kind)
            {
                changes.Add(new($"Firewall: {fa.Kind} → {fb.Kind}{(fb.Active ? "" : " (inactive)")}", true));
            }
            else if (fa.Active != fb.Active)
            {
                changes.Add(new($"Firewall {fb.Kind} turned {OnOff(fb.Active)}", true));
            }
            if (fa.InputPolicy != fb.InputPolicy)
            {
                changes.Add(new($"Firewall input policy: {fa.InputPolicy ?? "none"} → {fb.InputPolicy ?? "none"}", true));
            }
        }
        if (a.Security?.AppArmor is { } aa && b.Security?.AppArmor is { } ab)
        {
            if (aa.Enabled != ab.Enabled)
            {
                changes.Add(new($"AppArmor turned {OnOff(ab.Enabled)}", true));
            }
            else if (aa.Enforcing != ab.Enforcing || aa.Complaining != ab.Complaining)
            {
                changes.Add(new($"AppArmor profiles: {aa.Enforcing} enforcing, {aa.Complaining} complaining → {ab.Enforcing} enforcing, {ab.Complaining} complaining", false));
            }
        }
        if (a.Security?.SecureBoot is { } sa && b.Security?.SecureBoot is { } sb && sa != sb)
        {
            changes.Add(new($"Secure Boot turned {OnOff(sb)}", false));
        }
        if (a.Security?.Fail2ban is { } f2a && b.Security?.Fail2ban is { } f2b && f2a.Running != f2b.Running)
        {
            changes.Add(new($"fail2ban {(f2b.Running ? "started" : "stopped")}", false));
        }
        if (a.Updates is { } ua && b.Updates is { } ub && ua.UnattendedEnabled != ub.UnattendedEnabled)
        {
            changes.Add(new($"unattended-upgrades turned {OnOff(ub.UnattendedEnabled)}", false));
        }
        return changes;
    }

    /// <summary>"deploy added to sudo", "deploy has uid 0", "New local administrator deploy (sudoers)".</summary>
    private static string AdminAdded(LinuxAdmin admin) => admin.Via switch
    {
        var via when via.StartsWith("group ", StringComparison.Ordinal) => $"{admin.Name} added to {via["group ".Length..]}",
        "uid 0" => $"{admin.Name} has uid 0",
        var via => $"New local administrator {admin.Name} ({via})",
    };

    private static string AdminRemoved(LinuxAdmin admin) => admin.Via switch
    {
        var via when via.StartsWith("group ", StringComparison.Ordinal) => $"{admin.Name} removed from {via["group ".Length..]}",
        "uid 0" => $"{admin.Name} no longer has uid 0",
        var via => $"Local administrator {admin.Name} removed ({via})",
    };

    // --- services, timers and cron (§5.4) -----------------------------------------------------------------------

    private static string ClassLabel(LinuxServiceClass c) => c switch
    {
        LinuxServiceClass.ThirdParty => "third-party",
        LinuxServiceClass.Pnmt => "PNMT",
        _ => c.ToString().ToLowerInvariant(),
    };

    private static List<AgentChange> ServiceChanges(LinuxServicesSection? a, LinuxServicesSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        var old = a.Services.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var current = b.Services.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var service in current.Values.Where(s => !old.ContainsKey(s.Name)).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            changes.Add(new($"Service {service.Name} added ({ClassLabel(service.Class)}, {service.UnitFileState ?? "?"}, {service.SubState})", true));
        }
        foreach (var service in old.Values.Where(s => !current.ContainsKey(s.Name)).OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            changes.Add(new($"Service {service.Name} removed", true));
        }
        foreach (var service in current.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            if (!old.TryGetValue(service.Name, out var before))
            {
                continue;
            }
            if (before.UnitFileState != service.UnitFileState)
            {
                changes.Add(new($"Service {service.Name}: {before.UnitFileState ?? "?"} → {service.UnitFileState ?? "?"}", true));
            }
            if (before.Class != service.Class)
            {
                changes.Add(new($"Service {service.Name}: now {ClassLabel(service.Class)} (was {ClassLabel(before.Class)})", true));
            }
            if (!string.Equals(before.User, service.User, StringComparison.Ordinal))
            {
                changes.Add(new($"Service {service.Name}: runs as {service.User ?? "root"} (was {before.User ?? "root"})", true));
            }
            if (!string.Equals(before.Program, service.Program, StringComparison.Ordinal))
            {
                changes.Add(new($"Service {service.Name}: program {service.Program ?? "?"}", false, before.Program, service.Program));
            }
            if (!before.Failed && service.Failed)
            {
                changes.Add(new($"Service {service.Name} failed", false));
            }
        }

        var timersBefore = a.Timers.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var timersAfter = b.Timers.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var timer in timersAfter.Values.Where(t => !timersBefore.ContainsKey(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            changes.Add(new($"Timer {timer.Name} added{(timer.Unit is { } unit ? $" (starts {unit})" : "")}", true));
        }
        foreach (var timer in timersBefore.Values.Where(t => !timersAfter.ContainsKey(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            changes.Add(new($"Timer {timer.Name} removed", true));
        }
        foreach (var timer in timersAfter.Values.Where(t => timersBefore.TryGetValue(t.Name, out var x) && x.UnitFileState != t.UnitFileState))
        {
            changes.Add(new($"Timer {timer.Name}: {timersBefore[timer.Name].UnitFileState ?? "?"} → {timer.UnitFileState ?? "?"}", true));
        }

        // Cron: by the line's hash, so a changed line is one removed and one added.
        static string Line(CronEntry e) => $"{e.Schedule} {e.Command ?? e.Program}";
        var cronBefore = a.Cron.Select(e => e.LineHash).ToHashSet(StringComparer.Ordinal);
        var cronAfter = b.Cron.Select(e => e.LineHash).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in b.Cron.Where(e => !cronBefore.Contains(e.LineHash)).DistinctBy(e => e.LineHash))
        {
            changes.Add(new($"Cron entry added for {entry.User}: {Line(entry)} ({entry.File})", true));
        }
        foreach (var entry in a.Cron.Where(e => !cronAfter.Contains(e.LineHash)).DistinctBy(e => e.LineHash))
        {
            changes.Add(new($"Cron entry removed for {entry.User}: {Line(entry)} ({entry.File})", true));
        }
        return changes;
    }

    // --- remote access (§5.5) -----------------------------------------------------------------------------------

    private static List<AgentChange> RemoteChanges(LinuxRemoteAccessSection? a, LinuxRemoteAccessSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        if (a.Sshd is { } sa && b.Sshd is { } sb)
        {
            changes.AddRange(SshdChanges(sa, sb));
        }
        else if (a.Sshd is null != (b.Sshd is null))
        {
            changes.Add(new(b.Sshd is null ? "sshd's settings can't be read any more" : "sshd's settings can be read now", true));
        }
        if (a.FirewallKind != b.FirewallKind)
        {
            changes.Add(new($"Firewall: {a.FirewallKind ?? "none"} → {b.FirewallKind ?? "none"}", true));
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
            else if (feature.Active && old.Firewall != feature.Firewall)
            {
                changes.Add(new($"{feature.Name}: the firewall's verdict {old.Firewall} → {feature.Firewall}", true));
            }
            if (feature.Active && !old.Ports.SequenceEqual(feature.Ports))
            {
                changes.Add(new($"{feature.Name}: port {string.Join(", ", old.Ports)} → {string.Join(", ", feature.Ports)}", true));
            }
            foreach (var note in feature.Notes.Except(old.Notes))
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

    /// <summary>Every setting of <c>sshd -T</c> that decides who gets in (§5.5): "sshd: password authentication turned on".</summary>
    private static List<AgentChange> SshdChanges(SshdSettings a, SshdSettings b)
    {
        var changes = new List<AgentChange>();
        void Switch(string what, bool before, bool after)
        {
            if (before != after)
            {
                changes.Add(new($"sshd: {what} turned {OnOff(after)}", true));
            }
        }
        void Value(string what, string? before, string? after)
        {
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                changes.Add(new($"sshd: {what} {before ?? "not set"} → {after ?? "not set"}", true, before, after));
            }
        }
        void List(string what, IReadOnlyList<string> before, IReadOnlyList<string> after)
        {
            if (!before.Order(StringComparer.Ordinal).SequenceEqual(after.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                changes.Add(new($"sshd: {what} {WindowsDiffers.List(before)} → {WindowsDiffers.List(after)}", true));
            }
        }
        List("port", a.Ports.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList(),
            b.Ports.Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList());
        List("listens on", a.ListenAddresses, b.ListenAddresses);
        Value("PermitRootLogin", a.PermitRootLogin, b.PermitRootLogin);
        Switch("password authentication", a.PasswordAuthentication, b.PasswordAuthentication);
        Switch("keyboard-interactive authentication", a.KbdInteractiveAuthentication, b.KbdInteractiveAuthentication);
        Switch("key authentication", a.PubkeyAuthentication, b.PubkeyAuthentication);
        List("AllowUsers", a.AllowUsers, b.AllowUsers);
        List("AllowGroups", a.AllowGroups, b.AllowGroups);
        List("DenyUsers", a.DenyUsers, b.DenyUsers);
        List("DenyGroups", a.DenyGroups, b.DenyGroups);
        Switch("agent forwarding", a.AllowAgentForwarding, b.AllowAgentForwarding);
        Value("AllowTcpForwarding", a.AllowTcpForwarding, b.AllowTcpForwarding);
        Switch("X11 forwarding", a.X11Forwarding, b.X11Forwarding);
        Value("MaxAuthTries", a.MaxAuthTries?.ToString(System.Globalization.CultureInfo.InvariantCulture), b.MaxAuthTries?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Value("LogLevel", a.LogLevel, b.LogLevel);
        Value("AuthorizedKeysCommand", a.AuthorizedKeysCommand, b.AuthorizedKeysCommand);
        Switch("TrustedUserCAKeys", a.TrustedUserCaKeys, b.TrustedUserCaKeys);
        Switch("Match blocks", a.MatchBlocks, b.MatchBlocks);
        Switch("socket activation", a.SocketActivated, b.SocketActivated);
        return changes;
    }

    // --- the logging check (§5.6) -------------------------------------------------------------------------------

    private static List<AgentChange> AccessChanges(LinuxAccessSection? a, LinuxAccessSection? b)
    {
        var changes = new List<AgentChange>();
        if (a is null || b is null)
        {
            return changes;
        }
        if (a.Logging.JournalPersistent != b.Logging.JournalPersistent)
        {
            changes.Add(new(b.Logging.JournalPersistent ? "The journal is persistent now" : "The journal is no longer persistent", true));
        }
        if (!string.Equals(a.Logging.SshdLogLevel, b.Logging.SshdLogLevel, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(new($"sshd's LogLevel: {a.Logging.SshdLogLevel ?? "?"} → {b.Logging.SshdLogLevel ?? "?"}", true));
        }
        if (a.Logging.SudoLogging != b.Logging.SudoLogging)
        {
            changes.Add(new($"sudo's logging turned {OnOff(b.Logging.SudoLogging)}", true));
        }
        return changes;
    }
}
