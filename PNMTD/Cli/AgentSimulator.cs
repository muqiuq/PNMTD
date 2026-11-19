using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using PNMTD.Lib.Agent;

namespace PNMTD.Cli;

/// <summary>What a simulated agent keeps between runs: PNMT, its identity and key, PNMTD's pinned key, and its platform.</summary>
public sealed record SimulatedAgentState(string Url, Guid AgentId, Guid SensorId, string PrivateKey, string PnmtdKey, string Hostname,
    AgentPlatform Platform = AgentPlatform.Windows);

/// <summary>
/// A fake agent (docs/windows-sensor-plan.md §11; docs/linux-sensor-plan.md §9.4), for development without the server and for
/// the tests: it joins with a token, connects with the same session code as the real agent, sends heartbeats and canned
/// sections and logins, and answers collect, run-diagnostics and send-log. A Windows agent (<see cref="SimulatedSections"/>), or a
/// Linux one (<see cref="SimulatedLinux"/>: a Debian 12 server's eight sections, SSH key logins, sudo, file transfers, failures and
/// the internet's invalid users), whose hello carries the rights <see cref="AllowKeyRemoval"/> and <see cref="AllowScripts"/> give.
/// </summary>
public sealed class AgentSimulator(Uri baseUrl, HttpClient http, Func<CancellationToken, Task<WebSocket>> connect, TextWriter output,
    AgentPlatform platform = AgentPlatform.Windows) : IAgentSessionHandler
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

    /// <summary>Whether an administrator allowed scripts on this (simulated) server (§7.2; Linux: config.yaml's scripts.enabled): off, as on a real one.</summary>
    public bool AllowScripts { get; set; }

    /// <summary>An agent from before functions in scripts (docs/connectors-plan.md §8.3, <c>--no-functions</c>): neither function capability.</summary>
    public bool NoFunctions { get; set; }

    /// <summary>
    /// For the tests: a run is acked at once, and its result waits until this completes (a run that takes its time). Null: the result
    /// comes before the ack, as a quick script's does.
    /// </summary>
    public Func<RunScriptRequest, Task>? HoldRuns { get; set; }

    /// <summary>Linux: whether config.yaml allows PNMT to remove keys (docs/linux-sensor-plan.md §4.6), for deploy and www: off, as on a real one.</summary>
    public bool AllowKeyRemoval { get; set; }

    public AgentPlatform Platform { get; } = platform;

    private bool Linux => Platform == AgentPlatform.Linux;

    /// <summary>The OS this simulator reports: Windows Server 2019, or Debian 12.</summary>
    public AgentOs CurrentOs => Linux ? SimulatedLinux.Os : Os;

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
        // Linux (§4.4): no domain, and /etc/machine-id's 32 hex digits.
        var request = new JoinRequest(token, AgentSigning.PublicKey(key), hostname, $"{hostname.ToLowerInvariant()}.sim.example", Linux ? null : "SIM", CurrentOs,
            AgentVersion, "x64", Linux ? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString(), name, Platform);
        var (response, grant) = await AgentJoinClient.JoinAsync(http, baseUrl, request, ct);
        Write($"Joined as {grant.HostName} / {grant.SensorName}: agent {grant.AgentId}, this key {grant.AgentKeyFingerprint}");
        return new SimulatedAgentState(baseUrl.ToString(), grant.AgentId, grant.SensorId, Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            response.SigningPublicKey, hostname, Platform);
    }

    /// <summary>Connects and reports until <paramref name="ct"/> ends, reconnecting after a second; <paramref name="heartbeats"/> stops after so many.</summary>
    public async Task<AgentSessionEnd> RunAsync(SimulatedAgentState state, CancellationToken ct, TimeSpan? interval = null, int? heartbeats = null)
    {
        hostname = state.Hostname;
        if (state.Platform != Platform)
        {
            throw new InvalidOperationException($"The state is a {state.Platform} agent's, and this simulator is a {Platform} agent.");
        }
        using var agentKey = ECDsa.Create();
        agentKey.ImportPkcs8PrivateKey(Convert.FromBase64String(state.PrivateKey), out _);
        using var pnmtdKey = AgentSigning.ImportPublicKey(state.PnmtdKey) ?? throw new InvalidOperationException("The state's PNMTD key is invalid.");
        var capabilities = Linux ? SimulatedLinux.Capabilities(AllowKeyRemoval, AllowScripts, !NoFunctions)
            : Capabilities.Append(AgentCapabilities.Parse).Concat(NoFunctions ? [] : [AgentCapabilities.ParseFunctions])
                .Concat(AllowScripts ? [AgentCapabilities.Scripts] : Array.Empty<string>())
                .Concat(AllowScripts && !NoFunctions ? [AgentCapabilities.ScriptFunctions] : Array.Empty<string>()).ToList();
        var identity = new AgentIdentity(state.AgentId, agentKey, pnmtdKey, AgentSigning.Origin(baseUrl), AgentVersion, "x64", CurrentOs, capabilities,
            Convert.ToBase64String(encryption.ExportSubjectPublicKeyInfo()), Platform, Linux ? SimulatedLinux.Rights(AllowKeyRemoval, AllowScripts) : null);
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
        await current.SendAsync(AgentMessageTypes.Access, Linux ? SimulatedLinux.Logins(hostname) : SimulatedSections.Logins(hostname), ct);
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

    public Heartbeat Heartbeat(AgentSession current) => Linux ? SimulatedLinux.Heartbeat(hostname, current.Config.ConfigVersion, AgentVersion, AllowKeyRemoval, removedKeys)
        : new(DateTimeOffset.UtcNow, bootTime, 7.5 + Random.Shared.NextDouble() * 10, 41.2, "C:", 63.5,
            [new SessionInfo("SIM\\admin", "Rdp", "Active", "10.0.4.17")], false, SimulatedSections.Hashes(hostname), current.Config.ConfigVersion, AgentVersion, 0,
            hostname, "Restarted for updates (planned)");

    private Task SendSectionsAsync(AgentSession current, IReadOnlyList<string> names, CancellationToken ct) => Linux
        ? SimulatedLinux.SendAsync(current, hostname, names, AllowKeyRemoval, ct, removedKeys)
        : SimulatedSections.SendAsync(current, hostname, names, ct, LastConfig ?? current.Config);

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
        if (Linux && await OnLinuxRequestAsync(current, request, ct) is { } answered)
        {
            return answered;
        }
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
                var simulated = SimulatedParser.Parse(parse.Text);
                await current.SendAsync(AgentMessageTypes.ParseResult, new ParseResultMessage(request.RequestId, parse.Hash,
                    NoFunctions ? simulated with { Functions = null } : simulated, null), ct);
                return new Ack(request.RequestId, true);
            case AgentMessageTypes.RunScript:
                if (!AllowScripts)
                {
                    return new Ack(request.RequestId, false, "Scripts are not allowed on this server.");
                }
                var script = AgentJson.Deserialize<RunScriptRequest>(request.Body)!;
                if (script.Function is { } function && (NoFunctions || SimulatedParser.Parse(script.Text).Function(function) is null))
                {
                    // An agent without functions would never be sent one (§8.3); this one checks the name against its own parse.
                    return new Ack(request.RequestId, false, $"The script defines no function {function} at its top level.");
                }
                var started = DateTimeOffset.UtcNow;
                var password = script.RunAs.Password is { } secret ? AgentSecrets.Decrypt(encryption, secret, script.RunId) : null;
                var secure = script.SecureArguments.ToDictionary(p => p.Key, p => AgentSecrets.Decrypt(encryption, p.Value, script.RunId));
                var output = (script.Function is { } called ? $"ran {called} with {string.Join(", ", script.Arguments.Select(a => $"{a.Key}={a.Value.GetRawText()}"))}\n" : "") +
                    $"Hello from {hostname} as {script.RunAs.Account ?? "SYSTEM"}\n" +
                    string.Join("\n", script.Arguments.Select(a => $"{a.Key}={a.Value.GetRawText()}")) +
                    string.Join("", secure.Select(p => $"\n{p.Key} has {p.Value.Length} characters")) +
                    (password is null ? "" : $"\nthe password has {password.Length} characters");
                await ResultAsync(current, script, new ScriptResultMessage(script.RunId, ScriptRunState.Done, 0, output, null, 12, started, DateTimeOffset.UtcNow), ct);
                return new Ack(request.RequestId, true);
            default:
                return new Ack(request.RequestId, false, $"The simulator does not know \"{request.Type}\".");
        }
    }

    /// <summary>A run's result: before the ack, or with <see cref="HoldRuns"/> after it, once the hold ends.</summary>
    private async Task ResultAsync(AgentSession current, RunScriptRequest script, ScriptResultMessage result, CancellationToken ct)
    {
        if (HoldRuns is not { } hold)
        {
            await current.SendAsync(AgentMessageTypes.ScriptResult, result, ct);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await hold(script);
                await current.SendAsync(AgentMessageTypes.ScriptResult, result with { FinishedAt = DateTimeOffset.UtcNow }, ct);
            }
            catch (Exception e)
            {
                Write($"A held run's result was not sent: {e.Message}");
            }
        }, CancellationToken.None);
    }

    /// <summary>The keys this (simulated) server's removals took, as <see cref="SimulatedLinux.Place"/> names them.</summary>
    private readonly HashSet<string> removedKeys = new(StringComparer.Ordinal);

    /// <summary>The secrets of the last bash run, decrypted as the agent would put them into the environment file: for the tests.</summary>
    public IReadOnlyDictionary<string, string> LastSecrets { get; private set; } = new Dictionary<string, string>();

    /// <summary>
    /// What only a Linux agent answers (docs/linux-sensor-plan.md §6.5, §8): null for the shared requests (collect, diagnostics,
    /// send-log), which the switch above answers.
    /// <list type="bullet">
    /// <item><c>remove-ssh-key</c>: refused unless <see cref="AllowKeyRemoval"/> (as config.yaml would); with it, the checks of §6.5
    ///   step 4 that the canned data can make (deploy and www only, a protected key, the path one it reported), then the key goes from
    ///   its file: a <c>key-removal</c> (before the ack), and the ssh-keys section again;</item>
    /// <item><c>parse-script</c>: bash's header with PNMTD.Lib's parser, and a <c>bash -n</c> that finds nothing;</item>
    /// <item><c>run-script</c>: with <see cref="AllowScripts"/>, as root or deploy, it echoes the environment (secrets by their length).</item>
    /// </list>
    /// </summary>
    private async Task<Ack?> OnLinuxRequestAsync(AgentSession current, SignedContent request, CancellationToken ct)
    {
        switch (request.Type)
        {
            case AgentMessageTypes.RemoveSshKey:
                if (!AllowKeyRemoval)
                {
                    return new Ack(request.RequestId, false, "config.yaml doesn't allow PNMT to remove keys here (ssh_keys.remove: false).");
                }
                var removal = AgentJson.Deserialize<RemoveSshKeyRequest>(request.Body)!;
                var result = Remove(request.RequestId, removal);
                Write($"Key removal {removal.RemovalId}: {result.Outcome} ({removal.Account}, {removal.Path}, {removal.Fingerprint})");
                await current.SendAsync(AgentMessageTypes.KeyRemoval, result, ct);
                if (result.Outcome == SshKeyRemovalOutcome.Removed)
                {
                    await SendSectionsAsync(current, [SectionNames.SshKeys], ct);
                }
                // As the agent does: the ack is ok for removed and not found, and says why for a refusal or a failure.
                var done = result.Outcome is SshKeyRemovalOutcome.Removed or SshKeyRemovalOutcome.NotFound;
                return new Ack(request.RequestId, done, done ? null : result.Message);
            case AgentMessageTypes.ParseScript:
                var parse = AgentJson.Deserialize<ParseScriptRequest>(request.Body)!;
                // The header with the shared parser; the simulator's bash -n finds no syntax error.
                await current.SendAsync(AgentMessageTypes.ParseResult, new ParseResultMessage(request.RequestId, parse.Hash, BashScriptHeader.Parse(parse.Text), null), ct);
                return new Ack(request.RequestId, true);
            case AgentMessageTypes.RunScript:
                if (!AllowScripts)
                {
                    return new Ack(request.RequestId, false, "Scripts are off on this server (scripts.enabled: false in /etc/pnmt-agent/config.yaml).");
                }
                var script = AgentJson.Deserialize<RunScriptRequest>(request.Body)!;
                var account = script.RunAs.Kind == RunAsKind.Service ? "root" : script.RunAs.Account ?? "";
                var rights = SimulatedLinux.Rights(AllowKeyRemoval, AllowScripts);
                if (script.Shell != "bash" || !rights.ScriptRunAs.Contains(account))
                {
                    return new Ack(request.RequestId, false, script.Shell != "bash" ? $"This server runs bash, not {script.Shell}." : $"{account} is not in scripts.run_as.");
                }
                var parsed = BashScriptHeader.Parse(script.Text);
                if (script.Function is not null && NoFunctions)
                {
                    return new Ack(request.RequestId, false, "This agent can't call functions.");
                }
                var problems = ScriptArguments.Check(parsed, script.Function, script.Arguments, script.SecureArguments.Keys.ToList()).ToList();
                if (parsed.Errors.Count > 0 || problems.Count > 0)
                {
                    return new Ack(request.RequestId, false, parsed.Errors.Count > 0 ? parsed.Errors[0].Message : problems[0].Message);
                }
                var started = DateTimeOffset.UtcNow;
                var secrets = script.SecureArguments.ToDictionary(p => p.Key, p => AgentSecrets.Decrypt(encryption, p.Value, script.RunId));
                LastSecrets = secrets;
                var environment = script.Arguments.Select(a => $"{BashScriptHeader.EnvironmentName(a.Key)}={(a.Value.ValueKind == JsonValueKind.String ? a.Value.GetString() : a.Value.GetRawText())}")
                    .Concat(secrets.Select(p => $"{BashScriptHeader.EnvironmentName(p.Key)}=({p.Value.Length} characters, secret)")).Order(StringComparer.Ordinal);
                var output = (script.Function is { } called ? $"ran {called}\n" : "") + $"Hello from {hostname} as {account}\n" + string.Join("\n", environment);
                await ResultAsync(current, script, new ScriptResultMessage(script.RunId, ScriptRunState.Done, 0, output, null, 8, started, DateTimeOffset.UtcNow), ct);
                return new Ack(request.RequestId, true);
            default:
                return null;
        }
    }

    /// <summary>A removal against the canned section, with the agent's checks in their order (§6.5 step 4).</summary>
    private SshKeyRemovalResult Remove(string requestId, RemoveSshKeyRequest removal)
    {
        SshKeyRemovalResult Answer(SshKeyRemovalOutcome outcome, string? message, int lines = 0, string? backup = null, string? hash = null) =>
            new(requestId, removal.RemovalId, outcome, lines, backup, hash, message);
        var rights = SimulatedLinux.Rights(AllowKeyRemoval, AllowScripts);
        if (rights.KeyRemovalUsers.Count > 0 && !rights.KeyRemovalUsers.Contains(removal.Account))
        {
            return Answer(SshKeyRemovalOutcome.Refused, $"config.yaml allows removing keys only for {string.Join(", ", rights.KeyRemovalUsers)} (ssh_keys.users).");
        }
        var section = SimulatedLinux.SshKeys(hostname, false, AllowKeyRemoval, removedKeys);
        var file = section.Files.FirstOrDefault(f => f.Account == removal.Account && f.Path == removal.Path);
        if (file is null)
        {
            return Answer(SshKeyRemovalOutcome.Refused, $"{removal.Path} is not a key file this agent found for {removal.Account}.");
        }
        var keys = file.Keys.Where(k => k.Fingerprint == removal.Fingerprint).ToList();
        if (keys.Count == 0)
        {
            return Answer(SshKeyRemovalOutcome.NotFound, $"The key is not in {removal.Path} (gone already).");
        }
        if (keys.Any(k => k.Protected))
        {
            return Answer(SshKeyRemovalOutcome.Refused, "The key is protected on this server (ssh_keys.protect).");
        }
        removedKeys.Add(SimulatedLinux.Place(removal.Account, removal.Path, removal.Fingerprint));
        var after = SimulatedLinux.SshKeys(hostname, false, AllowKeyRemoval, removedKeys).Files.Single(f => f.Account == removal.Account && f.Path == removal.Path);
        return Answer(SshKeyRemovalOutcome.Removed, null, keys.Count,
            $"/var/lib/pnmt-agent/state/key-backups/{removal.Account}/{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Path.GetFileName(removal.Path)}", after.Sha256);
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
            [SectionNames.Wmi] = Wmi(),
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

    public static async Task SendAsync(AgentSession session, string hostname, IReadOnlyList<string> names, CancellationToken ct, AgentConfig? config = null)
    {
        foreach (var (name, canned) in All(hostname).Where(p => names.Count == 0 || names.Contains(p.Key)))
        {
            var data = name == SectionNames.Wmi && config is not null ? Wmi(config) : canned;
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
        new ServiceEntry("PNMTSensor", "PNMT Sensor", "Running", "Delayed", "LocalSystem", @"C:\Program Files\PNMT Sensor\pnmt-sensor.exe", null, AgentSimulator.AgentVersion, "PNMT", null,
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

    /// <summary>
    /// WMI (§16): a few built-in queries as an agent reports them, with PNMT's switches and timeouts applied, SMART not available,
    /// and each own query answering one row with its name and query.
    /// </summary>
    public static WmiSection Wmi(AgentConfig? config = null)
    {
        var settings = config?.Wmi ?? WmiConfig.Empty;
        WmiQueryState Query(string ns, string className, string section, int rows, double ms, string status = "ok", string? error = null)
        {
            var id = WmiQueries.Id(ns, className);
            var setting = settings.Overrides.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
            var on = setting?.Enabled ?? true;
            return new WmiQueryState(id, ns, className, $"SELECT * FROM {className}", section, on, on ? null : "pnmt", setting?.TimeoutSeconds ?? WmiQueries.DefaultTimeoutSeconds,
                Stamp, on && status == "ok" ? rows : null, on ? ms : null, on ? status : "off", on ? error : null);
        }
        return new WmiSection(
        [
            Query(@"root\cimv2", "Win32_ComputerSystem", SectionNames.System, 1, 42),
            Query(@"root\cimv2", "SoftwareLicensingProduct", SectionNames.System, 1, 2350),
            Query(@"root\cimv2", "Win32_Processor", SectionNames.Hardware, 2, 31),
            Query(@"root\wmi", "MSStorageDriver_FailurePredictStatus", SectionNames.Hardware, 0, 12, "not available",
                @"root\wmi: MSStorageDriver_FailurePredictStatus is not available here (NotSupported)"),
            Query(@"root\cimv2", "Win32_Service", SectionNames.Services, 214, 850),
        ],
        settings.Custom.Select(c => c.Enabled
            ? new CustomWmiResult(c.Id, c.Name, c.Namespace, c.Wql, true, null, Stamp, 12, "ok", null, ["Name", "Query"], [[c.Name, c.Wql]], false)
            : new CustomWmiResult(c.Id, c.Name, c.Namespace, c.Wql, false, "pnmt", null, null, "off", null, [], [], false)).ToList(),
        []);
    }

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
        // Functions (docs/connectors-plan.md §8.1): "function Name {", with the first param() block of its body.
        var functions = new List<ScriptFunction>();
        var matches = FunctionHeader().Matches(text);
        for (var i = 0; i < matches.Count; i++)
        {
            var body = text[matches[i].Index..(i + 1 < matches.Count ? matches[i + 1].Index : text.Length)];
            var own = ParametersOf(body);
            functions.Add(new ScriptFunction(matches[i].Groups["name"].Value, text[..matches[i].Index].Count(c => c == '\n') + 1, own, null));
        }
        // The script's own param() comes before its first function.
        var scriptPart = matches.Count > 0 ? text[..matches[0].Index] : text;
        return new ParsedScript(matches.Count > 0 ? ParametersOf(scriptPart) : parameters, null, [], findings, functions);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?m)^function\s+(?<name>[A-Za-z_][A-Za-z0-9_-]*)\s*(\{|\()")]
    private static partial System.Text.RegularExpressions.Regex FunctionHeader();

    private static List<ScriptParameter> ParametersOf(string text)
    {
        var start = text.IndexOf("param(", StringComparison.OrdinalIgnoreCase);
        var parameters = new List<ScriptParameter>();
        if (start < 0)
        {
            return parameters;
        }
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
        return parameters;
    }
}
