using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using PNMTD.Lib.Agent;

namespace PNMTD.Cli;

/// <summary>What a simulated agent keeps between runs: PNMT, its identity and key, and PNMTD's pinned key.</summary>
public sealed record SimulatedAgentState(string Url, Guid AgentId, Guid SensorId, string PrivateKey, string PnmtdKey, string Hostname);

/// <summary>
/// A fake Windows agent (docs/windows-sensor-plan.md §11), for development without Windows and for the tests: it joins with a
/// token, connects with the same session code as the real agent, sends heartbeats and canned sections and logins, and
/// answers collect, run-diagnostics and send-log.
/// </summary>
public sealed class WindowsSimulator(Uri baseUrl, HttpClient http, Func<CancellationToken, Task<WebSocket>> connect, TextWriter output) : IAgentSessionHandler
{
    public const string AgentVersion = "0.0.0-sim";

    private readonly DateTimeOffset bootTime = DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(-17);
    private readonly List<string> log = [];
    private AgentSession? session;
    private string hostname = "SIM-SRV01";

    public static AgentOs Os { get; } = new("Microsoft Windows Server 2019 Standard", "ServerStandard", "10.0.17763", "17763.6532", "1809", true, "Server");

    public static IReadOnlyList<string> Capabilities { get; } =
        [AgentCapabilities.Collect, AgentCapabilities.Access, AgentCapabilities.DiagPing, AgentCapabilities.DiagDns];

    private readonly System.Security.Cryptography.ECDiffieHellman encryption = System.Security.Cryptography.ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Whether an administrator allowed scripts on this (simulated) server (§7.2): off, as on a real one.</summary>
    public bool AllowScripts { get; set; }

    /// <summary>Heartbeats sent, for the tests.</summary>
    public int Heartbeats { get; private set; }

    /// <summary>Requests answered, by type, for the tests.</summary>
    public List<string> Requests { get; } = [];

    public async Task<SimulatedAgentState> JoinAsync(string token, string? name, string hostname, CancellationToken ct)
    {
        this.hostname = hostname;
        var info = await AgentJoinClient.InfoAsync(http, baseUrl, ct);
        Write($"PNMT {info.PnmtVersion}, its key {info.SigningKeyFingerprint}");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new JoinRequest(token, AgentSigning.PublicKey(key), hostname, $"{hostname.ToLowerInvariant()}.sim.example", "SIM", Os, AgentVersion, "x64",
            Guid.NewGuid().ToString(), name);
        var (response, grant) = await AgentJoinClient.JoinAsync(http, baseUrl, request, ct);
        Write($"Joined as {grant.HostName} / {grant.SensorName}: agent {grant.AgentId}, this key {grant.AgentKeyFingerprint}");
        return new SimulatedAgentState(baseUrl.ToString(), grant.AgentId, grant.SensorId, Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            response.SigningPublicKey, hostname);
    }

