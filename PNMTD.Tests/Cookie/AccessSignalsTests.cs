using PNMTD.Cookie.Access;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// The access watch's pure parts (docs/cookie-access-plan.md §10.6): the signals, the networks and addresses, known access, the
/// rules that hold a verdict to the signals, and the RouterOS login lines and clocks.
/// </summary>
[TestClass]
public sealed class AccessSignalsTests
{
    private static readonly DateTime At = new(2026, 11, 15, 3, 12, 0, DateTimeKind.Utc);

    private static AccessLogin Rdp(string source = "10.1.2.3", string user = "jdoe", AccessTargetKind target = AccessTargetKind.Server, LogonKind kind = LogonKind.Rdp,
        string? key = null, string? becomes = null, bool admin = false) =>
        new(target, Guid.Empty, "srv-01", null, null, null, null, At, user, target == AccessTargetKind.Server ? "CONTOSO" : null, admin, kind, AccessSignals.WayOf(kind),
            source, KeyFingerprint: key, TargetUser: becomes);

    /// <summary>The account knows this target, this network and this way, at this hour: nothing new.</summary>
    private static AccessFacts Known(AccessLogin l) => new()
    {
        Login = l, AccountSeen = true, NetworkSeen = true, GroupSeen = true, AdminSeen = true, TargetUserSeen = true, DeviceSeen = true, HourSeen = true,
        PublicSeen = true, CleartextSeen = true,
    };

    private static IReadOnlyList<string> Keys(AccessFacts f) => [.. AccessSignals.Compute(f).Select(s => s.Key)];

    [TestMethod]
    public void A_known_pattern_is_normal_and_each_new_part_is_found()
    {
        var l = Rdp();
        Assert.AreEqual(0, AccessSignals.Compute(Known(l)).Count, "the history settles it");
        CollectionAssert.AreEqual(new[] { "new-account" }, Keys(Known(l) with { AccountSeen = false, NetworkSeen = false, GroupSeen = false }).ToArray(),
            "a new account is one signal, not three");
        CollectionAssert.Contains(Keys(Known(l) with { NetworkSeen = false }).ToList(), "new-network");
        CollectionAssert.Contains(Keys(Known(l) with { GroupSeen = false }).ToList(), "new-way");
        CollectionAssert.Contains(Keys(Known(l with { IsAdmin = true }) with { AdminSeen = false }).ToList(), "first-admin");
        CollectionAssert.Contains(Keys(Known(l) with { WorkingHours = false, HourSeen = false }).ToList(), "odd-hour");
        Assert.AreEqual(0, AccessSignals.Compute(Known(l) with { WorkingHours = false, HourSeen = true }).Count, "a usual hour at night is usual");
        CollectionAssert.Contains(Keys(Known(l) with { Person = new AccessPerson(Guid.NewGuid(), "Bea", true, false, "on vacation") }).ToList(), "not-working");
        var pnmt = Rdp(target: AccessTargetKind.Pnmt) with { Way = AccessSignals.Web, Kind = null, Device = "Firefox on Windows" };
        CollectionAssert.Contains(Keys(Known(pnmt) with { DeviceSeen = false }).ToList(), "new-device");
        Assert.IsFalse(Keys(Known(pnmt) with { AccountSeen = false }).Contains("new-account"), "PNMT has no new account");
    }

