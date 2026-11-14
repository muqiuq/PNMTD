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
/// The seed's servers with an agent (docs/windows-sensor-plan.md §11; docs/linux-sensor-plan.md §9.4): two Windows servers on
/// existing hosts with what their agents would have reported (every section, logins, changes, the agent log) and a
/// diagnostics set assigned to both, with runs. No agent is connected; the heartbeat interval is a day, so the demo stays as
/// seeded while the stack runs. Linux servers join the seed with the Linux agent's sections (L8).
/// </summary>
internal static class DevSeedAgents
{
    private sealed record ServerPlan(string Host, string Hostname, string Os, string Build, int Ubr, bool Failing);

    private static readonly ServerPlan[] Plans =
    [
        new("backup-srv", "BACKUP01", "Microsoft Windows Server 2019 Standard", "17763", 6532, false),
        new("app-04", "APP04", "Microsoft Windows Server 2022 Datacenter", "20348", 2849, true),
    ];

    public static int Seed(PnmtdDbContext db, IReadOnlyDictionary<string, HostEntity> hosts, DateTime now)
    {
        var count = 0;
        var servers = new List<(ServerPlan Plan, SensorEntity Sensor)>();
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
            foreach (var (plan, server) in servers)
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
                    var failing = plan.Failing && n is 3 or 4;
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

    private static void AddEvent(PnmtdDbContext db, Guid sensorId, DateTime at, int code, string message) => db.Events.Add(new EventEntity
    {
        Id = Guid.NewGuid(), SensorId = sensorId, Code = code, Message = message, Created = at, Source = AgentPlatformInfo.Windows.EventSource,
    });
}
