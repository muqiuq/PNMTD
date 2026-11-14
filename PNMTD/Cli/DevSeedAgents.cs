using System.Security.Cryptography;
using System.Text.Json;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Agents;

namespace PNMTD.Cli;

/// <summary>
/// The seed's servers with an agent (docs/windows-sensor-plan.md §11; docs/linux-sensor-plan.md §9.4, §12): two Windows servers on
/// existing hosts with what their agents would have reported (every section, logins, changes, the agent log) and a
/// diagnostics set assigned to both, with runs; and two Linux servers, <i>web-01</i> (Debian 12, on its host) and <i>pve-01</i>
/// (Proxmox VE 9, on a host of its own), with every section, a week of SSH logins with keys, sudo and the internet's scanners,
/// changes, and the diagnostics set assigned to web-01. No agent is connected; the heartbeat interval is a day, so the demo stays
/// as seeded while the stack runs. The key manager's data comes with L5.
/// </summary>
internal static class DevSeedAgents
{
    private sealed record ServerPlan(string Host, string Hostname, string Os, string Build, int Ubr, bool Failing);

    private static readonly ServerPlan[] Plans =
    [
        new("backup-srv", "BACKUP01", "Microsoft Windows Server 2019 Standard", "17763", 6532, false),
        new("app-04", "APP04", "Microsoft Windows Server 2022 Datacenter", "20348", 2849, true),
    ];