    [TestMethod]
    public void The_strong_signals_are_strong()
    {
        var l = Rdp(source: "203.0.113.7");
        AccessSignal Only(AccessFacts f, string key) => AccessSignals.Compute(f).Single(s => s.Key == key);
        Assert.AreEqual(SignalStrength.Strong, Only(Known(l) with { FailuresHere = 14 }, "after-failures").Strength);
        Assert.AreEqual(SignalStrength.Strong, Only(Known(l) with { FailuresAnywhere = 10 }, "after-failures").Strength);
        Assert.IsFalse(Keys(Known(l) with { FailuresAnywhere = 9 }).Contains("after-failures"), "fewer than 10 elsewhere isn't a burst");
        Assert.AreEqual(SignalStrength.Strong, Only(Known(l) with { PublicSeen = false }, "public-source").Strength);
        Assert.IsFalse(Keys(Known(l) with { PublicSeen = false, TrustedName = "VPN" }).Contains("public-source"), "a trusted network isn't public");
        Assert.IsFalse(Keys(Known(Rdp(source: "10.0.0.1")) with { PublicSeen = false }).Contains("public-source"), "a private one neither");
        var key = Rdp(kind: LogonKind.Ssh, key: "SHA256:abc");
        Assert.AreEqual(SignalStrength.Strong, Only(Known(key) with { KeyKnown = false }, "unknown-key").Strength);
        Assert.AreEqual(SignalStrength.Strong, Only(Known(key) with { KeyKnown = true, KeyRetired = true }, "retired-key").Strength);
        Assert.AreEqual(SignalStrength.Strong, Only(Known(l) with { Person = new AccessPerson(Guid.NewGuid(), "Carl", false, true, null), FormerPerson = true },
            "former-person").Strength);
        Assert.AreEqual(SignalStrength.Strong, Only(Known(l) with { FanOut = 3, FanOutNew = true }, "fan-out").Strength);
        Assert.IsFalse(Keys(Known(l) with { FanOut = 3, FanOutNew = false }).Contains("fan-out"), "known accounts on many targets aren't a fan-out");
        var router = Rdp(target: AccessTargetKind.Router, kind: LogonKind.Winbox, source: "10.99.0.1") with { Domain = null, User = "admin" };
        Assert.AreEqual(SignalStrength.Strong, Only(Known(router) with { FromPnmt = true }, "pnmt-address").Strength, "PNMTD's address, but no job");
        Assert.IsFalse(Keys(Known(router) with { FromPnmt = true, PnmtJob = true }).Contains("pnmt-address"), "one of PNMT's jobs");
        Assert.AreEqual(SignalStrength.Strong, Only(Known(router with { User = "pnmt" }) with { FromPnmt = false }, "pnmt-address").Strength, "pnmt from elsewhere");
    }

    [TestMethod]
    public void A_learning_period_keeps_only_the_strong_and_sudo_only_two()
    {
        var l = Rdp(source: "203.0.113.7");
        var facts = Known(l) with { Learning = true, AccountSeen = false, PublicSeen = false };
        CollectionAssert.AreEqual(new[] { "public-source" }, Keys(facts).ToArray());
        var sudo = Rdp(kind: LogonKind.Sudo, becomes: "root");
        CollectionAssert.AreEqual(new[] { "new-target" }, Keys(Known(sudo) with { TargetUserSeen = false, NetworkSeen = false, HourSeen = false, WorkingHours = false }).ToArray(),
            "sudo: only a new target and success after failures");
    }

    [TestMethod]
    public void A_verdict_is_held_to_the_signals()
    {
        Assert.AreEqual(AccessVerdict.Suspicious, AccessSignals.Hold(AccessVerdict.Alarming, 0.99, strong: false, flagged: false, late: false), "alarming needs a strong signal");
        Assert.AreEqual(AccessVerdict.Alarming, AccessSignals.Hold(AccessVerdict.Alarming, 0.99, strong: true, flagged: false, late: false));
        Assert.AreEqual(AccessVerdict.Unusual, AccessSignals.Hold(AccessVerdict.Normal, 0.79, strong: true, flagged: false, late: false), "normal on a strong one needs 0.8");
        Assert.AreEqual(AccessVerdict.Normal, AccessSignals.Hold(AccessVerdict.Normal, 0.8, strong: true, flagged: false, late: false));
        Assert.AreEqual(AccessVerdict.Suspicious, AccessSignals.Hold(AccessVerdict.Normal, 0.99, strong: false, flagged: true, late: false), "a flagged text");
        Assert.AreEqual(AccessVerdict.Suspicious, AccessSignals.Hold(AccessVerdict.Alarming, 0.99, strong: true, flagged: false, late: true), "a late login");
        Assert.AreEqual(AccessVerdict.Alarming, AccessSignals.CodeVerdict(2, flagged: false, late: false));
        Assert.AreEqual(AccessVerdict.Suspicious, AccessSignals.CodeVerdict(1, flagged: false, late: false));
        Assert.AreEqual(AccessVerdict.Alarming, AccessSignals.CodeVerdict(1, flagged: true, late: false));
        Assert.AreEqual(AccessVerdict.Suspicious, AccessSignals.CodeVerdict(3, flagged: true, late: true));
        Assert.AreEqual(AccessVerdict.Suspicious, AccessSignals.Lower(AccessVerdict.Alarming));
    }

