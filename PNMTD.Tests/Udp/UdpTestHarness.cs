using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Models.Enum;
using PNMTD.Lib.Udp;
using PNMTD.Models.Enums;
using PNMTD.Udp;

namespace PNMTD.Tests.Udp;

/// <summary>
/// A TestApp with the UDP listener on a free port, and a device that talks to it over loopback:
/// real datagrams through the real socket, processor and PostgreSQL.
/// </summary>
internal sealed class UdpTestHarness : IDisposable
{
    public TestApp App { get; }

    public int Port { get; }

    public Guid HostId { get; }

    public HttpClient Admin { get; }

    public UdpTestHarness(IReadOnlyDictionary<string, string?>? settings = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Udp:Enabled"] = "true",
            ["Udp:Port"] = "0",
            ["Udp:RateLimit:PerNodeBurst"] = "100000",
            ["Udp:RateLimit:PerNodePerMinute"] = "6000000",
            ["Udp:RateLimit:PerAddressPerSecond"] = "100000",
            ["Udp:JoinIntervalSeconds"] = "0",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }
        App = new TestApp(settings: all);
        Admin = App.Client(global::PNMTD.Tests.Roles.Admin);
        var state = App.Services.GetRequiredService<UdpListenerState>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!state.Listening && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
        Assert.IsTrue(state.Listening, $"The UDP listener did not start: {state.Error}");
        Port = state.Port!.Value;
        var host = Admin.PostJsonAsync("/api/v1/hosts", new { name = $"udp-test-{Guid.NewGuid():N}"[..20], enabled = true }).Result
            .ReadAsync<HostDetail>().Result;
        HostId = host.Id;
    }

    public void Dispose()
    {
        Admin.Dispose();
        App.Dispose();
    }

    /// <summary>Creates a UDP node through the API; the node's generated key and token come from the integration.</summary>
    public async Task<(SensorDetail Sensor, UdpDevice Device)> CreateNodeAsync(KeyFamily family = KeyFamily.HmacSha256,
        SecuritySuite[]? suites = null, UdpSessionMode session = UdpSessionMode.None, SecuritySuite? responseSuite = null,
        UdpAckMode ack = UdpAckMode.OnRequest, long? deviceId = null, string? key = null, int interval = 600)
    {
        var request = new
        {
            hostId = HostId,
            type = SensorType.UDP_NODE,
            name = $"node {Guid.NewGuid():N}"[..13],
            enabled = true,
            interval,
            gracePeriod = 300,
            udp = new
            {
                deviceId,
                keyFamily = family,
                allowedSuites = suites ?? [SecuritySuite.HmacSha256_32, SecuritySuite.HmacSha256_64],
                sessionMode = session,
                ackMode = ack,
                replayProtection = true,
                responseSuite,
                key,
            },
        };
        var response = await Admin.PostJsonAsync("/api/v1/sensors", request);
        var sensor = await response.ReadAsync<SensorDetail>();
        Assert.IsNotNull(sensor.Udp);
        Assert.IsNotNull(sensor.Integration?.Udp);
        var device = new UdpDevice((uint)sensor.Udp!.DeviceId, family, Convert.FromHexString(sensor.Integration!.Udp!.Key),
            Convert.FromHexString(sensor.Integration.Udp.Token));
        return (sensor, device);
    }

    /// <summary>Sends a datagram and waits briefly for an answer; null when none comes.</summary>
    public async Task<byte[]?> SendAsync(byte[] datagram, int waitMs = 1500)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(IPAddress.Loopback, Port);
        await client.SendAsync(datagram);
        using var timeout = new CancellationTokenSource(waitMs);
        try
        {
            var result = await client.ReceiveAsync(timeout.Token);
            return result.Buffer;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Sends an uplink and opens the answer as the device would; fails the test when the answer does not verify.</summary>
    public async Task<Downlink?> ExchangeAsync(UdpDevice device, Uplink uplink, int waitMs = 1500)
    {
        var answer = await SendAsync(uplink.Datagram, waitMs);
        if (answer is null)
        {
            return null;
        }
        var downlink = device.Open(answer, uplink, out var why);
        Assert.IsNotNull(downlink, $"The answer does not verify: {why}");
        return downlink;
    }

    public async Task<List<Models.Db.EventEntity>> EventsAsync(Guid sensorId)
    {
        await using var db = App.NewDb();
        return await db.Events.AsNoTracking().Where(e => e.SensorId == sensorId).OrderBy(e => e.Created).ToListAsync();
    }

    public async Task<T> WithDbAsync<T>(Func<Data.PnmtdDbContext, Task<T>> query)
    {
        await using var db = App.NewDb();
        return await query(db);
    }
}
