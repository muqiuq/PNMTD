using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Tests.Agents;
using PNMTD.Tests.Linux;
using static PNMTD.Tests.KeyManager.SshKeyTestKit;

namespace PNMTD.Tests.KeyManager;

/// <summary>
/// The SSH key manager, PNMTD's side (docs/linux-sensor-plan.md §6.3, §6.4, §14, L5): registering by paste (ssh-keygen's fingerprints),
/// duplicates and private keys refused (and a private key stored and logged nowhere), 465 for keys not registered as active and 263
/// for registered ones, a server's first section found quietly, registering later naming past logins (264 and the login list, with
/// the key filters), a key's last use, a deleted user's keys becoming "former user", <c>mine</c> answering only the caller's keys, the
/// server's tab, the unregistered list, the strip, and the register Admin only.
/// </summary>
[TestClass]
public sealed class SshKeyManagerTests
{
    private static TestApp app = null!;
    private static HttpClient admin = null!;
    private static Guid hostId;
    private static readonly CapturingLoggerProvider Logs = new();

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Agents:JoinRateLimitPerMinute"] = "1000",
            ["Agents:HelloTimeoutSeconds"] = "2",
        }, services: s => s.AddLogging(b => b.AddProvider(Logs).AddFilter<CapturingLoggerProvider>((_, _) => true)));
        admin = app.Client(Roles.Admin);
        hostId = await AgentHarness.CreateHostAsync(admin, "ssh-keys-tests");
    }

    [ClassCleanup]
    public static async Task Stop()
    {
        await using (var db = app.NewDb())
        {
            await db.Hosts.Where(h => h.Id == hostId).ExecuteUpdateAsync(u => u.SetProperty(h => h.Enabled, false));
        }
        app.Dispose();
    }

    private static async Task<AgentHarness.Joined> JoinAsync(string hostname) =>
        await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, null, AgentPlatform.Linux)).Token, hostname, AgentPlatform.Linux);

    private static async Task<SshKeyItem> RegisterAsync(TestKey key, string name, Guid? userId = null, string? owner = null, SshKeyKind kind = SshKeyKind.Person) =>
        (await (await admin.PostJsonAsync("/api/v1/ssh-keys", new SshKeyRegisterRequest(key.Line, name, null, userId, owner ?? (userId is null ? "tests" : null), kind, null)))
            .ReadAsync<List<SshKeyItem>>()).Single();

    [TestMethod]
    public async Task KeysAreRegisteredByPasteWithSshKeygensFingerprintsAndDuplicatesAndPrivateKeysAreRefused()
    {
        // The preview, as typed: ssh-keygen's fingerprints, the weak RSA key, a line that isn't a key.
        var preview = await (await admin.PostJsonAsync("/api/v1/ssh-keys/parse", new SshKeyParseRequest($"{SshKeysTests.Ed25519}\n\n{SshKeysTests.Rsa2048}\nnonsense\n")))
            .ReadAsync<SshKeyParseResult>();
        Assert.AreEqual(2, preview.Keys);
        Assert.AreEqual((1, 0, SshKeysTests.Ed25519Fingerprint, "test-ed25519@pnmt"), (preview.Lines[0].Line, preview.Lines[0].Index, preview.Lines[0].Fingerprint, preview.Lines[0].SuggestedName));
        Assert.AreEqual((SshKeysTests.Rsa2048Fingerprint, 2048, "RSA 2048 bits: weak"), (preview.Lines[1].Fingerprint, preview.Lines[1].Bits, preview.Lines[1].Weakness));
        Assert.AreEqual((4, SshKeyLineProblem.Unreadable), (preview.Lines[2].Line, preview.Lines[2].Problem));

        // Two keys, a name each, one owner: the fingerprints are ssh-keygen's.
        var registered = await (await admin.PostJsonAsync("/api/v1/ssh-keys", new SshKeyRegisterRequest($"{SshKeysTests.Ed25519}\n{SshKeysTests.Rsa2048}\n", null,
            ["Tester, laptop", "Tester, old desktop"], TestUsers.User, null, SshKeyKind.Person, "for the tests"))).ReadAsync<List<SshKeyItem>>();
        CollectionAssert.AreEqual(new[] { SshKeysTests.Ed25519Fingerprint, SshKeysTests.Rsa2048Fingerprint }, registered.Select(k => k.Fingerprint).ToList());
        Assert.AreEqual("Tester, laptop", registered[0].Name);
        Assert.AreEqual(("test-user", TestUsers.User), (registered[0].Owner.Name, registered[0].Owner.UserId));
        CollectionAssert.Contains(registered[1].Notes.ToList(), "weak");
        var audit = await AuditAsync(AuditEvents.SshKeyRegistered, SshKeysTests.Ed25519Fingerprint);
        Assert.AreEqual(1, audit.Count);

        // Registered before, twice in the text, no owner, a name template: refused, or named from the comment.
        var again = await admin.PostJsonAsync("/api/v1/ssh-keys", new SshKeyRegisterRequest(SshKeysTests.Ed25519, "again", null, null, "tests", SshKeyKind.Person, null));
        Assert.AreEqual(HttpStatusCode.BadRequest, again.StatusCode);
        StringAssert.Contains((await again.ProblemAsync()).GetProperty("errors").GetProperty("publicKeys")[0].GetString(), "registered already, as \"Tester, laptop\"");
        var fresh = NewKey("someone@box");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/ssh-keys",
            new SshKeyRegisterRequest($"{fresh.Line}\n{fresh.Line}", "twice", null, null, "tests", SshKeyKind.Person, null))).StatusCode, "twice in the text");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/ssh-keys",
            new SshKeyRegisterRequest(fresh.Line, "nobody's", null, null, null, SshKeyKind.Person, null))).StatusCode, "an owner is required");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/ssh-keys",
            new SshKeyRegisterRequest(fresh.Line, "both", null, TestUsers.User, "tests", SshKeyKind.Person, null))).StatusCode, "a user or a text");
        var other = NewKey(null!);
        var templated = await (await admin.PostJsonAsync("/api/v1/ssh-keys", new SshKeyRegisterRequest($"{fresh.Line}\n{other.Line}", "Box: {comment}", null, null,
            "a vendor", SshKeyKind.Service, null))).ReadAsync<List<SshKeyItem>>();
        CollectionAssert.AreEqual(new[] { "Box: someone@box", $"Box: {SshKeys.Short(other.Fingerprint)}" }, templated.Select(k => k.Name).ToList());

        // A private key: refused before anything is stored or logged, the public key pasted with it too.
        const string secret = "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZWQyNTUxOQAAACB1c2VyLXNlY3JldC1tYXRlcmlhbA";
        var withPrivate = NewKey("pasted@with-private");
        var privateKey = $"{withPrivate.Line}\n-----BEGIN OPENSSH PRIVATE KEY-----\n{secret}\n-----END OPENSSH PRIVATE KEY-----\n";
        var refused = await admin.PostJsonAsync("/api/v1/ssh-keys", new SshKeyRegisterRequest(privateKey, "oops", null, null, "tests", SshKeyKind.Person, null));
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.AreEqual("private_key", (await refused.ProblemAsync()).GetProperty("code").GetString());
        Assert.IsFalse(body.Contains(secret), "the answer doesn't repeat it");
        var parsed = await (await admin.PostJsonAsync("/api/v1/ssh-keys/parse", new SshKeyParseRequest(privateKey))).ReadAsync<SshKeyParseResult>();
        Assert.IsTrue(parsed.PrivateKey);
        Assert.AreEqual(0, parsed.Keys);
        // At every level (MVC's trace prints the actions' arguments), before this test's own queries name it.
        Assert.IsFalse(Logs.Messages.Any(m => m.Contains(secret) || m.Contains("PRIVATE KEY")), "logged nowhere: "
            + string.Join("\n---\n", Logs.Messages.Where(m => m.Contains(secret) || m.Contains("PRIVATE KEY")).Take(3)));
        await using (var db = app.NewDb())
        {
            Assert.IsFalse(await db.SshKeys.AnyAsync(k => k.Fingerprint == withPrivate.Fingerprint || k.PublicKey.Contains(secret) || k.Name == "oops"));
            Assert.IsFalse(await db.AuditLog.AnyAsync(l => l.Detail != null && l.Detail.Contains(secret)));
            Assert.IsFalse(await db.AgentLog.AnyAsync(l => l.Message.Contains(secret)));
        }

        // The register is Admin only; the list finds a key by a pasted public key, a fingerprint, or a name.
        foreach (var role in new[] { Roles.Viewer, Roles.User })
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(role).GetAsync("/api/v1/ssh-keys")).StatusCode, role);
            Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(role).PostJsonAsync("/api/v1/ssh-keys",
                new SshKeyRegisterRequest(NewKey("x").Line, "mine", null, null, "me", SshKeyKind.Person, null))).StatusCode, role);
        }
        foreach (var search in new[] { SshKeysTests.Ed25519, SshKeysTests.Ed25519Fingerprint[7..], "tester, LAPTOP" })
        {
            var found = await (await admin.GetAsync($"/api/v1/ssh-keys?search={Uri.EscapeDataString(search)}")).ReadAsync<Paged<SshKeyItem>>();
            Assert.AreEqual(SshKeysTests.Ed25519Fingerprint, found.Items.Single().Fingerprint, search);
        }
        var weak = await (await admin.GetAsync($"/api/v1/ssh-keys?filter=weak&owner={TestUsers.User}")).ReadAsync<Paged<SshKeyItem>>();
        Assert.AreEqual(SshKeysTests.Rsa2048Fingerprint, weak.Items.Single().Fingerprint);

        // Editing, retiring, reactivating and deleting are audited.
        var id = registered[0].Id;
        var edited = await (await admin.PutJsonAsync($"/api/v1/ssh-keys/{id}", new SshKeyUpdateRequest("Tester, new laptop", TestUsers.User, null, SshKeyKind.Person, null)))
            .ReadAsync<SshKeyDetail>();
        Assert.AreEqual("Tester, new laptop", edited.Key.Name);
        Assert.AreEqual(SshKeysTests.Ed25519, edited.PublicKey);
        Assert.AreEqual(SshKeyState.Retired, (await (await admin.PostJsonAsync($"/api/v1/ssh-keys/{id}/retire", new { })).ReadAsync<SshKeyDetail>()).Key.State);
        Assert.AreEqual(SshKeyState.Active, (await (await admin.PostJsonAsync($"/api/v1/ssh-keys/{id}/reactivate", new { })).ReadAsync<SshKeyDetail>()).Key.State);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/ssh-keys/{registered[1].Id}")).StatusCode);
        Assert.AreEqual(1, (await AuditAsync(AuditEvents.SshKeyChanged, SshKeysTests.Ed25519Fingerprint)).Count(l => l.Contains("name Tester, laptop → Tester, new laptop")));
        Assert.AreEqual(1, (await AuditAsync(AuditEvents.SshKeyRetired, SshKeysTests.Ed25519Fingerprint)).Count);
        Assert.AreEqual(1, (await AuditAsync(AuditEvents.SshKeyDeleted, SshKeysTests.Rsa2048Fingerprint)).Count);
        var history = (await (await admin.GetAsync($"/api/v1/ssh-keys/{id}")).ReadAsync<SshKeyDetail>()).History;
        Assert.IsTrue(history.Count >= 4 && history.All(h => h.Source == "audit"), string.Join("\n", history.Select(h => h.Text)));
    }

    private static async Task<List<string>> AuditAsync(string type, string fingerprint)
    {
        await using var db = app.NewDb();
        return await db.AuditLog.AsNoTracking().Where(l => l.EventType == type && l.Detail != null && l.Detail.Contains(fingerprint)).Select(l => l.Detail!).ToListAsync();
    }

    [TestMethod]
    public async Task AKeyNotRegisteredAsActiveWrites465AndARegisteredOne263()
    {
        var joined = await JoinAsync("keys-events");
        var sensor = joined.Grant.SensorId;
        await using var session = await AgentHarness.OpenAsync(app, joined, rights: new AgentRightsSummary("defaults", null, false, [], 0, [], false, [], 0, 60, false, false),
            capabilities: LinuxCapabilities(false));
        var found = NewKey("found@first");
        var active = NewKey("tester@laptop");
        var retired = NewKey("jan@old-laptop");
        var unknown = NewKey("jdoe@laptop");
        var activeKey = await RegisterAsync(active, "Tester, laptop", TestUsers.User);
        var retiredKey = await RegisterAsync(retired, "Jan, old laptop", owner: "Jan (contractor)");
        await admin.PostJsonAsync($"/api/v1/ssh-keys/{retiredKey.Id}/retire", new { });

        // The first section: its keys are found, with no event for each.
        await SendKeysAsync(app, session, sensor, Section(Root(found)));
        await using (var db = app.NewDb())
        {
            CollectionAssert.Contains(await db.AgentChanges.Where(c => c.SensorId == sensor).Select(c => c.Summary).ToListAsync(), "1 key found in 1 file (1 not registered as active)");
        }
        Assert.AreEqual(0, (await AgentHarness.EventsAsync(app, sensor)).Count);

        // Added: an unregistered key and a retired one (465 each), a registered active one (263); removed outside PNMT (263).
        await SendKeysAsync(app, session, sensor, Section(Root(unknown, retired), Deploy(active)));
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, sensor), e => e.Count >= 3);
        var alerts = events.Where(e => e.Code == PNMTStatusCodes.LINUX_KEY_ADDED).Select(e => e.Message).ToList();
        CollectionAssert.AreEquivalent(new[]
        {
            $"root: unregistered key ssh-ed25519 {SshKeys.Short(unknown.Fingerprint)} 'jdoe@laptop' added to /root/.ssh/authorized_keys",
            "root: retired key Jan, old laptop (Jan (contractor)) added again to /root/.ssh/authorized_keys",
        }, alerts);
        var changed = events.Single(e => e.Code == PNMTStatusCodes.LINUX_CHANGED).Message!;
        StringAssert.Contains(changed, "Tester, laptop (test-user) added to deploy's authorized_keys");
        StringAssert.Contains(changed, $"unregistered key ssh-ed25519 {SshKeys.Short(found.Fingerprint)} 'found@first' removed from root's authorized_keys, outside PNMT");

        // The key manager's views: the server's tab, the key's places, the unregistered list, the fleet's columns, the strip.
        var tab = await (await app.Client(Roles.User).GetAsync($"/api/v1/agents/servers/{sensor}/ssh-keys")).ReadAsync<SshKeyServerView>();
        Assert.AreEqual(activeKey.Id, tab.Files.Single(f => f.Account == "deploy").Keys.Single().Registration?.Id);
        Assert.IsNull(tab.Files.Single(f => f.Account == "root").Keys.Single(k => k.Fingerprint == unknown.Fingerprint).Registration);
        Assert.AreEqual(unknown.Line, tab.Files.Single(f => f.Account == "root").Keys.Single(k => k.Fingerprint == unknown.Fingerprint).PublicKey);
        Assert.AreEqual((1, 1), (tab.Unregistered, tab.RetiredAuthorized));
        Assert.IsFalse(tab.RemovalAllowed);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.Viewer).GetAsync($"/api/v1/agents/servers/{sensor}/ssh-keys")).StatusCode);
        var detail = await (await admin.GetAsync($"/api/v1/ssh-keys/{activeKey.Id}")).ReadAsync<SshKeyDetail>();
        Assert.AreEqual(("deploy", "/home/deploy/.ssh/authorized_keys"), (detail.Authorizations.Single().Account, detail.Authorizations.Single().Path));
        StringAssert.Contains(detail.Authorizations.Single().CannotRemove, "ssh_keys: remove: true");
        Assert.IsTrue(detail.History.Any(h => h.Source == "change" && h.Text.Contains("added to deploy's authorized_keys")), "the change line is in the key's history");
        var unregistered = await (await admin.GetAsync($"/api/v1/ssh-keys/unregistered?sensorId={sensor}")).ReadAsync<Paged<SshKeyUnregisteredItem>>();
        Assert.AreEqual(unknown.Fingerprint, unregistered.Items.Single().Fingerprint);
        Assert.AreEqual(unknown.Line, unregistered.Items.Single().PublicKey);
        CollectionAssert.AreEqual(new[] { "jdoe@laptop" }, unregistered.Items.Single().Comments.ToList());
        var retiredItem = (await (await admin.GetAsync($"/api/v1/ssh-keys?filter=retiredStillAuthorized&search={Uri.EscapeDataString(retired.Fingerprint)}"))
            .ReadAsync<Paged<SshKeyItem>>()).Items.Single();
        Assert.AreEqual((1, 1), (retiredItem.Servers, retiredItem.Accounts));
        var fleet = (await (await admin.GetAsync("/api/v1/agents/servers?search=keys-events&unregisteredKeys=true")).ReadAsync<Paged<AgentServerItem>>()).Items.Single();
        Assert.AreEqual((1, 1), (fleet.UnregisteredKeys, fleet.RetiredKeysAuthorized));
        var summary = await (await admin.GetAsync("/api/v1/ssh-keys/summary")).ReadAsync<SshKeySummary>();
        Assert.IsTrue(summary.Unregistered >= 1 && summary.RetiredStillAuthorized >= 1, AgentJson.Serialize(summary));

        // A key file's mode is notable too; with the server's switch off, an unregistered key added writes no 465.
        await (await app.Client(Roles.User).PutJsonAsync($"/api/v1/agents/servers/{sensor}/settings", new AgentServerSettings(true, true, 10, false, KeyAddedEvents: false)))
            .ReadAsync<AgentServerDetail>();
        var another = NewKey("another@laptop");
        await SendKeysAsync(app, session, sensor, Section(SshKeyTestKit.File("root", "/root/.ssh/authorized_keys", [unknown, retired, another], mode: "0664"), Deploy(active)));
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, sensor), e => e.Count(x => x.Code == PNMTStatusCodes.LINUX_CHANGED) == 2);
        Assert.AreEqual("root's /root/.ssh/authorized_keys: mode 0600 → 0664", events[0].Message);
        Assert.AreEqual(2, events.Count(e => e.Code == PNMTStatusCodes.LINUX_KEY_ADDED), "the switch is off");
    }

    [TestMethod]
    public async Task RegisteringAKeyLaterNamesItsPastLoginsAndALoginSetsItsLastUse()
    {
        var joined = await JoinAsync("keys-logins");
        var sensor = joined.Grant.SensorId;
        await using var session = await AgentHarness.OpenAsync(app, joined, capabilities: LinuxCapabilities(false));
        // A fingerprint with a '+' and a '/', as the logins' filter gets it from the UI (escaped).
        var key = NewKey("tester@workstation");
        while (!key.Fingerprint.Contains('+'))
        {
            key = NewKey("tester@workstation");
        }
        await SendKeysAsync(app, session, sensor, Section(Root(key)));
        LoginRecord Login(long id, DateTimeOffset at) => new(LinuxLogins.Channel, id, at, null, 1, LogonOutcome.Success, null, "root", null, true, LogonKind.Ssh, "publickey",
            "10.0.4.17", 51000, null, null, null, null, key.Fingerprint);
        var first = DateTimeOffset.UtcNow.AddSeconds(-30);
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([Login(9101, first)]), CancellationToken.None);
        var events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, sensor), e => e.Any(x => x.Code == PNMTStatusCodes.LINUX_ADMIN_LOGIN));
        Assert.AreEqual($"root: SSH from 10.0.4.17 with key {SshKeys.Short(key.Fingerprint)}", events[0].Message);

        // The login set the key's last use there.
        await using (var db = app.NewDb())
        {
            var place = await db.SshKeyAuthorizations.AsNoTracking().SingleAsync(a => a.SensorId == sensor && a.Fingerprint == key.Fingerprint);
            Assert.IsTrue(Math.Abs((first.UtcDateTime - place.LastUsedAt!.Value).TotalMilliseconds) < 1, $"{place.LastUsedAt:O}");
            Assert.AreEqual("10.0.4.17", place.LastUsedFrom);
        }

        // Registered later: the past login is named, and the filters by key and owner find it.
        var registered = await RegisterAsync(key, "Tester, workstation", TestUsers.Admin);
        var url = $"/api/v1/agents/servers/{sensor}/logins";
        var user = app.Client(Roles.User);
        foreach (var filter in new[] { $"keyFingerprint={Uri.EscapeDataString(key.Fingerprint)}", $"keyFingerprint={Uri.EscapeDataString(key.Fingerprint[7..])}",
            $"keyOwner={TestUsers.Admin}", "keyOwner=test-admin", "keyOwner=TEST-ADM" })
        {
            var response = await user.GetAsync($"{url}?{filter}");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, filter);
            var login = (await response.ReadAsync<Paged<AgentLoginItem>>()).Items.Single();
            Assert.AreEqual(("Tester, workstation", "test-admin", registered.Id), (login.KeyName, login.KeyOwner, login.KeyId), filter);
        }
        Assert.AreEqual(0, (await (await user.GetAsync($"{url}?keyOwner=nobody-has-this")).ReadAsync<Paged<AgentLoginItem>>()).TotalCount);
        var detail = await (await admin.GetAsync($"/api/v1/ssh-keys/{registered.Id}")).ReadAsync<SshKeyDetail>();
        Assert.AreEqual(sensor, detail.RecentLogins.Single().Server.Id);
        Assert.AreEqual(("root", "10.0.4.17"), (detail.Key.LastUse?.Account, detail.Key.LastUse?.From));

        // The next login's 264 names it.
        await session.SendAsync(AgentMessageTypes.Access, new AccessBatch([Login(9102, DateTimeOffset.UtcNow.AddSeconds(-5))]), CancellationToken.None);
        events = await AgentHarness.EventuallyAsync(() => AgentHarness.EventsAsync(app, sensor), e => e.Count(x => x.Code == PNMTStatusCodes.LINUX_ADMIN_LOGIN) == 2);
        Assert.AreEqual("root: SSH from 10.0.4.17 with key Tester, workstation (test-admin)", events[0].Message);
        var names = await AgentAccessService.KeyNamesAsync(app.NewDb(), [key.Fingerprint], CancellationToken.None);
        Assert.AreEqual(new SshKeyName("Tester, workstation", "test-admin"), names[key.Fingerprint]);
    }

    [TestMethod]
    public async Task ADeletedUsersKeysBecomeFormerUsersAndMineAnswersOnlyTheCallersKeys()
    {
        var created = await (await admin.PostJsonAsync("/api/v1/users", new CreateUserRequest { Username = $"leaver{Random.Shared.Next(100_000)}", Role = Roles.User }))
            .ReadAsync<CreatedUser>();
        var leaver = created.User;
        var key = NewKey("leaver@laptop");
        var registered = await RegisterAsync(key, "Leaver, laptop", leaver.Id);
        Assert.AreEqual(1, (await (await admin.GetAsync($"/api/v1/users/{leaver.Id}")).ReadAsync<UserItem>()).SshKeys);
        var page = await (await admin.GetAsync($"/api/v1/users?search={leaver.Username}")).ReadAsync<Paged<UserItem>>();
        Assert.AreEqual(1, page.Items.Single().SshKeys);
        var keys = await (await admin.GetAsync($"/api/v1/ssh-keys/users/{leaver.Id}")).ReadAsync<SshKeyUserKeys>();
        Assert.AreEqual(registered.Id, keys.Keys.Single().Key.Id);

        // Deactivated: the strip names the user while their keys are authorized somewhere; deleted: the names stay, the owner becomes a text.
        Assert.AreEqual(HttpStatusCode.OK, (await admin.PutJsonAsync($"/api/v1/users/{leaver.Id}", new UpdateUserRequest { DisplayName = "Leaver", Role = Roles.User, IsActive = false }))
            .StatusCode);
        var item = (await (await admin.GetAsync($"/api/v1/ssh-keys?owner={leaver.Id}")).ReadAsync<Paged<SshKeyItem>>()).Items.Single();
        CollectionAssert.Contains(item.Notes.ToList(), "ownerDeactivated");
        Assert.IsTrue(item.Owner.Deactivated);
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/users/{leaver.Id}")).StatusCode);
        var after = await (await admin.GetAsync($"/api/v1/ssh-keys/{registered.Id}")).ReadAsync<SshKeyDetail>();
        Assert.AreEqual("Leaver, laptop", after.Key.Name);
        Assert.AreEqual(($"former user {leaver.Username}", (Guid?)null, true), (after.Key.Owner.Text, after.Key.Owner.UserId, after.Key.Owner.FormerUser));
        var names = await AgentAccessService.KeyNamesAsync(app.NewDb(), [key.Fingerprint], CancellationToken.None);
        Assert.AreEqual($"key Leaver, laptop (former user {leaver.Username})", AgentAccessService.KeyText(key.Fingerprint, names[key.Fingerprint]));

        // mine: every role its own keys only, with where each is authorized.
        var users = NewKey("user@laptop");
        var viewers = NewKey("viewer@laptop");
        await RegisterAsync(users, "Test user, laptop", TestUsers.User);
        await RegisterAsync(viewers, "Test viewer, laptop", TestUsers.Viewer);
        var joined = await JoinAsync("keys-mine");
        await using (var session = await AgentHarness.OpenAsync(app, joined, capabilities: LinuxCapabilities(false)))
        {
            await SendKeysAsync(app, session, joined.Grant.SensorId, Section(Deploy(users, viewers)));
        }
        var mine = await (await app.Client(Roles.Viewer).GetAsync("/api/v1/ssh-keys/mine")).ReadAsync<List<SshKeyMineItem>>();
        Assert.IsTrue(mine.Any(k => k.Fingerprint == viewers.Fingerprint));
        Assert.IsFalse(mine.Any(k => k.Fingerprint == users.Fingerprint));
        var place = mine.Single(k => k.Fingerprint == viewers.Fingerprint).Where.Single();
        Assert.AreEqual("deploy", place.Account);
        StringAssert.Contains(place.Server, "ssh-keys-tests");
        var usersMine = await (await app.Client(Roles.User).GetAsync("/api/v1/ssh-keys/mine")).ReadAsync<List<SshKeyMineItem>>();
        Assert.IsTrue(usersMine.Any(k => k.Fingerprint == users.Fingerprint) && !usersMine.Any(k => k.Fingerprint == viewers.Fingerprint));
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await app.Client().GetAsync("/api/v1/ssh-keys/mine")).StatusCode);
    }

    /// <summary>Everything logged while the class runs, at every level: a private key must be in none of it.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                provider.Messages.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}