    [TestMethod]
    public void Networks_addresses_and_accounts()
    {
        Assert.AreEqual("10.1.2.0/24", AccessSignals.NetworkOf("10.1.2.3"));
        Assert.AreEqual("2001:db8:1:2::/64", AccessSignals.NetworkOf("2001:db8:1:2:aa:bb:cc:dd"));
        Assert.AreEqual("d4:ca:6d:00:11:22", AccessSignals.NetworkOf("D4:CA:6D:00:11:22"), "a MAC (WinBox) as it is");
        Assert.IsTrue(AccessSignals.IsPublic("203.0.113.7"));
        Assert.IsTrue(AccessSignals.IsPublic("2001:db8::1"));
        foreach (var address in new[] { "10.0.0.1", "172.20.1.1", "192.168.1.1", "100.64.0.1", "169.254.1.1", "127.0.0.1", "fe80::1", "fd00::1", "D4:CA:6D:00:11:22", "" })
        {
            Assert.IsFalse(AccessSignals.IsPublic(address), address);
        }
        Assert.IsTrue(AccessSignals.InNetwork("10.1.2.3", "10.0.0.0/8"));
        Assert.IsFalse(AccessSignals.InNetwork("11.1.2.3", "10.0.0.0/8"));
        Assert.IsTrue(AccessSignals.SameAccount("CONTOSO\\jdoe", "jdoe"));
        Assert.IsTrue(AccessSignals.SameAccount("jdoe@contoso.local", "CONTOSO\\JDoe"));
        Assert.IsFalse(AccessSignals.SameAccount("jdoe", "jdoe2"));
        Assert.IsTrue(AccessSignals.Shared("CONTOSO\\Administrator"));
        Assert.IsTrue(AccessSignals.NearHour(167, new HashSet<int> { 0 }), "round the week");
    }

    [TestMethod]
    public void Known_access_covers_its_scope_and_stops_at_its_end()
    {
        var sensor = Guid.NewGuid();
        var l = Rdp() with { SensorId = sensor, LocationId = Guid.NewGuid() };
        AccessKnownEntity Entry(string scope = "target", string? account = "jdoe", string? source = "10.1.2.0/24", List<string>? groups = null, DateTime? until = null,
            Guid? other = null, AccessTargetKind? target = AccessTargetKind.Server) => new()
        {
            Scope = scope, Target = target, SensorId = scope == "target" ? other ?? sensor : null, LocationId = scope == "location" ? l.LocationId : null,
            Account = account, Source = source, Groups = groups ?? [], Until = until,
        };
        Assert.IsTrue(AccessSignals.Covers(Entry(), l, At));
        Assert.IsTrue(AccessSignals.Covers(Entry(account: null, source: null), l, At), "any account, anywhere");
        Assert.IsFalse(AccessSignals.Covers(Entry(source: "10.9.9.0/24"), l, At), "another network");
        Assert.IsFalse(AccessSignals.Covers(Entry(groups: [AccessSignals.Console]), l, At), "another way");
        Assert.IsFalse(AccessSignals.Covers(Entry(until: At.AddMinutes(-1)), l, At), "ended");
        Assert.IsFalse(AccessSignals.Covers(Entry(other: Guid.NewGuid()), l, At), "another server");
        Assert.IsTrue(AccessSignals.Covers(Entry(scope: "location"), l, At));
        Assert.IsTrue(AccessSignals.Covers(Entry(scope: "all", target: null), l, At));
        Assert.IsFalse(AccessSignals.Covers(Entry(scope: "all", target: AccessTargetKind.Router), l, At), "every router, not servers");
    }

