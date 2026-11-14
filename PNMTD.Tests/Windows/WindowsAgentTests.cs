using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Cli;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Tests.Agents;

namespace PNMTD.Tests.Windows;

/// <summary>
/// Windows servers, what is Windows' own on PNMTD's side (docs/windows-sensor-plan.md §13; docs/linux-sensor-plan.md §3.3): the
/// typed sections stored once per hash, WMI's settings and queries, and the Windows differs with 253 and 453. The shared agent
/// infrastructure is tested in PNMTD.Tests/Agents.
/// </summary>
[TestClass]
public sealed class WindowsAgentTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Agents:JoinRateLimitPerMinute"] = "1000",
            ["Agents:HelloTimeoutSeconds"] = "2",
            ["Agents:MaxMessageBytes"] = "65536",
            // No GitHub in the tests.
            ["Agents:AgentDownloadBaseUrl"] = "http://127.0.0.1:9/releases/download",
        });
        admin = app.Client(Roles.Admin);
        hostId = await AgentHarness.CreateHostAsync(admin, "windows-tests");
    }

    /// <summary>The servers' state changes would crowd other classes' dashboards (the newest 15 changes): their host goes off.</summary>
    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            await db.Hosts.Where(h => h.Id == hostId).ExecuteUpdateAsync(u => u.SetProperty(h => h.Enabled, false));
        }
        app.Dispose();
    }

    [TestCleanup]
    public void Thaw() => Clock.Provider = TimeProvider.System;

    [TestMethod]
    public async Task WmiSettingsReachTheAgentAndItsQueriesComeBack()
    {
        var created = await AgentHarness.TokenAsync(admin, hostId, "wmi");
        var simulator = new AgentSimulator(new Uri("http://localhost/"), app.Client(), ct => AgentHarness.ConnectAsync(app, ct), TextWriter.Null);
        var state = await simulator.JoinAsync(created.Token, null, "SIM-WMI", CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        await AgentHarness.EventuallyAsync(() => Task.FromResult(app.Services.GetRequiredService<AgentHub>().IsConnected(state.SensorId)));
        var url = $"/api/v1/windows/servers/{state.SensorId}/wmi";

        // Only read-only SELECTs, in a namespace under root.
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PutJsonAsync(url, new WmiConfig([],
            [new CustomWmiQuery(Guid.Empty, "bad", @"root\cimv2", "DELETE FROM Win32_Process", 5, 15, true)]))).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PutJsonAsync(url, new WmiConfig([],
            [new CustomWmiQuery(Guid.Empty, "bad", "cimv2", "SELECT * FROM Win32_Process", 5, 15, true)]))).StatusCode);

        var service = WmiQueries.Id(@"root\cimv2", "Win32_Service");
        var saved = await (await admin.PutJsonAsync(url, new WmiConfig([new WmiQueryOverride(service, false, null)],
            [new CustomWmiQuery(Guid.Empty, "Shares", @"root\cimv2", "SELECT Name, Path FROM Win32_Share", 5, 20, true)]))).ReadAsync<WindowsWmiView>();
        var own = saved.Settings.Custom.Single();
        Assert.AreNotEqual(Guid.Empty, own.Id, "PNMT gives an own query its id");
        await AgentHarness.EventuallyAsync(() => Task.FromResult(simulator.LastConfig?.Wmi?.Custom.Count == 1));
        Assert.IsTrue((await (await admin.PostJsonAsync($"/api/v1/agents/servers/{state.SensorId}/collect", new AgentCollectRequest([SectionNames.Wmi])))
            .ReadAsync<AgentRequestResult>()).Ok);
        var view = await AgentHarness.EventuallyAsync(async () => await (await admin.GetAsync(url)).ReadAsync<WindowsWmiView>(),
            v => v.Section?.Custom.Count == 1);
        Assert.AreEqual("off", view.Section!.Queries.Single(q => q.Id == service).Status);
        Assert.AreEqual("SELECT Name, Path FROM Win32_Share", view.Section.Custom.Single().Rows.Single()[1]);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).GetAsync(url)).StatusCode, "Admin only");
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/windows/servers/{state.SensorId}/sections/wmi")).StatusCode);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task SectionsAreStoredOncePerHashAndServedTyped()
    {
        var created = await AgentHarness.TokenAsync(admin, hostId, "sections");
        var simulator = new AgentSimulator(new Uri("http://localhost/"), app.Client(), ct => AgentHarness.ConnectAsync(app, ct), TextWriter.Null);
        var state = await simulator.JoinAsync(created.Token, null, "SIM-SECTIONS", CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var running = simulator.RunAsync(state, stop.Token, TimeSpan.FromSeconds(30));
        var rows = await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.AgentSections.AsNoTracking().Where(s => s.SensorId == state.SensorId).ToListAsync();
        }, r => r.Count == SectionNames.Windows.Count);
        var viewer = app.Client(Roles.Viewer);
        var system = await (await viewer.GetAsync($"/api/v1/windows/servers/{state.SensorId}/sections/system")).ReadAsync<WindowsSectionView>();
        Assert.AreEqual("SIM-SECTIONS", system.System?.Hostname);
        Assert.AreEqual("17763", system.System?.Os.Build);
        Assert.IsNull(system.Hardware);
        var network = await (await viewer.GetAsync($"/api/v1/windows/servers/{state.SensorId}/sections/network")).ReadAsync<WindowsSectionView>();
        Assert.AreEqual(3389, network.Network?.Listeners.Single(l => l.Services.Contains("TermService")).Port);

        // The same hash again only moves the times.
        var changedAt = rows.Single(r => r.Section == "hardware").ChangedAt;
        Assert.IsTrue((await (await app.Client(Roles.User).PostJsonAsync($"/api/v1/agents/servers/{state.SensorId}/collect",
            new AgentCollectRequest(["hardware"]))).ReadAsync<AgentRequestResult>()).Ok);
        var again = await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.AgentSections.AsNoTracking().SingleAsync(s => s.SensorId == state.SensorId && s.Section == "hardware");
        }, r => r.ReceivedAt > rows.Single(x => x.Section == "hardware").ReceivedAt);
        Assert.AreEqual(changedAt, again.ChangedAt);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/v1/windows/servers/{state.SensorId}/sections/access")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/windows/servers/{state.SensorId}/sections/nonsense")).StatusCode);
        var detail = await (await viewer.GetAsync($"/api/v1/agents/servers/{state.SensorId}")).ReadAsync<AgentServerDetail>();
        CollectionAssert.AreEquivalent(SectionNames.Windows.ToList(), detail.Sections.Select(s => s.Name).ToList());
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task AChangedSectionWritesReadableChangesAnd253()
    {
        var joined = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null)).Token, "SRV-CHANGES");
        await (await app.Client(Roles.User).PutJsonAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/settings",
            new AgentServerSettings(true, true, 10, true))).ReadAsync<AgentServerDetail>();
        await using var session = await AgentHarness.OpenAsync(app, joined);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.Services, SimulatedSections.Services()), CancellationToken.None);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.RemoteAccess, SimulatedSections.RemoteAccess()), CancellationToken.None);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.EventLog, SimulatedSections.EventLog()), CancellationToken.None);
        await AgentHarness.EventuallyAsync(async () =>
        {
            await using var db = app.NewDb();
            return await db.AgentSections.CountAsync(s => s.SensorId == joined.Grant.SensorId) == 3;
        });
        var list = await (await admin.GetAsync($"/api/v1/agents/servers?search=SRV-CHANGES")).ReadAsync<Paged<AgentServerItem>>();
        Assert.AreEqual("RDP · WinRM · SMB · AnyDesk open", list.Items.Single().RemoteAccess);

        var services = SimulatedSections.Services();
        var changed = new ServicesSection([.. services.Services, new ServiceEntry("TeamViewer", "TeamViewer", "Running", "Automatic", "LocalSystem",
            @"C:\Program Files\TeamViewer\TeamViewer_Service.exe", null, "15.0", "TeamViewer Germany GmbH", "TeamViewer Germany GmbH", ServiceClass.ThirdParty, false)]);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.Services, changed), CancellationToken.None);
        var remote = SimulatedSections.RemoteAccess();
        var rdpOff = remote with { Features = remote.Features.Select(f => f.Key == "rdp" ? f with { Active = false } : f).ToList() };
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.RemoteAccess, rdpOff), CancellationToken.None);
        var critical = new EventLogSection([new EventEntry(DateTimeOffset.UtcNow, "System", "Critical", "Kernel-Power", 41, null,
            "The system has rebooted without cleanly shutting down first.", null, 99999), .. SimulatedSections.EventLog().Entries]);
        await session.SendAsync(AgentMessageTypes.Section, AgentHarness.Section(SectionNames.EventLog, critical), CancellationToken.None);

        var changes = await AgentHarness.EventuallyAsync(async () =>
            await (await app.Client(Roles.Viewer).GetAsync($"/api/v1/agents/servers/{joined.Grant.SensorId}/changes")).ReadAsync<Paged<AgentChangeItem>>(),
            p => p.TotalCount >= 2);
        CollectionAssert.IsSubsetOf(new[] { "Service TeamViewer added (third-party, automatic, running)", "Remote Desktop (RDP) disabled" },
            changes.Items.Select(c => c.Summary).ToList());
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, joined.Grant.SensorId),
            e => e.Count(x => x.Code == PNMTStatusCodes.WINDOWS_CHANGED) == 2 && e.Any(x => x.Code == PNMTStatusCodes.WINDOWS_CRITICAL_EVENT));
        Assert.AreEqual("Kernel-Power 41: The system has rebooted without cleanly shutting down first.",
            events.Single(e => e.Code == PNMTStatusCodes.WINDOWS_CRITICAL_EVENT).Message);
    }

    [TestMethod]
    public void TheDiffersNameWhatChanged()
    {
        string Json<T>(T value) => AgentJson.Serialize(value);
        var network = SimulatedSections.Network();
        var moved = network with { Adapters = network.Adapters.Select(a => a with { Addresses = ["10.0.1.21/24", .. a.Addresses.Skip(1)], DnsServers = ["10.0.1.10"] }).ToList() };
        var lines = PNMTD.Windows.WindowsDiffers.Compare(SectionNames.Network, Json(network), Json(moved)).Select(c => c.Summary).ToList();
        CollectionAssert.Contains(lines, "10.0.1.20/24 → 10.0.1.21/24 on Ethernet0");
        CollectionAssert.Contains(lines, "DNS servers on Ethernet0: 10.0.1.10, 10.0.1.11 → 10.0.1.10");

        var system = SimulatedSections.System("SRV");
        var updated = system with { Os = system.Os with { Ubr = 6659 }, LocalAdministrators = [.. system.LocalAdministrators, new LocalAdmin(@"SIM\jdoe", "User")] };
        lines = PNMTD.Windows.WindowsDiffers.Compare(SectionNames.System, Json(system), Json(updated)).Select(c => c.Summary).ToList();
        CollectionAssert.Contains(lines, "Windows updated: build 17763.6532 → 17763.6659");
        CollectionAssert.Contains(lines, @"New local administrator SIM\jdoe");

        var access = SimulatedSections.Access();
        var off = access with { AuditPolicy = access.AuditPolicy.Select(a => a with { Success = false }).ToList() };
        lines = PNMTD.Windows.WindowsDiffers.Compare(SectionNames.Access, Json(access), Json(off)).Select(c => c.Summary).ToList();
        CollectionAssert.Contains(lines, "Logon success auditing turned off");
    }

}
