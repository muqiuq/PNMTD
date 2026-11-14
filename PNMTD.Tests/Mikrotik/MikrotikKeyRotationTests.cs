using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using static PNMTD.Tests.Mikrotik.MikrotikJobTests;

namespace PNMTD.Tests.Mikrotik;

/// <summary>Rotating PNMTD's SSH key (docs/mikrotik-plan.md §4.2): scripts carry the new key, the old one is offered until retired.</summary>
[TestClass]
public sealed class MikrotikKeyRotationTests
{
    [TestMethod]
    public async Task A_rotation_keeps_routers_working_until_the_old_key_is_retired()
    {
        var routers = new AdminConnector();
        using var app = new TestApp(services: s => s.Replace(ServiceDescriptor.Singleton<IRouterConnector>(routers)));
        var admin = app.Client(Roles.Admin);
        var store = app.Services.GetRequiredService<MikrotikKeyStore>();
        var polls = app.Services.GetRequiredService<RouterPollService>();
        var host = await (await admin.PostJsonAsync("/api/v1/hosts", new HostRequest { Name = $"rotation-{Guid.NewGuid():N}" })).ReadAsync<HostDetail>();

        // A router that has the current key.
        var old = (await store.GetAsync(CancellationToken.None)).Current;
        var router = new SimulatedRouter { KeyOwner = old.Comment };
        var address = $"198.51.100.{Random.Shared.Next(100, 250)}";
        routers.Add(address, router);
        var sensor = await (await admin.PostJsonAsync("/api/v1/sensors", new SensorRequest
        {
            HostId = host.Id, Type = SensorType.MIKROTIK, Name = $"rotation {address}", Interval = 300, GracePeriod = 600,
            Mikrotik = new MikrotikRouterRequest { Mode = MikrotikMode.DirectSsh, Address = address },
        })).ReadAsync<SensorDetail>();
        await polls.PollAsync(sensor.Id, wait: true, CancellationToken.None);

        var rotated = await (await admin.PostAsync("/api/v1/mikrotik/ssh-keys/rotate", null)).ReadAsync<MikrotikSshKeyStatus>();
        Assert.AreNotEqual(old.Comment, rotated.CurrentComment);
        Assert.AreEqual(rotated.CurrentComment, rotated.Keys[0].Comment, "newest first");
        Assert.IsTrue(rotated.Waiting.Any(w => w.SensorId == sensor.Id), "the router has only the old key");

        // The script carries only the new key; PNMTD offers both, so the router still answers.
        var script = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}/mikrotik/join-script")).ReadAsync<MikrotikScript>();
        StringAssert.Contains(script.Script, rotated.Keys[0].Comment);
        Assert.IsFalse(script.Script.Contains(old.Comment, StringComparison.Ordinal), "a new script removes the old key");
        var outcome = await polls.PollAsync(sensor.Id, wait: true, CancellationToken.None);
        Assert.IsTrue(outcome.Succeeded, outcome.Message);
        Assert.IsTrue(router.OfferedKeys >= 2);
        var detail = await (await admin.GetAsync($"/api/v1/sensors/{sensor.Id}")).ReadAsync<SensorDetail>();
        Assert.IsTrue(detail.Mikrotik!.ScriptDifferences.Contains("The router does not have PNMTD's current SSH key."));

        // Retiring is refused while the router waits, unless forced; once it has the new key, it is not.
        Assert.AreEqual(HttpStatusCode.Conflict, (await admin.PostAsync("/api/v1/mikrotik/ssh-keys/retire", null)).StatusCode);
        router.KeyOwner = rotated.CurrentComment;
        await polls.PollAsync(sensor.Id, wait: true, CancellationToken.None);
        var status = await (await admin.GetAsync("/api/v1/mikrotik/ssh-keys")).ReadAsync<MikrotikSshKeyStatus>();
        Assert.IsFalse(status.Waiting.Any(w => w.SensorId == sensor.Id));
        // Other test classes' routers may still wait: force, as an Admin would after reading the list.
        var retired = await (await admin.PostAsync("/api/v1/mikrotik/ssh-keys/retire?force=true", null)).ReadAsync<MikrotikSshKeyStatus>();
        Assert.IsTrue(retired.Keys.Skip(1).All(k => k.RetiredAt is not null));
        Assert.AreEqual(1, (await store.PrivateKeysAsync(CancellationToken.None)).Count, "only the new key is offered");
        await polls.PollAsync(sensor.Id, wait: true, CancellationToken.None);
        Assert.AreEqual(1, router.OfferedKeys);

        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == AuditEvents.MikrotikSshKeyRotated));
        Assert.IsTrue(await db.AuditLog.AnyAsync(a => a.EventType == AuditEvents.MikrotikSshKeyRetired));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).PostAsync("/api/v1/mikrotik/ssh-keys/rotate", null)).StatusCode);
    }
}