    /// <summary>Connects and reports until <paramref name="ct"/> ends, reconnecting after a second; <paramref name="heartbeats"/> stops after so many.</summary>
    public async Task<AgentSessionEnd> RunAsync(SimulatedAgentState state, CancellationToken ct, TimeSpan? interval = null, int? heartbeats = null)
    {
        hostname = state.Hostname;
        using var agentKey = ECDsa.Create();
        agentKey.ImportPkcs8PrivateKey(Convert.FromBase64String(state.PrivateKey), out _);
        using var pnmtdKey = AgentSigning.ImportPublicKey(state.PnmtdKey) ?? throw new InvalidOperationException("The state's PNMTD key is invalid.");
        var capabilities = Capabilities.Append(AgentCapabilities.Parse).Concat(AllowScripts ? [AgentCapabilities.Scripts] : Array.Empty<string>()).ToList();
        var identity = new AgentIdentity(state.AgentId, agentKey, pnmtdKey, AgentSigning.Origin(baseUrl), AgentVersion, "x64", Os, capabilities,
            Convert.ToBase64String(encryption.ExportSubjectPublicKeyInfo()));
        while (!ct.IsCancellationRequested)
        {
            var socket = await connect(ct);
            AgentSession current;
            try
            {
                current = await AgentSession.HandshakeAsync(socket, identity, restartedAfterCrash: false, ct);
            }
            catch (AgentHandshakeException e)
            {
                Write($"Refused: {e.Message}");
                socket.Dispose();
                if (e.CloseCode == AgentCloseCodes.Revoked)
                {
                    return new AgentSessionEnd(e.CloseCode, e.Reason);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }
            await using (current)
            {
                session = current;
                LastConfig = current.Config;
                Write($"Connected; configuration {current.Config.ConfigVersion}, heartbeat every {current.Config.HeartbeatSeconds} s, {current.Config.Diagnostics.Count} diagnostics assignments");
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var receiving = current.RunAsync(this, stop.Token);
                var sending = ReportAsync(current, interval ?? TimeSpan.FromSeconds(current.Config.HeartbeatSeconds), heartbeats, stop.Token);
                AgentSessionEnd end;
                try
                {
                    var first = await Task.WhenAny(receiving, sending);
                    if (first == sending)
                    {
                        await sending;
                        await current.CloseAsync("done", CancellationToken.None);
                        return new AgentSessionEnd(1000, "done");
                    }
                    end = await receiving;
                }
                catch (Exception e) when (ct.IsCancellationRequested || e is WebSocketException or OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await current.CloseAsync("stopped", closing.Token);
                        return new AgentSessionEnd(1000, "stopped");
                    }
                    end = new AgentSessionEnd(null, e.Message);
                }
                finally
                {
                    stop.Cancel();
                }
                Write($"Disconnected: {end.CloseCode} {end.Reason}");
                if (end.Revoked)
                {
                    return end;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return new AgentSessionEnd(null, "stopped");
    }

    private async Task ReportAsync(AgentSession current, TimeSpan interval, int? count, CancellationToken ct)
    {
        await SendSectionsAsync(current, [], ct);
        await current.SendAsync(AgentMessageTypes.Access, SimulatedSections.Logins(hostname), ct);
        while (!ct.IsCancellationRequested && (count is null || Heartbeats < count))
        {
            await current.SendAsync(AgentMessageTypes.Heartbeat, Heartbeat(current), ct);
            Heartbeats++;
            if (count is null || Heartbeats < count)
            {
                await Task.Delay(interval, ct);
            }
        }
    }

    public Heartbeat Heartbeat(AgentSession current) => new(DateTimeOffset.UtcNow, bootTime, 7.5 + Random.Shared.NextDouble() * 10, 41.2, "C:", 63.5,
        [new SessionInfo("SIM\\admin", "Rdp", "Active", "10.0.4.17")], false, SimulatedSections.Hashes(hostname), current.Config.ConfigVersion, AgentVersion, 0,
        hostname, "Restarted for updates (planned)");

    private Task SendSectionsAsync(AgentSession current, IReadOnlyList<string> names, CancellationToken ct) =>
        SimulatedSections.SendAsync(current, hostname, names, ct);

    /// <summary>The last configuration received, for the tests.</summary>
    public AgentConfig? LastConfig { get; private set; }

    public Task OnConfigAsync(AgentConfig config, CancellationToken ct)
    {
        LastConfig = config;
        Write($"Configuration {config.ConfigVersion}: {config.Diagnostics.Count} diagnostics assignments");
        return Task.CompletedTask;
    }

    public async Task<Ack> OnRequestAsync(AgentSession current, SignedContent request, CancellationToken ct)
    {
        Requests.Add(request.Type);
        Write($"Request {request.Type} ({request.RequestId})");
        switch (request.Type)
        {
            case AgentMessageTypes.Collect:
                var collect = AgentJson.Deserialize<CollectRequest>(request.Body);
                await SendSectionsAsync(current, collect?.Sections ?? [], ct);
                return new Ack(request.RequestId, true);
            case AgentMessageTypes.SendLog:
                var lines = log.TakeLast(Math.Clamp(AgentJson.Deserialize<SendLogRequest>(request.Body)?.Lines ?? 100, 1, 500)).ToList();
                await current.SendAsync(AgentMessageTypes.Log, new LogLines(request.RequestId, lines), ct);
                return new Ack(request.RequestId, true);
            case AgentMessageTypes.RunDiagnostics:
                var run = AgentJson.Deserialize<RunDiagnosticsRequest>(request.Body)!;
                var result = SimulatedSections.Diagnostics(current.Config, run.AssignmentId, run.RunId, "manual");
                if (result is null)
                {
                    return new Ack(request.RequestId, false, "No such assignment in this agent's configuration.");
                }
                await current.SendAsync(AgentMessageTypes.DiagResult, new DiagnosticResultBatch([result]), ct);
                return new Ack(request.RequestId, true);
            case AgentMessageTypes.ParseScript:
                var parse = AgentJson.Deserialize<ParseScriptRequest>(request.Body)!;
                await current.SendAsync(AgentMessageTypes.ParseResult, new ParseResultMessage(request.RequestId, parse.Hash, SimulatedParser.Parse(parse.Text), null), ct);
                return new Ack(request.RequestId, true);
            case AgentMessageTypes.RunScript:
                if (!AllowScripts)
                {
                    return new Ack(request.RequestId, false, "Scripts are not allowed on this server.");
                }
                var script = AgentJson.Deserialize<RunScriptRequest>(request.Body)!;
                var started = DateTimeOffset.UtcNow;
                var password = script.RunAs.Password is { } secret ? AgentSecrets.Decrypt(encryption, secret, script.RunId) : null;
                var secure = script.SecureArguments.ToDictionary(p => p.Key, p => AgentSecrets.Decrypt(encryption, p.Value, script.RunId));
                var output = $"Hello from {hostname} as {script.RunAs.Account ?? "SYSTEM"}\n" +
                    string.Join("\n", script.Arguments.Select(a => $"{a.Key}={a.Value.GetRawText()}")) +
                    string.Join("", secure.Select(p => $"\n{p.Key} has {p.Value.Length} characters")) +
                    (password is null ? "" : $"\nthe password has {password.Length} characters");
                await current.SendAsync(AgentMessageTypes.ScriptResult, new ScriptResultMessage(script.RunId, ScriptRunState.Done, 0, output, null, 12,
                    started, DateTimeOffset.UtcNow), ct);
                return new Ack(request.RequestId, true);
            default:
                return new Ack(request.RequestId, false, $"The simulator does not know \"{request.Type}\".");
        }
    }

    public void OnIgnored(string type, string reason) => Write($"Ignored {type}: {reason}");

    private void Write(string line)
    {
        var text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [INF] simulator: {line}";
        lock (log)
        {
            log.Add(text);
            if (log.Count > 1000)
            {
                log.RemoveAt(0);
            }
        }
        output.WriteLine(line);
    }

    /// <summary>The WebSocket URL of a PNMT URL: http → ws, https → wss.</summary>
    public static Uri WebSocketUrl(Uri baseUrl)
    {
        var builder = new UriBuilder(new Uri(baseUrl, AgentProtocol.WebSocketPath)) { Scheme = baseUrl.Scheme == "https" ? "wss" : "ws" };
        return builder.Uri;
    }

    public static string Save(SimulatedAgentState state) => JsonSerializer.Serialize(state, AgentJson.Options);

    public static SimulatedAgentState Load(string json) => JsonSerializer.Deserialize<SimulatedAgentState>(json, AgentJson.Options)
        ?? throw new InvalidOperationException("The state file is empty.");
}

/// <summary>The simulator's canned data: sections (W2, W3), logins (W3) and diagnostics results (W4).</summary>
public static class SimulatedSections
{
    private static readonly DateTimeOffset Boot = DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(-17);

    /// <summary>Fixed dates: the same data must give the same hash.</summary>
    private static readonly DateTimeOffset Stamp = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Changes what the next sections report, for the tests of the differs: a section's data by name.</summary>
    public static Func<string, string, object?>? Override { get; set; }

    public static IReadOnlyDictionary<string, object> All(string hostname)
    {
        var sections = new Dictionary<string, object>
        {
            [SectionNames.System] = System(hostname),
            [SectionNames.Hardware] = Hardware(),
            [SectionNames.Network] = Network(),
            [SectionNames.Services] = Services(),
            [SectionNames.RemoteAccess] = RemoteAccess(),
            [SectionNames.Access] = Access(),
            [SectionNames.EventLog] = EventLog(),
            [SectionNames.Scripts] = Scripts(),
        };
        foreach (var name in sections.Keys.ToList())
        {
            if (Override?.Invoke(name, hostname) is { } replaced)
            {
                sections[name] = replaced;
            }
        }
        return sections;
    }

    public static IReadOnlyDictionary<string, string> Hashes(string hostname) =>
        All(hostname).ToDictionary(p => p.Key, p => Hash(AgentJson.ToElement(p.Value)));

    public static async Task SendAsync(AgentSession session, string hostname, IReadOnlyList<string> names, CancellationToken ct)
    {
        foreach (var (name, data) in All(hostname).Where(p => names.Count == 0 || names.Contains(p.Key)))
        {
            var element = AgentJson.ToElement(data);
            await session.SendAsync(AgentMessageTypes.Section, new SectionMessage(name, SectionNames.SchemaVersion, Hash(element), DateTimeOffset.UtcNow, element), ct);
        }
    }

    private static string Hash(global::System.Text.Json.JsonElement element) =>
        Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(element.GetRawText())), 0, 16).ToLowerInvariant();

    public static SystemSection System(string hostname) => new(
        hostname, $"{hostname.ToLowerInvariant()}.sim.example", "SIM", true, "Member server", "3f1c2a9e-5b7d-4c8e-9a01-2b3c4d5e6f70",
        new OsInfo("Microsoft Windows Server 2019 Standard", "ServerStandard", "10.0.17763", "17763", 6532, "1809", Stamp.AddYears(-2), "64-bit",
            true, true, "Server", new ActivationInfo("Licensed", "Windows(R), ServerStandard edition", "Windows(R) Operating System, VOLUME_KMSCLIENT channel",
                "Volume:GVLK", "J464C", 259200, null, "kms01.sim.example", null, null)),
        Boot,
        new ShutdownInfo(Boot.AddMinutes(-2), false, "Operating System: Service pack (Planned); Monthly patching", "SIM\\admin"),
        new UpdatesInfo("KB5044277", Stamp.AddDays(-12), false, []),
        new TimeInfo("W. Europe Standard Time", "dc01.sim.example", Stamp.AddMinutes(-30)),
        new SecurityInfo(new DefenderInfo(true, 0, Stamp.AddHours(-20), Stamp.AddDays(-6), "4.18.24090.11"),
            [new FirewallProfileState("Domain", true, "Block", true), new FirewallProfileState("Private", true, "Block", false), new FirewallProfileState("Public", true, "Block", false)],
            [new BitLockerVolume("C:", "On", "Fully encrypted")], true),
        [new LocalAdmin("SIM\\Domain Admins", "Group"), new LocalAdmin($"{hostname}\\Administrator", "User"), new LocalAdmin("SIM\\svc-backup", "User")],
        [new SessionInfo("SIM\\admin", "Rdp", "Active", "10.0.4.17", Stamp.AddMinutes(-42))],
        new HypervisorInfo(true, "VMware", "VMware7,1"));

    public static HardwareSection Hardware() => new(
        new MachineInfo("VMware, Inc.", "VMware7,1", "VMware-56 4d 9a 1c", "VMW71.00V.21100432.B64.2301110304", Stamp.AddYears(-2), "UEFI"),
        new TpmInfo(true, "2.0", true),
        [new ProcessorInfo("Intel(R) Xeon(R) Gold 6338 CPU @ 2.00GHz", 4, 4, 1995)],
        new MemoryInfo(16L * 1024 * 1024 * 1024, [new MemoryModule("RAM slot #0", 16L * 1024 * 1024 * 1024, null, "VMware Virtual RAM", null)]),
        [new DiskInfo("0", "VMware Virtual disk", 120L * 1024 * 1024 * 1024, "SSD", "SAS", "Healthy", "OK", false),
         new DiskInfo("1", "VMware Virtual disk", 2L * 1024 * 1024 * 1024 * 1024, "HDD", "SAS", "Healthy", "OK", false)],
        [new VolumeInfo("C:", null, "NTFS", 119L * 1024 * 1024 * 1024, 43L * 1024 * 1024 * 1024, "On"),
         new VolumeInfo("D:", "Data", "ReFS", 2047L * 1024 * 1024 * 1024, 812L * 1024 * 1024 * 1024, "Off")],
        [new GpuInfo("Microsoft Basic Display Adapter", "10.0.17763.1", null)],
        []);

    public static NetworkSection Network() => new(
        [new AdapterInfo("Ethernet0", "vmxnet3 Ethernet Adapter", "Ethernet", "00:50:56:9A:1C:22", "Up", 10_000_000_000, 1500, false, null, null,
            ["10.0.1.20/24", "fe80::4c1:2b3c:4d5e:6f70/64"], ["10.0.1.1"], ["10.0.1.10", "10.0.1.11"], "sim.example", true, null, "vmxnet3 Ethernet Adapter",
            "1.9.2.0", "Domain", false)],
        [],
        [],
        [new RouteInfo("0.0.0.0/0", "10.0.1.1", "Ethernet0", 0, true), new RouteInfo("10.20.0.0/16", "10.0.1.254", "Ethernet0", 10, true)],
        [new ListenerInfo("TCP", "0.0.0.0", 135, 912, "svchost", ["RpcEptMapper", "RpcSs"]),
         new ListenerInfo("TCP", "0.0.0.0", 445, 4, "System", []),
         new ListenerInfo("TCP", "0.0.0.0", 3389, 1104, "svchost", ["TermService"]),
         new ListenerInfo("TCP", "0.0.0.0", 5985, 4, "System", ["WinRM"])],
        new Dictionary<string, int> { ["Established"] = 23, ["TimeWait"] = 4 },
        new FirewallInfo([new FirewallProfileState("Domain", true, "Block", true), new FirewallProfileState("Private", true, "Block", false),
                new FirewallProfileState("Public", true, "Block", false)],
            [new FirewallRuleInfo("Remote Desktop - User Mode (TCP-In)", "Remote Desktop", "TCP", "3389", "All", "%SystemRoot%\\system32\\svchost.exe", null),
             new FirewallRuleInfo("Windows Remote Management (HTTP-In)", "Windows Remote Management", "TCP", "5985", "Domain, Private", "System", null)]),
        null,
        []);

    public static ServicesSection Services() => new(
    [
        new ServiceEntry("AnyDesk", "AnyDesk Service", "Running", "Automatic", "LocalSystem", @"C:\Program Files (x86)\AnyDesk\AnyDesk.exe", null, "8.0.14",
            "philandro Software GmbH", "philandro Software GmbH", ServiceClass.ThirdParty, false, 2210),
        new ServiceEntry("BackupAgent", "Backup Agent", "Stopped", "Automatic", @"SIM\svc-backup", @"C:\Tools\backup-agent.exe", null, "2.1.0", null, null,
            ServiceClass.Unsigned, true),
        new ServiceEntry("MSSQLSERVER", "SQL Server (MSSQLSERVER)", "Running", "Automatic", @"NT Service\MSSQLSERVER",
            @"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\Binn\sqlservr.exe", null, "2022.160.4135.4", "Microsoft Corporation",
            "Microsoft Corporation", ServiceClass.Microsoft, false, 3120),
        new ServiceEntry("PNMTSensor", "PNMT Sensor", "Running", "Delayed", "LocalSystem", @"C:\Program Files\PNMT Sensor\pnmt-sensor.exe", null, WindowsSimulator.AgentVersion, "PNMT", null,
            ServiceClass.Pnmt, false, 4410),
        new ServiceEntry("TermService", "Remote Desktop Services", "Running", "Manual", @"NT Authority\NetworkService", @"C:\Windows\System32\svchost.exe",
            @"C:\Windows\System32\termsrv.dll", "10.0.17763.1", "Microsoft Corporation", "Microsoft Windows (catalog)", ServiceClass.Windows, false, 1104),
        new ServiceEntry("WinRM", "Windows Remote Management (WS-Management)", "Running", "Automatic", @"NT AUTHORITY\NetworkService", @"C:\Windows\System32\svchost.exe",
            @"C:\Windows\system32\WsmSvc.dll", "10.0.17763.1", "Microsoft Corporation", "Microsoft Windows (catalog)", ServiceClass.Windows, false, 1388),
    ]);

    public static RemoteAccessSection RemoteAccess() => new(
    [
        new RemoteFeature("rdp", "Remote Desktop (RDP)", true, true, [3389], true, true, ["Domain", "Private", "Public"], []),
        new RemoteFeature("winrm", "WinRM / PowerShell remoting", true, true, [5985], true, true, ["Domain", "Private"], []),
        new RemoteFeature("smb", "SMB file sharing", true, true, [445], true, true, ["Domain"], ["shares: ADMIN$, C$, IPC$, Data"]),
        new RemoteFeature("openssh", "OpenSSH Server", false, false, [22], false, false, [], []),
        new RemoteFeature("tool:anydesk", "AnyDesk", true, true, [], false, false, [], ["service AnyDesk"]),
    ], "RDP · WinRM · SMB · AnyDesk open");

    public static AccessSection Access() => new(
        [new SessionInfo("SIM\\admin", "Rdp", "Active", "10.0.4.17", Stamp.AddMinutes(-42))],
        [
            new AuditSetting("Logon", "{0CCE9215-69AE-11D9-BED3-505054503030}", true, false, true, true,
                "auditpol /set /subcategory:\"{0CCE9215-69AE-11D9-BED3-505054503030}\" /failure:enable"),
            new AuditSetting("Special Logon", "{0CCE921B-69AE-11D9-BED3-505054503030}", true, false, true, false, null),
        ],
        [
            new OperationalLog("Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational", "RDP connections (1149)", true, true, null),
            new OperationalLog("OpenSSH/Operational", "SSH logins", false, false, null),
        ],
        new SecurityLogInfo(20 * 1024 * 1024, Stamp.AddHours(-9), 9),
        new AccessCounts(14, 3, 2, 0),
        ["auditpol /set /subcategory:\"{0CCE9215-69AE-11D9-BED3-505054503030}\" /failure:enable", "wevtutil sl Security /ms:536870912"]);

    public static EventLogSection EventLog() => new(
    [
        new EventEntry(Stamp.AddHours(-1), "System", "Error", "Service Control Manager", 7031, null,
            "The Backup Agent service terminated unexpectedly. It has done this 1 time(s).", null, 40211),
        new EventEntry(Stamp.AddHours(-3), "Application", "Warning", "MSSQLSERVER", 833, null,
            "SQL Server has encountered 1 occurrence(s) of I/O requests taking longer than 15 seconds to complete.", null, 88123),
    ]);

    /// <summary>A script registered in the agent's window, with a schedule, and scripts from PNMT allowed for it.</summary>
    public const string RegisteredScriptText = """
        <#
        .SYNOPSIS
            Reports the disks' free space.
        #>
        [CmdletBinding()]
        param(
            [ValidateRange(1, 99)]
            [int] $MinFreePercent = 10
        )
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'
        Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType = 3' | ForEach-Object { '{0} {1:N1} GB free' -f $_.DeviceID, ($_.FreeSpace / 1GB) }
        """;

    public static ScriptsSection Scripts() => new(true, false, [],
    [
        new RegisteredScript("Get-DiskReport", ScriptHashes.Of(RegisteredScriptText), true, RunAsKind.Service, null, "daily at 02:00",
            new ScheduledTaskState("Ready", Stamp.AddHours(-6), 0, Stamp.AddHours(18)), Stamp.AddDays(-3), "SIM\\admin", Stamp.AddDays(-3),
            RegisteredScriptText, SimulatedParser.Parse(RegisteredScriptText), null),
    ]);

    /// <summary>An admin's RDP login and a few failed ones, new each time (record ids from the clock).</summary>
    public static AccessBatch Logins(string hostname)
    {
        var id = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var now = DateTimeOffset.UtcNow;
        return new AccessBatch(
        [
            new LoginRecord("Security", id, now.AddSeconds(-30), null, 1, LogonOutcome.Success, null, "admin", "SIM", true, LogonKind.Rdp, "Kerberos", "10.0.4.17",
                51234, "WS-17", true, "0x3E7A1", null),
            new LoginRecord("Security", id + 1, now.AddSeconds(-20), now.AddSeconds(-5), 3, LogonOutcome.Failure, "bad password", "administrator", null, true,
                LogonKind.Network, "NTLM", "203.0.113.9", null, "KALI", null, null, null),
        ]);
    }

    /// <summary>Checks whose names are in here fail: for the tests of 452.</summary>
    public static HashSet<string> FailingChecks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A run of an assignment in the configuration: every check passes, unless it is named in <see cref="FailingChecks"/>.</summary>
    public static DiagnosticRunResult? Diagnostics(AgentConfig config, Guid assignmentId, Guid runId, string trigger)
    {
        var assignment = config.Diagnostics.FirstOrDefault(a => a.AssignmentId == assignmentId);
        if (assignment is null)
        {
            return null;
        }
        var started = DateTimeOffset.UtcNow;
        var results = assignment.Checks.Select(c =>
        {
            var fails = FailingChecks.Contains(c.Name);
            return c.Type == DiagnosticCheckType.Ping
                ? new DiagnosticCheckResult(c.Id, c.Name, c.Type, fails ? DiagnosticOutcome.Fail : DiagnosticOutcome.Pass, fails ? "100 % loss" : "0 % loss, avg 1.2 ms",
                    new PingValues(c.Ping?.Count ?? 4, fails ? 0 : c.Ping?.Count ?? 4, fails ? 100 : 0, fails ? null : 0.9, fails ? null : 1.2, fails ? null : 1.8, "10.0.1.1"), null, 4.2)
                : new DiagnosticCheckResult(c.Id, c.Name, c.Type, fails ? DiagnosticOutcome.Fail : DiagnosticOutcome.Pass, fails ? "NXDOMAIN" : "10.0.1.10 in 3 ms",
                    null, new DnsValues(fails ? [] : ["10.0.1.10"], "10.0.1.10", 3, fails ? "NXDOMAIN" : null), 3.1);
        }).ToList();
        return new DiagnosticRunResult(assignmentId, runId, trigger, started, DateTimeOffset.UtcNow, results);
    }
}

/// <summary>
/// The simulator's stand-in for PowerShell's parser: <c>[Parameter(Mandatory)] [type] $Name</c> lines in a <c>param()</c> block,
/// <c>[CmdletBinding()]</c> and <c>Invoke-Expression</c>. Good enough for development and the tests; the agent uses the real one.
/// </summary>
public static partial class SimulatedParser
{
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<attrs>(\[[^\]]*\]\s*)*)\[(?<type>[A-Za-z0-9.]+)\]\s*\$(?<name>[A-Za-z_][A-Za-z0-9_]*)(\s*=\s*(?<default>[^,\r\n)]+))?")]
    private static partial System.Text.RegularExpressions.Regex Parameter();

    public static ParsedScript Parse(string text)
    {
        var start = text.IndexOf("param(", StringComparison.OrdinalIgnoreCase);
        var parameters = new List<ScriptParameter>();
        if (start >= 0)
        {
            var depth = 0;
            var end = start + 5;
            for (; end < text.Length; end++)
            {
                depth += text[end] == '(' ? 1 : text[end] == ')' ? -1 : 0;
                if (depth == 0)
                {
                    break;
                }
            }
            foreach (System.Text.RegularExpressions.Match m in Parameter().Matches(text[start..Math.Min(end + 1, text.Length)]))
            {
                var type = m.Groups["type"].Value;
                var attrs = m.Groups["attrs"].Value;
                var set = System.Text.RegularExpressions.Regex.Match(attrs, @"ValidateSet\(([^)]*)\)");
                parameters.Add(new ScriptParameter(m.Groups["name"].Value, type, attrs.Contains("Mandatory", StringComparison.OrdinalIgnoreCase),
                    m.Groups["default"].Success ? m.Groups["default"].Value.Trim().Trim('\'', '"') : null, null,
                    set.Success ? set.Groups[1].Value.Split(',').Select(v => v.Trim().Trim('\'', '"')).ToList() : null, null, null, null,
                    type.Equals("switch", StringComparison.OrdinalIgnoreCase), type.Equals("securestring", StringComparison.OrdinalIgnoreCase)));
            }
        }
        var findings = new List<ScriptFinding>();
        if (!text.Contains("[CmdletBinding(", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new ScriptFinding("CmdletBinding", "No [CmdletBinding()]: the script can't take -Verbose or -ErrorAction."));
        }
        if (text.Contains("Invoke-Expression", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new ScriptFinding("InvokeExpression", "Invoke-Expression runs strings as code."));
        }
        return new ParsedScript(parameters, null, [], findings);
    }
}
