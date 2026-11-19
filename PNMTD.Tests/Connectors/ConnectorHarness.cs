using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Cli;
using PNMTD.Connectors;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;
using PNMTD.Tests.Agents;

namespace PNMTD.Tests.Connectors;

/// <summary>
/// The connectors' tests' fixture (docs/connectors-plan.md §16): the caller's address from a test header (the test server has none), a
/// simulated server with scripts allowed, connectors and callers through the admin API, and clients that call as a caller.
/// </summary>
internal static class ConnectorHarness
{
    public const string AddressHeader = "X-Test-Address";

    /// <summary>Where the tests' callers call from, unless a test says otherwise.</summary>
    public const string Office = "10.20.0.5";

    public const string BackupScript = """
        #!/usr/bin/env bash
        # pnmt:
        #   synopsis: Starts a backup job now
        #   parameters:
        #     job:     { type: string, mandatory: true, set: [nightly, weekly, monthly], help: "The job" }
        #     note:    { type: string, help: "Why" }
        #     keep:    { type: int, default: 7, range: [1, 90] }
        #     dry_run: { type: bool }
        #     token:   { type: secret, help: "The backup server's token" }
        set -euo pipefail
        echo "started ${PNMT_JOB}"
        """;

    /// <summary>The settings of a connectors test app; <paramref name="extra"/> on top.</summary>
    public static Dictionary<string, string?> Settings(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Agents:JoinRateLimitPerMinute"] = "1000",
            ["Agents:HelloTimeoutSeconds"] = "2",
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }
        return settings;
    }

    /// <summary>The test header becomes the connection's address; without it, <see cref="Office"/>.</summary>
    public static void Install(IServiceCollection services) => services.AddSingleton<IStartupFilter, AddressFilter>();

    private sealed class AddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue(AddressHeader, out var value) && IPAddress.TryParse(value.ToString(), out var ip)
                    ? ip
                    : IPAddress.Parse(Office);
                return following(context);
            });
            next(app);
        };
    }

    /// <summary>A server with a connected simulator and scripts allowed; its name as PNMT shows it ("host / sensor").</summary>
    public sealed record Server(AgentSimulator Simulator, SimulatedAgentState State, string Name, string HostName, CancellationTokenSource Stop, Task Running)
        : IAsyncDisposable
    {
        public Guid SensorId => State.SensorId;

        public async ValueTask DisposeAsync()
        {
            await Stop.CancelAsync();
            try
            {
                await Running;
            }
            catch (OperationCanceledException)
            {
            }
            Stop.Dispose();
        }
    }

    public static async Task<Server> ServerAsync(TestApp app, HttpClient admin, string prefix, AgentPlatform platform = AgentPlatform.Linux,
        Action<AgentSimulator>? configure = null, TextWriter? output = null)
    {
        var hostId = await AgentHarness.CreateHostAsync(admin, prefix);
        var simulator = new AgentSimulator(new Uri("http://localhost/"), app.Client(), ct => AgentHarness.ConnectAsync(app, ct), output ?? TextWriter.Null, platform)
        {
            AllowScripts = true,
        };
        configure?.Invoke(simulator);
        var token = (await AgentHarness.TokenAsync(admin, hostId, $"{prefix}-agent", platform)).Token;
        var state = await simulator.JoinAsync(token, null, $"{prefix}-sim", CancellationToken.None);
        var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        var hub = app.Services.GetRequiredService<AgentHub>();
        await AgentHarness.EventuallyAsync(() => Task.FromResult(hub.Connection(state.SensorId)?.Capabilities.Contains(AgentCapabilities.Scripts) == true));
        await using var db = app.NewDb();
        var names = await db.Sensors.AsNoTracking().Where(s => s.Id == state.SensorId).Select(s => new { Host = s.Parent.Name, s.Name }).SingleAsync();
        return new Server(simulator, state, $"{names.Host} / {names.Name}", names.Host, stop, running);
    }

    public static ConnectorRequest Request(string name, string server, string text = BackupScript, string scriptName = "backup.sh",
        ConnectorType type = ConnectorType.LinuxScript, ConnectorMode mode = ConnectorMode.Wait, IReadOnlyList<Guid>? callers = null, string? function = null) => new()
    {
        Type = type, Name = name, Slug = Slug(name), Purpose = $"{name}: the tests' connector, which starts a backup job.",
        Target = new ConnectorTargetRequest
        {
            Server = server, ScriptName = scriptName, ScriptText = text, RunAsKind = RunAsKind.Service, TimeoutSeconds = 120, Function = function,
            Shell = type == ConnectorType.LinuxScript ? "bash" : "powershell",
        },
        Mode = mode, CallerIds = callers?.ToList(),
    };

    public static string Slug(string name) => new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>Creates a connector, exposes what <paramref name="arguments"/> says (the rest as created), and enables it.</summary>
    public static async Task<ConnectorDetail> ConnectorAsync(HttpClient admin, ConnectorRequest request, IReadOnlyList<ConnectorArgumentRequest>? arguments = null,
        bool enable = true, Action<ConnectorRequest>? change = null)
    {
        var created = await (await admin.PostJsonAsync("/api/v1/connectors", request)).ReadAsync<ConnectorDetail>();
        if (!enable && arguments is null)
        {
            return created;
        }
        var update = Update(created, enabled: enable, arguments: arguments);
        update.WaitSeconds = 15;
        change?.Invoke(update);
        return await (await admin.PutJsonAsync($"/api/v1/connectors/{created.Id}", update)).ReadAsync<ConnectorDetail>();
    }

    /// <summary>A PUT that keeps everything as it is, but what the arguments say.</summary>
    public static ConnectorRequest Update(ConnectorDetail c, bool? enabled = null, IReadOnlyList<ConnectorArgumentRequest>? arguments = null, string? text = null,
        bool? returnOutput = null, ConnectorMode? mode = null, int? maxQueued = null, int? maxCallsPerHour = null, bool? review = null,
        ConnectorReviewFallback? fallback = null, string? server = null, IReadOnlyList<Guid>? callers = null) => new()
    {
        Name = c.Name, Slug = c.Slug, Purpose = c.Purpose, Enabled = enabled ?? c.Enabled,
        Target = new ConnectorTargetRequest
        {
            Server = server ?? c.Target.Server, ScriptName = c.Target.ScriptName, ScriptText = text ?? c.Target.ScriptText, TakenFrom = c.Target.TakenFrom,
            Function = c.Target.Function, RunAsKind = c.Target.RunAsKind, Account = c.Target.Account, Shell = c.Target.Shell, TimeoutSeconds = c.Target.TimeoutSeconds,
        },
        Arguments = arguments?.ToList(), Mode = mode ?? c.Mode, WaitSeconds = c.WaitSeconds, ReturnOutput = returnOutput ?? c.ReturnOutput,
        MaxCallsPerHour = maxCallsPerHour ?? c.MaxCallsPerHour, MaxQueued = maxQueued ?? c.MaxQueued, MaxQueueSeconds = c.MaxQueueSeconds,
        ReviewEnabled = review ?? c.Review.Enabled, ReviewPurpose = c.Review.Purpose, ReviewThreshold = c.Review.Threshold, ReviewChecks = c.Review.Checks.ToList(),
        ReviewFallback = fallback ?? c.Review.Fallback, CallerIds = callers?.ToList(),
    };

    /// <summary>The backup script's arguments: job exposed with a pattern, note multi-line, keep fixed at 3, the token exposed, dry_run omitted.</summary>
    public static IReadOnlyList<ConnectorArgumentRequest> BackupArguments { get; } =
    [
        new() { Name = "job", Mode = ConnectorArgumentMode.Exposed, Required = true, Pattern = "[a-z]{5,10}", Description = "The job to start." },
        new() { Name = "note", Mode = ConnectorArgumentMode.Exposed, Multiline = true },
        new() { Name = "keep", Mode = ConnectorArgumentMode.Fixed, FixedValue = JsonSerializer.SerializeToElement(3) },
        new() { Name = "dry_run", Mode = ConnectorArgumentMode.Omitted },
        new() { Name = "token", Mode = ConnectorArgumentMode.Exposed },
    ];

    public static async Task<ConnectorCallerCreated> CallerAsync(HttpClient admin, string name, IReadOnlyList<Guid> connectors, params string[] networks) =>
        await (await admin.PostJsonAsync("/api/v1/connector-callers", new ConnectorCallerRequest
        {
            Name = $"{name} {Guid.NewGuid():N}"[..Math.Min(name.Length + 33, 200)], Enabled = networks.Length > 0,
            Networks = networks.Select(n => new ConnectorNetworkRequest { Cidr = n }).ToList(), ConnectorIds = connectors.ToList(),
        })).ReadAsync<ConnectorCallerCreated>();

    /// <summary>A client that calls as a caller: the key in the header, from <paramref name="from"/>.</summary>
    public static HttpClient Client(TestApp app, string? key, string from = Office)
    {
        var client = app.Client();
        if (key is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        client.DefaultRequestHeaders.Add(AddressHeader, from);
        return client;
    }

    public static Task<HttpResponseMessage> CallAsync(HttpClient client, string slug, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/connector/v1/{slug}") { Content = JsonContent(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return client.SendAsync(request);
    }

    private static HttpContent JsonContent(object body) =>
        new StringContent(body as string ?? JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