    [TestMethod]
    public void RouterOS_login_lines_and_clocks()
    {
        var login = RouterLogins.Parse("user admin logged in from 192.168.88.10 via winbox")!;
        Assert.AreEqual((RouterLoginEvent.LoggedIn, "admin", "192.168.88.10", LogonKind.Winbox), (login.Event, login.User, login.Source, login.Kind));
        Assert.AreEqual((RouterLoginEvent.LoggedOut, LogonKind.Ssh), (RouterLogins.Parse("user admin logged out from 10.0.0.5 via ssh")!.Event,
            RouterLogins.Parse("user admin logged out from 10.0.0.5 via ssh")!.Kind));
        var failure = RouterLogins.Parse("login failure for user admin from 203.0.113.7 via ssh")!;
        Assert.AreEqual((RouterLoginEvent.Failure, "203.0.113.7"), (failure.Event, failure.Source));
        Assert.AreEqual((null as string, LogonKind.Console), (RouterLogins.Parse("user admin logged in via local")!.Source, RouterLogins.Parse("user admin logged in via local")!.Kind));
        Assert.AreEqual(LogonKind.Web, RouterLogins.Parse("user bob logged in from 10.0.0.9 via web")!.Kind);
        Assert.AreEqual(LogonKind.Api, RouterLogins.Parse("user api-reader logged in from 10.0.0.9 via api-ssl")!.Kind);
        Assert.AreEqual("D4:CA:6D:00:11:22", RouterLogins.Parse("user admin logged in from D4:CA:6D:00:11:22 via winbox")!.Source);
        Assert.IsNull(RouterLogins.Parse("user bob added by admin"));
        Assert.IsTrue(RouterLogins.LooksLikeLogin("user bob logged in via something-new"));
        Assert.IsNull(RouterLogins.Parse("user bob logged in via something-new")!.Kind, "an unknown way is counted, not stored");

        var now = new DateTime(2026, 11, 15, 10, 30, 0);
        Assert.AreEqual(new DateTime(2026, 11, 15, 10, 14, 32), RouterLogins.Time("10:14:32", now));
        Assert.AreEqual(new DateTime(2026, 11, 14, 23, 59, 0), RouterLogins.Time("23:59:00", now), "a time of today after the clock is yesterday's");
        Assert.AreEqual(new DateTime(2026, 11, 13, 8, 0, 1), RouterLogins.Time("nov/13 08:00:01", now));
        Assert.AreEqual(new DateTime(2026, 11, 13, 8, 0, 1), RouterLogins.Time("2026-11-13 08:00:01", now));
        Assert.AreEqual(new DateTime(2025, 12, 31, 8, 0, 1), RouterLogins.Time("dec/31/2025 08:00:01", now));
        Assert.AreEqual(new DateTime(2026, 11, 15, 11, 30, 5), RouterLogins.Clock("2026-11-15 11:30:05"));
        Assert.AreEqual(new DateTime(2026, 11, 15, 11, 30, 5), RouterLogins.Clock("nov/15/2026 11:30:05"));
        Assert.AreEqual(TimeSpan.FromHours(1), RouterLogins.Offset(new DateTime(2026, 11, 15, 11, 30, 7), new DateTime(2026, 11, 15, 10, 29, 58)),
            "rounded to a quarter of an hour");
        var utc = new DateTime(2026, 11, 15, 9, 14, 32, DateTimeKind.Utc);
        Assert.AreEqual(RouterLogins.RecordId(utc, "user admin logged in from 10.0.0.5 via winbox"),
            RouterLogins.RecordId(utc, " user admin logged in from 10.0.0.5 via winbox "), "the same line, the same id");
        Assert.AreEqual(RouterLogins.FailureId(utc, "admin", "203.0.113.7", "ssh"), RouterLogins.FailureId(utc.AddMinutes(5), "admin", "203.0.113.7", "ssh"),
            "failures aggregate per 10 minutes");
        Assert.IsTrue(RouterLogins.IsAdminGroup(["read", "write", "ssh"]));
        Assert.IsFalse(RouterLogins.IsAdminGroup(["read", "ssh"]));
    }
}
