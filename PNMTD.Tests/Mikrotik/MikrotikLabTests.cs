using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Enums;

namespace PNMTD.Tests.Mikrotik;

/// <summary>
/// Against real RouterOS (docs/mikrotik-plan.md §14, the lab): opt-in with PNMTD_CHR_LAB naming lab
/// routers that <c>tools/chr-lab.sh start</c> runs, for example <c>PNMTD_CHR_LAB=r13,r25</c>. Each
/// router is added through the API in Direct SSH mode, gets the join script PNMTD generates (imported
/// as an admin would), and is polled over real SSH.
/// </summary>
[TestClass]
[TestCategory("Lab")]
public sealed class MikrotikLabTests
{
    private static readonly string Repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [TestMethod]
    public async Task Join_and_poll_real_routers()
    {
        var lab = Environment.GetEnvironmentVariable("PNMTD_CHR_LAB");
        if (string.IsNullOrWhiteSpace(lab))
        {
            Assert.Inconclusive("PNMTD_CHR_LAB is not set: no lab routers.");
        }
        using var app = new TestApp();
        var user = app.Client(Roles.User);
        var host = await (await user.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"lab-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();
        foreach (var name in lab.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var port = int.Parse(Lab("port", name).Trim());
            var sensor = await (await user.PostJsonAsync("/api/v1/sensors", new SensorRequest
            {
                HostId = host.Id,
                Type = SensorType.MIKROTIK,
                Name = $"lab {name}",
                Interval = 300,
                GracePeriod = 600,
                // From this host, the lab router sees Podman's gateway as the source (M0).
                Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = "127.0.0.1", SshPort = port, SourceAddress = "192.168.127.1" },
            })).ReadAsync<SensorDetail>();
            var script = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/join-script")).ReadAsync<MikrotikScript>();
            var file = Path.Combine(Path.GetTempPath(), $"pnmt-join-{name}.rsc");
            await File.WriteAllTextAsync(file, script.Script);
            StringAssert.Contains(Lab("import", name, file), "PNMT: the user pnmt is ready.");

            var outcome = await app.Services.GetRequiredService<RouterPollService>().PollAsync(sensor.Id, wait: true, CancellationToken.None);
            Assert.IsTrue(outcome.Succeeded, $"{name}: {outcome.Message}");
            Assert.AreEqual(PNMTStatusCodes.MIKROTIK_OK, outcome.Code, name);
            Assert.AreEqual(name, outcome.Reading!.Identity);
            CollectionAssert.AreEquivalent(new[] { "ssh", "read" }, outcome.Reading.GroupPolicies.ToArray(), name);

            await using var db = app.NewDb();
            var router = await db.MikrotikRouters.SingleAsync(r => r.SensorId == sensor.Id);
            Assert.IsNotNull(router.HostKeyFingerprint, name);
            Assert.AreEqual(MikrotikSetupState.Ready, router.SetupState);
            var detail = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
            Assert.IsFalse(detail.Mikrotik!.ScriptOutdated, $"{name}: {string.Join("; ", detail.Mikrotik.ScriptDifferences)}");

            // The removal script leaves nothing behind, and PNMT's login is gone.
            var removal = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/removal-script")).ReadAsync<MikrotikScript>();
            await File.WriteAllTextAsync(file, removal.Script);
            StringAssert.Contains(Lab("import", name, file), "PNMT: removed.");
            var after = await app.Services.GetRequiredService<RouterPollService>().PollAsync(sensor.Id, wait: true, CancellationToken.None);
            Assert.AreEqual(PNMTStatusCodes.MIKROTIK_ACCESS_DENIED, after.Code, $"{name}: {after.Message}");
        }
    }

    private static string Lab(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Repository, "tools", "chr-lab.sh")) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}