    public static int Seed(PnmtdDbContext db, Dictionary<string, HostEntity> hosts, DateTime now)
    {
        var count = 0;
        var servers = new List<(ServerPlan Plan, SensorEntity Sensor)>();
        var linuxServers = SeedLinux(db, hosts, now, ref count);
        foreach (var plan in Plans.Where(p => hosts.ContainsKey(p.Host)))
        {
            var sensor = new SensorEntity
            {
                Id = Guid.NewGuid(), ParentId = hosts[plan.Host].Id, Type = SensorType.WINDOWS, Name = "Windows", Enabled = true,
                Interval = 86_400, GracePeriod = 120, Created = now.AddDays(-40),
            };
            sensor.SetNewSecretToken();
            db.Sensors.Add(sensor);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = AgentSigning.PublicKey(key);
            var boot = now.AddDays(-3).AddMinutes(-17);
            var agent = new AgentEntity
            {
                SensorId = sensor.Id, Platform = AgentPlatform.Windows, AgentId = Guid.NewGuid(), PublicKey = publicKey, KeyFingerprint = AgentSigning.Fingerprint(publicKey),
                JoinedAt = now.AddDays(-40), MachineId = Guid.NewGuid().ToString(), Hostname = plan.Hostname, Fqdn = $"{plan.Hostname.ToLowerInvariant()}.corp.example",
                Domain = "CORP", OsCaption = plan.Os, OsEdition = "ServerStandard", OsBuild = $"{plan.Build}.{plan.Ubr}", OsDisplayVersion = plan.Build == "17763" ? "1809" : "21H2",
                OsSupported = true, Architecture = "x64", AgentVersion = ServerInfo.Version, Capabilities = [AgentCapabilities.Collect, AgentCapabilities.Access,
                    AgentCapabilities.DiagPing, AgentCapabilities.DiagDns],
                ConfigVersion = 1, Connected = false, ConnectedAt = now.AddDays(-3), DisconnectedAt = now.AddMinutes(-1), LastSeenAt = now.AddMinutes(-1),
                RemoteAddress = "10.0.1.20", BootTime = boot, ClockSkewSeconds = 1, RemoteAccessSummary = "RDP · WinRM · SMB · AnyDesk open", AuditGaps = 2,
                Heartbeat = AgentJson.Serialize(new Heartbeat(now.AddMinutes(-1), boot, 12.4, 46, "C:", 36.1, [new SessionInfo(@"CORP\admin", "Rdp", "Active", "10.0.4.17")],
                    false, new Dictionary<string, string>(), 1, ServerInfo.Version, 0, plan.Hostname)),
            };
            db.Agents.Add(agent);
            servers.Add((plan, sensor));
            var name = $"{plan.Host} / Windows";
            AgentLog.Add(db, sensor.Id, name, AgentLogKind.Joined, $"Joined as {plan.Hostname} ({plan.Os} {plan.Build}.{plan.Ubr}) with token k7…. The server's key: {agent.KeyFingerprint}.", address: "10.0.1.20");
            AgentLog.Add(db, sensor.Id, name, AgentLogKind.Connected, $"Connected: agent {ServerInfo.Version} (x64) on {plan.Os} {plan.Build}.{plan.Ubr}.", address: "10.0.1.20");
            AgentLog.Add(db, sensor.Id, name, AgentLogKind.Problem, "collector hardware, collect: MSFT_PhysicalDisk timed out after 15 s (2× since 08:00 UTC)", address: "10.0.1.20");

            // Every section, from the simulator's canned data with this server's names.
            var sections = SimulatedSections.All(plan.Hostname).ToDictionary();
            var system = SimulatedSections.System(plan.Hostname);
            sections[SectionNames.System] = system with { Os = system.Os with { Caption = plan.Os, Build = plan.Build, Ubr = plan.Ubr }, BootTime = boot };
            foreach (var (section, data) in sections)
            {
                var json = AgentJson.Serialize(data);
                db.AgentSections.Add(new AgentSectionEntity
                {
                    SensorId = sensor.Id, Section = section, SchemaVersion = SectionNames.SchemaVersion,
                    Hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)), 0, 16).ToLowerInvariant(),
                    CollectedAt = now.AddMinutes(-3), ReceivedAt = now.AddMinutes(-3), ChangedAt = now.AddDays(-2), Content = AgentSections.Compress(json), Errors = [],
                });
            }

            // Logins: admins over RDP, a service account's share access, failed attempts from outside, aggregated.
            var record = 1000L;
            void Login(DateTime at, LogonOutcome outcome, string user, LogonKind kind, string? source, int count = 1, string? reason = null, bool admin = true) =>
                db.AgentLogins.Add(new AgentLoginEntity
                {
                    Id = Guid.NewGuid(), SensorId = sensor.Id, At = at, LastAt = count > 1 ? at.AddMinutes(8) : null, Count = count, Outcome = outcome, FailureReason = reason,
                    User = user, Domain = "CORP", IsAdmin = admin, Kind = kind, AuthPackage = kind == LogonKind.Network ? "NTLM V2" : "Kerberos", SourceAddress = source,
                    Workstation = source is null ? null : "WS-17", Channel = "Security", RecordId = record++,
                    LoggedOffAt = outcome == LogonOutcome.Success && kind == LogonKind.Rdp ? at.AddMinutes(37) : null,
                });
            for (var day = 0; day < 7; day++)
            {
                Login(now.AddDays(-day).AddHours(-2), LogonOutcome.Success, "admin", LogonKind.Rdp, "10.0.4.17");
                Login(now.AddDays(-day).AddHours(-5), LogonOutcome.Success, "svc-backup", LogonKind.Network, "10.0.1.30", count: 12);
            }
            Login(now.AddHours(-1), LogonOutcome.Failure, "administrator", LogonKind.Network, "203.0.113.9", count: 14, reason: "bad password", admin: true);
            Login(now.AddHours(-26), LogonOutcome.Failure, "jdoe", LogonKind.Rdp, "10.0.4.22", count: 2, reason: "bad password", admin: false);
            count += 17;

            // Changes, with the 253 that the notable ones wrote.
            void Change(DateTime at, string section, string summary, bool notable) => db.AgentChanges.Add(new AgentChangeEntity
            {
                Id = Guid.NewGuid(), SensorId = sensor.Id, Section = section, At = at, Summary = summary, Notable = notable,
            });
            Change(now.AddDays(-2), SectionNames.Services, "Service AnyDesk added (third-party, automatic, running)", true);
            Change(now.AddDays(-2), SectionNames.RemoteAccess, "AnyDesk enabled", true);
            Change(now.AddDays(-9), SectionNames.System, $"Windows updated: build {plan.Build}.{plan.Ubr - 120} → {plan.Build}.{plan.Ubr}", true);
            Change(now.AddDays(-9), SectionNames.Network, "Port 8443/TCP now listens (java)", false);
            AddEvent(db, sensor.Id, now.AddDays(-40), PNMTStatusCodes.WINDOWS_OK, $"{plan.Hostname}: {plan.Os} {plan.Build}.{plan.Ubr - 120}, agent {ServerInfo.Version}");
            AddEvent(db, sensor.Id, now.AddDays(-9), PNMTStatusCodes.WINDOWS_CHANGED, $"Windows updated: build {plan.Build}.{plan.Ubr - 120} → {plan.Build}.{plan.Ubr}");
            AddEvent(db, sensor.Id, now.AddDays(-3), PNMTStatusCodes.WINDOWS_RESTARTED, $"{plan.Hostname}: restarted at {boot:yyyy-MM-dd HH:mm} UTC; shut down by CORP\\admin: Monthly patching");
            AddEvent(db, sensor.Id, now.AddDays(-2), PNMTStatusCodes.WINDOWS_CHANGED, "2 changes: Service AnyDesk added (third-party, automatic, running); AnyDesk enabled");
            AddEvent(db, sensor.Id, now.AddHours(-2), PNMTStatusCodes.WINDOWS_ADMIN_LOGIN, @"CORP\admin: RDP from 10.0.4.17 (Kerberos)");
            AddEvent(db, sensor.Id, now.AddHours(-1), PNMTStatusCodes.WINDOWS_LOGIN_FAILURES, "Failed logins from 203.0.113.9: at least 10 in 10 min (users: administrator)");
            AddEvent(db, sensor.Id, now.AddMinutes(-1), PNMTStatusCodes.WINDOWS_OK, $"{plan.Hostname}: {plan.Os} {plan.Build}.{plan.Ubr}, agent {ServerInfo.Version}");
            count += 8;
        }

        // A diagnostics set assigned to both, with runs; the second server's Basel gateway failed for half an hour, and recovered.
        if (servers.Count > 0)
        {
            var set = new DiagnosticSetEntity
            {
                Id = Guid.NewGuid(), Name = "branch connectivity", Description = "The gateways and the intranet's name, from each server.",
                CreatedAt = now.AddDays(-20), UpdatedAt = now.AddDays(-20),
            };
            var checks = new (string Name, DiagnosticCheckType Type, object Parameters)[]
            {
                ("gw-zurich", DiagnosticCheckType.Ping, new PingParameters("10.0.1.1", 4, 1000)),
                ("gw-basel", DiagnosticCheckType.Ping, new PingParameters("192.0.2.21", 4, 1000, MaxAvgMs: 50)),
                ("intranet", DiagnosticCheckType.Dns, new DnsParameters("intranet.corp.example", DnsRecordKind.A, Expect: "10.0.1.80")),
            };
            for (var i = 0; i < checks.Length; i++)
            {
                set.Checks.Add(new DiagnosticCheckEntity
                {
                    Id = Guid.NewGuid(), SetId = set.Id, Order = i, Name = checks[i].Name, Type = checks[i].Type,
                    Parameters = JsonSerializer.Serialize(checks[i].Parameters, checks[i].Parameters.GetType(), AgentJson.Options),
                });
            }
            db.DiagnosticSets.Add(set);
            // docs/linux-sensor-plan.md §12: web-01 runs it too.
            var targets = servers.Select(s => (s.Plan.Failing, s.Sensor)).Concat(linuxServers.Where(l => l.Host.Name == "web-01").Select(l => (false, l.Sensor)));
            foreach (var (failingPlan, server) in targets)
            {
                var sensor = new SensorEntity
                {
                    Id = Guid.NewGuid(), ParentId = server.ParentId, Type = SensorType.DIAGNOSTICS, Name = $"Diagnostics: {set.Name}", Enabled = true,
                    Interval = 86_400, GracePeriod = 120, Created = now.AddDays(-20),
                };
                sensor.SetNewSecretToken();
                db.Sensors.Add(sensor);
                var assignment = new DiagnosticAssignmentEntity
                {
                    Id = Guid.NewGuid(), SetId = set.Id, TargetSensorId = server.Id, SensorId = sensor.Id, Mode = DiagnosticMode.Scheduled, IntervalMinutes = 15,
                    Enabled = true, CreatedAt = now.AddDays(-20),
                };
                db.DiagnosticAssignments.Add(assignment);
                for (var n = 12; n >= 0; n--)
                {
                    var failing = failingPlan && n is 3 or 4;
                    var at = now.AddMinutes(-15 * n - 1);
                    var results = set.Checks.Select(c =>
                    {
                        var fails = failing && c.Name == "gw-basel";
                        return c.Type == DiagnosticCheckType.Ping
                            ? new DiagnosticCheckResult(c.Id, c.Name, c.Type, fails ? DiagnosticOutcome.Fail : DiagnosticOutcome.Pass, fails ? "100 % loss" : "0 % loss, avg 3.1 ms",
                                new PingValues(4, fails ? 0 : 4, fails ? 100 : 0, fails ? null : 2.4, fails ? null : 3.1, fails ? null : 4.0, fails ? "192.0.2.21" : "10.0.1.1"), null, 812)
                            : new DiagnosticCheckResult(c.Id, c.Name, c.Type, DiagnosticOutcome.Pass, "10.0.1.80 in 2 ms", null,
                                new DnsValues(["10.0.1.80"], "10.0.1.10", 2.1, null), 4);
                    }).ToList();
                    var (outcome, summary, ok) = Diagnostics.DiagnosticsRunner.Summarize(results);
                    db.DiagnosticRuns.Add(new DiagnosticRunEntity
                    {
                        Id = Guid.NewGuid(), AssignmentId = assignment.Id, SensorId = sensor.Id, Trigger = "schedule", StartedAt = at, FinishedAt = at.AddSeconds(4),
                        Outcome = outcome, Summary = summary, Results = JsonSerializer.Serialize(results, AgentJson.Options),
                    });
                    if (n == 12 || n == 4 || n == 2)
                    {
                        AddEvent(db, sensor.Id, at.AddSeconds(4), ok ? PNMTStatusCodes.DIAGNOSTICS_OK : PNMTStatusCodes.DIAGNOSTICS_FAILED, summary);
                    }
                    count++;
                }
            }
        }
        db.SaveChanges();
        return count;
    }

    private static void AddEvent(PnmtdDbContext db, Guid sensorId, DateTime at, int code, string message, string? source = null) => db.Events.Add(new EventEntity
    {
        Id = Guid.NewGuid(), SensorId = sensorId, Code = code, Message = message, Created = at, Source = source ?? AgentPlatformInfo.Windows.EventSource,
    });

    // --- Linux (docs/linux-sensor-plan.md §12) -------------------------------------------------------------------

    private sealed record LinuxPlan(string Host, string Hostname, bool Proxmox, string Address, bool KeyRemoval);

    private static readonly LinuxPlan[] LinuxPlans =
    [
        new("web-01", "web-01", false, "10.0.2.21", true),
        new("pve-01", "pve-01", true, "10.0.2.5", false),
    ];

    /// <summary>The Linux servers: web-01 on its host, pve-01 on a host of its own (Zurich DC), as their agents would have reported them.</summary>
    private static List<(HostEntity Host, SensorEntity Sensor)> SeedLinux(PnmtdDbContext db, Dictionary<string, HostEntity> hosts, DateTime now, ref int count)
    {
        var created = new List<(HostEntity, SensorEntity)>();
        var source = AgentPlatformInfo.Linux.EventSource;
        foreach (var plan in LinuxPlans)
        {
            if (!hosts.TryGetValue(plan.Host, out var host))
            {
                if (!plan.Proxmox)
                {
                    continue;
                }
                host = new HostEntity
                {
                    Id = Guid.NewGuid(), Name = plan.Host, Location = "Zurich DC", Created = now.AddDays(-120), Enabled = true,
                    Notes = "Proxmox VE 9: the cluster zrh-cluster, with pve-02.",
                };
                db.Hosts.Add(host);
                hosts[plan.Host] = host;
            }
            var sensor = new SensorEntity
            {
                Id = Guid.NewGuid(), ParentId = host.Id, Type = SensorType.LINUX, Name = "Linux", Enabled = true, Interval = 86_400, GracePeriod = 120,
                Created = now.AddDays(-30),
            };
            sensor.SetNewSecretToken();
            db.Sensors.Add(sensor);
            created.Add((host, sensor));
            var os = plan.Proxmox ? SimulatedLinux.ProxmoxOs : SimulatedLinux.Os;
            var system = SimulatedLinux.System(plan.Hostname, plan.Proxmox);
            var boot = now.AddDays(-9).AddMinutes(-42);
            var rights = SimulatedLinux.Rights(plan.KeyRemoval, false);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = AgentSigning.PublicKey(key);
            var heartbeat = SimulatedLinux.Heartbeat(plan.Hostname, 1, ServerInfo.Version, plan.KeyRemoval) with
            {
                AgentTime = now.AddMinutes(-1), BootTime = boot, RebootPending = !plan.Proxmox, LastShutdown = $"clean shutdown at {boot.AddMinutes(-1):yyyy-MM-dd HH:mm}",
            };
            var agent = new AgentEntity
            {
                SensorId = sensor.Id, Platform = AgentPlatform.Linux, AgentId = Guid.NewGuid(), PublicKey = publicKey, KeyFingerprint = AgentSigning.Fingerprint(publicKey),
                JoinedAt = now.AddDays(-30), MachineId = system.MachineId, Hostname = plan.Hostname, Fqdn = system.Fqdn, OsCaption = os.Caption, OsEdition = os.Edition,
                OsBuild = os.Build, OsDisplayVersion = os.DisplayVersion, OsSupported = true, Architecture = "x64", AgentVersion = ServerInfo.Version,
                Capabilities = SimulatedLinux.Capabilities(plan.KeyRemoval, false).ToList(), ConfigVersion = 1, Connected = false, ConnectedAt = now.AddDays(-9),
                DisconnectedAt = now.AddMinutes(-1), LastSeenAt = now.AddMinutes(-1), RemoteAddress = plan.Address, BootTime = boot, ClockSkewSeconds = 0,
                Heartbeat = AgentJson.Serialize(heartbeat), Rights = AgentJson.Serialize(rights),
                KernelRelease = system.Os.KernelRelease, Distribution = system.Os.Distribution, SecurityUpdates = system.Updates?.Security ?? 0,
                SecurityUpdatesSince = system.Updates?.Security > 0 ? now.AddDays(-9) : null,
                SecurityPackages = system.Updates is { Security: > 0 } u ? string.Join(", ", u.SecurityPackages) : null,
                RemoteAccessSummary = SimulatedLinux.RemoteAccess(plan.Proxmox).Summary, AuditGaps = SimulatedLinux.Access().Gaps.Count,
            };
            db.Agents.Add(agent);
            var name = $"{host.Name} / Linux";
            AgentLog.Add(db, sensor.Id, name, AgentLogKind.Joined,
                $"Joined as {plan.Hostname} ({os.Caption} {os.Build}) with token q4…. The server's key: {agent.KeyFingerprint}.", address: plan.Address);
            AgentLog.Add(db, sensor.Id, name, AgentLogKind.Connected,
                $"Connected: agent {ServerInfo.Version} (x64) on {os.Caption} {os.Build}; rights on the server: {ServerRights.Describe(rights)}.", address: plan.Address);
            if (plan.KeyRemoval)
            {
                AgentLog.Add(db, sensor.Id, name, AgentLogKind.Warning, $"Rights changed on the server: {ServerRights.Describe(rights)}.", address: plan.Address);
            }

            foreach (var (section, data) in SimulatedLinux.All(plan.Hostname, plan.Proxmox, plan.KeyRemoval))
            {
                var json = AgentJson.Serialize(section == SectionNames.System ? system with { BootTime = boot } : data);
                db.AgentSections.Add(new AgentSectionEntity
                {
                    SensorId = sensor.Id, Section = section, SchemaVersion = SectionNames.SchemaVersion,
                    Hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)), 0, 16).ToLowerInvariant(),
                    CollectedAt = now.AddMinutes(-3), ReceivedAt = now.AddMinutes(-3), ChangedAt = now.AddDays(-2), Content = AgentSections.Compress(json), Errors = [],
                });
            }

            // A week of logins (§5.6): SSH with keys, sudo, the backup's file transfers, the internet's scanners, and a burst at root.
            var record = 5_000_000L + (plan.Proxmox ? 1_000_000 : 0);
            void Login(DateTime at, LogonOutcome outcome, string user, LogonKind kind, string? from, string? method, string? fingerprint = null, int count = 1,
                string? reason = null, bool admin = false, string? target = null, string? tty = null, IReadOnlyList<string>? tried = null) =>
                db.AgentLogins.Add(new AgentLoginEntity
                {
                    Id = Guid.NewGuid(), SensorId = sensor.Id, At = at, LastAt = count > 1 ? at.AddMinutes(9) : null, Count = count, Outcome = outcome, FailureReason = reason,
                    User = user, IsAdmin = admin, Kind = kind, AuthPackage = method, SourceAddress = from, SourcePort = from is null ? null : 40_000 + (int)(record % 20_000),
                    Workstation = tty, Channel = LinuxLogins.Channel, RecordId = record++, KeyFingerprint = fingerprint, TargetUser = target,
                    TriedUsers = tried?.ToList(), LoggedOffAt = outcome == LogonOutcome.Success && kind == LogonKind.Ssh ? at.AddMinutes(23) : null,
                });
            for (var day = 0; day < 7; day++)
            {
                var d = now.AddDays(-day);
                if (plan.Proxmox)
                {
                    Login(d.AddHours(-3), LogonOutcome.Success, "root", LogonKind.Ssh, "10.0.4.17", "publickey", SimulatedLinux.PhilippLaptop.Fingerprint, admin: true);
                    Login(d.AddHours(-11), LogonOutcome.Failure, LinuxLogins.InvalidUser, LogonKind.Ssh, "198.51.100.77", "password", count: 12 + day,
                        reason: LinuxLogins.InvalidUser, tried: ["admin", "test", "proxmox", "pve"]);
                    count += 2;
                    continue;
                }
                Login(d.AddHours(-2), LogonOutcome.Success, "root", LogonKind.Ssh, "10.0.4.17", "publickey", SimulatedLinux.PhilippLaptop.Fingerprint, admin: true);
                Login(d.AddHours(-4), LogonOutcome.Success, "philipp", LogonKind.Ssh, "10.0.4.17", "publickey", SimulatedLinux.PhilippLaptop.Fingerprint, admin: true);
                Login(d.AddHours(-4).AddMinutes(3), LogonOutcome.Success, "philipp", LogonKind.Sudo, null, null, admin: true, target: "root", tty: "pts/0");
                Login(d.AddHours(-6), LogonOutcome.Success, "deploy", LogonKind.Ssh, "10.0.5.12", "publickey", SimulatedLinux.CiRunner.Fingerprint, admin: true);
                Login(d.AddHours(-6).AddMinutes(1), LogonOutcome.Success, "deploy", LogonKind.Sudo, null, null, admin: true, target: "root", tty: "pts/1");
                Login(d.AddHours(-21), LogonOutcome.Success, "bkp", LogonKind.Sftp, "10.0.1.30", "publickey", SimulatedLinux.Backup.Fingerprint, count: 6);
                Login(d.AddHours(-9), LogonOutcome.Failure, LinuxLogins.InvalidUser, LogonKind.Ssh, "198.51.100.23", "password", count: 30 + 3 * day,
                    reason: LinuxLogins.InvalidUser, tried: ["admin", "oracle", "test", "ubuntu", "git", "postgres", "pi", "user", "ftpuser", "guest"]);
                Login(d.AddHours(-15), LogonOutcome.Failure, LinuxLogins.InvalidUser, LogonKind.Ssh, "203.0.113.200", "password", count: 8,
                    reason: LinuxLogins.InvalidUser, tried: ["support", "admin1"]);
                count += 8;
            }
            if (!plan.Proxmox)
            {
                Login(now.AddDays(-2), LogonOutcome.Failure, "root", LogonKind.Ssh, "203.0.113.9", "password", count: 14, reason: LinuxLogins.WrongPassword, admin: true);
                Login(now.AddDays(-1).AddHours(-1), LogonOutcome.Failure, "www", LogonKind.Sudo, null, null, reason: LinuxLogins.NotInSudoers, tty: "pts/2");
                count += 2;
            }

            // Changes, with the 263 that the notable ones wrote.
            void Change(DateTime at, string section, string summary, bool notable) => db.AgentChanges.Add(new AgentChangeEntity
            {
                Id = Guid.NewGuid(), SensorId = sensor.Id, Section = section, At = at, Summary = summary, Notable = notable,
            });
            var oldKernel = plan.Proxmox ? "6.14.8-2-pve" : "6.1.0-27-amd64";
            AddEvent(db, sensor.Id, now.AddDays(-30), PNMTStatusCodes.LINUX_OK, $"{plan.Hostname}: {os.Caption}, kernel {oldKernel}, agent {ServerInfo.Version}", source);
            Change(now.AddDays(-9).AddMinutes(-50), SectionNames.System, $"Kernel updated: {oldKernel} → {os.Build}", true);
            AddEvent(db, sensor.Id, now.AddDays(-9).AddMinutes(-50), PNMTStatusCodes.LINUX_CHANGED, $"Kernel updated: {oldKernel} → {os.Build}", source);
            AddEvent(db, sensor.Id, now.AddDays(-9).AddMinutes(-41), PNMTStatusCodes.LINUX_RESTARTED,
                $"{plan.Hostname}: restarted at {boot:yyyy-MM-dd HH:mm} UTC; clean shutdown at {boot.AddMinutes(-1):yyyy-MM-dd HH:mm}", source);
            if (plan.Proxmox)
            {
                Change(now.AddDays(-9).AddMinutes(-50), SectionNames.System, "Proxmox VE updated: 9.0.6 → 9.0.10", true);
                Change(now.AddDays(-4), SectionNames.Network, "Network adapter wg0 added", true);
                Change(now.AddDays(-4), SectionNames.RemoteAccess, "WireGuard enabled (port 51820, wg0: 3 peers)", true);
                AddEvent(db, sensor.Id, now.AddDays(-4), PNMTStatusCodes.LINUX_CHANGED,
                    "2 changes: Network adapter wg0 added; WireGuard enabled (port 51820, wg0: 3 peers)", source);
                AddEvent(db, sensor.Id, now.AddHours(-3), PNMTStatusCodes.LINUX_ADMIN_LOGIN,
                    $"root: SSH from 10.0.4.17 with {AgentAccessService.KeyText(SimulatedLinux.PhilippLaptop.Fingerprint)}", source);
                count += 4;
            }
            else
            {
                Change(now.AddDays(-12), SectionNames.RemoteAccess, "sshd: password authentication turned off", true);
                Change(now.AddDays(-5), SectionNames.Services, "Service zabbix-agent2.service added (third-party, enabled, running)", true);
                Change(now.AddDays(-5), SectionNames.Network, "Port 10050/tcp now listens (zabbix_agent2: zabbix-agent2.service)", false);
                Change(now.AddDays(-5), SectionNames.Services, "Cron entry added for root: */5 * * * * /opt/sync/run.sh (/etc/cron.d/sync)", true);
                Change(now.AddDays(-3), SectionNames.System, "deploy added to sudo", true);
                Change(now.AddDays(-3), SectionNames.System, "Sudoers rule giving ALL added: deploy in /etc/sudoers.d/deploy (NOPASSWD)", true);
                AddEvent(db, sensor.Id, now.AddDays(-12), PNMTStatusCodes.LINUX_CHANGED, "sshd: password authentication turned off", source);
                AddEvent(db, sensor.Id, now.AddDays(-5), PNMTStatusCodes.LINUX_CHANGED,
                    "2 changes: Service zabbix-agent2.service added (third-party, enabled, running); Cron entry added for root: */5 * * * * /opt/sync/run.sh (/etc/cron.d/sync)",
                    source);
                AddEvent(db, sensor.Id, now.AddDays(-3), PNMTStatusCodes.LINUX_CHANGED,
                    "2 changes: deploy added to sudo; Sudoers rule giving ALL added: deploy in /etc/sudoers.d/deploy (NOPASSWD)", source);
                AddEvent(db, sensor.Id, now.AddDays(-2), PNMTStatusCodes.LINUX_LOGIN_FAILURES, "Failed logins from 203.0.113.9: at least 10 in 10 min (users: root)", source);
                AddEvent(db, sensor.Id, now.AddDays(-1).AddHours(-1), PNMTStatusCodes.LINUX_LOGIN_FAILURES, "www: not in sudoers (sudo on pts/2)", source);
                AddEvent(db, sensor.Id, now.AddHours(-2), PNMTStatusCodes.LINUX_ADMIN_LOGIN,
                    $"root: SSH from 10.0.4.17 with {AgentAccessService.KeyText(SimulatedLinux.PhilippLaptop.Fingerprint)}", source);
                count += 9;
            }
            AddEvent(db, sensor.Id, now.AddMinutes(-1), PNMTStatusCodes.LINUX_OK, $"{plan.Hostname}: {os.Caption}, kernel {os.Build}, agent {ServerInfo.Version}", source);
            count += 5;
        }
        return created;
    }
}
