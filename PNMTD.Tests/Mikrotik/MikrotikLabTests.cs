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
            // The access watch (docs/cookie-access-plan.md §4.2): the import's admin login, read from the router's own log with the
            // poll; every login line matched; the poll's own session named PNMTD's address, and its login isn't stored.
            var logins = await db.AgentLogins.Where(l => l.SensorId == sensor.Id && l.Channel == PNMTD.Lib.Mikrotik.RouterLogins.Channel).ToListAsync();
            Assert.IsTrue(logins.Any(l => l.User == "admin" && l.Outcome == PNMTD.Lib.Agent.LogonOutcome.Success && l.Kind == PNMTD.Lib.Agent.LogonKind.Ssh),
                $"{name}: the admin's SSH login, from the log: {string.Join("; ", logins.Select(l => $"{l.User} {l.Kind} {l.Outcome} {l.SourceAddress}"))}");
            Assert.IsFalse(logins.Any(l => l.User == PNMTD.Lib.Mikrotik.RouterPoll.UserName), $"{name}: the poll's own login isn't stored");
            Assert.AreEqual(0, router.LogUnmatched, $"{name}: every login line matched a pattern");
            Assert.IsNotNull(router.PnmtAddress, $"{name}: PNMTD's address from the poll's own session");
            Assert.IsNotNull(router.RouterUsers, name);
            StringAssert.Contains(router.RouterUsers, "\"admin\":true", $"{name}: admin is in a group with write");
            var detail = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
            Assert.IsFalse(detail.Mikrotik!.ScriptOutdated, $"{name}: {string.Join("; ", detail.Mikrotik.ScriptDifferences)}");

            // Backups (§6): the first login stored version 1; an unchanged export stores nothing; a change
            // gives version 2, 243, and a diff that shows it.
            Assert.IsNull(outcome.ExportError, $"{name}: {outcome.ExportError}");
            Assert.AreEqual(1, detail.Mikrotik.BackupCount, name);
            var first = detail.Mikrotik.LatestBackup!;
            var text = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/backups/{first.Id}")).ReadAsync<MikrotikBackupContent>();
            StringAssert.Contains(text.Content, $"/system identity set name={name}");
            Assert.IsFalse(text.Content.Contains("private-key=\"", StringComparison.Ordinal), $"{name}: no secret without the policy");
            var unchanged = await (await user.PostAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
            if (unchanged.Changed)
            {
                // Which lines differ, so that a failure says why.
                var latest = (await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>()).Mikrotik!.LatestBackup!;
                var second = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/backups/{latest.Id}")).ReadAsync<MikrotikBackupContent>();
                var was = text.Content.Split('\n');
                var now = second.Content.Split('\n');
                Assert.Fail($"{name}: {unchanged.Message}\n- {string.Join("\n- ", was.Except(now))}\n+ {string.Join("\n+ ", now.Except(was))}");
            }
            var note = $"pnmt lab {Guid.NewGuid():N}";
            Lab("ssh", name, $"/system note set note=\"{note}\"");
            var changed = await (await user.PostAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/backups", null)).ReadAsync<MikrotikBackupResult>();
            Assert.IsTrue(changed.Changed, $"{name}: {changed.Message}");
            var diff = await (await user.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/backups/{changed.Backup!.Id}/diff")).ReadAsync<MikrotikBackupDiff>();
            StringAssert.Contains(diff.Unified, note, name);
            await using (var events = app.NewDb())
            {
                Assert.IsTrue(await events.Events.AnyAsync(e => e.SensorId == sensor.Id && e.Code == PNMTStatusCodes.MIKROTIK_CONFIG_CHANGED), name);
            }

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
