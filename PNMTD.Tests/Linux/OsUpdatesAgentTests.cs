// The agent is Linux-only; these tests read its parsers on any OS.
#pragma warning disable CA1416
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Rights;
using PNMTD.Sensor.Linux.OsUpdates;
using PNMTD.Sensor.Linux.Rights;

namespace PNMTD.Tests.Linux;

/// <summary>
/// The Linux agent's OS updates (docs/linux-os-updates-plan.md §4, §12): apt's, dpkg's and needrestart's output parsed from O0's fixtures
/// (Debian 12, apt 2.6, captured in the lab on 2026-11-21), dpkg's version order, the changelog's part since a version, config.yaml's
/// os_updates (on unless it says false, D1; off in a refused file), and the daily slot.
/// </summary>
[TestClass]
public sealed class OsUpdatesAgentTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Linux", "os-updates", name));

    // --- the simulation and its lookups -----------------------------------------------------------------------------------

    [TestMethod]
    public void TheSimulationGivesEachPackageWithItsVersionsOriginAndArchitecture()
    {
        var simulation = AptParsers.ParseSimulation(Fixture("debian12-simulation.txt"));
        Assert.AreEqual(8, simulation.Installs.Count());
        Assert.AreEqual(0, simulation.Removals.Count());
        var libssl = simulation.Installs.Single(c => c.Name == "libssl3");
        Assert.AreEqual("3.0.20-1~deb12u2", libssl.Installed);
        Assert.AreEqual("3.0.22-1~deb12u2", libssl.Candidate);
        Assert.AreEqual("arm64", libssl.Arch);
        Assert.AreEqual("Debian-Security:12/oldstable-security", libssl.Origins);
        Assert.IsTrue(AptParsers.IsSecurityOrigin(libssl.Origins));
        var ssh = simulation.Installs.Single(c => c.Name == "openssh-server");
        Assert.AreEqual("1:9.2p1-2+deb12u10", ssh.Candidate);
        Assert.IsFalse(AptParsers.IsSecurityOrigin(ssh.Origins), "Debian:12.15/oldstable is the point release, not the security archive.");
        Assert.AreEqual("all", simulation.Installs.Single(c => c.Name == "tzdata").Arch);
    }

    [TestMethod]
    public void RemovalsNewPackagesKeptBackAndPhasedAreRead()
    {
        var simulation = AptParsers.ParseSimulation("""
            The following packages have been kept back:
              linux-image-amd64 snapd
            The following upgrades have been deferred due to phasing:
              ubuntu-advantage-tools
            Inst linux-image-6.1.0-29-amd64 (6.1.123-1 Debian-Security:12/oldstable-security [amd64])
            Remv libfoo1 [1.0-1]
            Remv libbar2:amd64 [2.3-4]
            Inst openssl [3.0.20-1~deb12u2] (3.0.22-1~deb12u2 Debian:12.15/oldstable, Debian-Security:12/oldstable-security [amd64])
            """);
        Assert.IsNull(simulation.Installs.Single(c => c.Name == "linux-image-6.1.0-29-amd64").Installed, "A new package has no installed version.");
        CollectionAssert.AreEquivalent(new[] { "libfoo1", "libbar2" }, simulation.Removals.Select(r => r.Name).ToList());
        Assert.AreEqual("amd64", simulation.Removals.Single(r => r.Name == "libbar2").Arch);
        CollectionAssert.AreEqual(new[] { "linux-image-amd64", "snapd" }, simulation.KeptBack.ToList());
        CollectionAssert.AreEqual(new[] { "ubuntu-advantage-tools" }, simulation.Phased.ToList());
        Assert.IsTrue(AptParsers.IsSecurityOrigin(simulation.Installs.Single(c => c.Name == "openssl").Origins), "One of two origins is the security archive.");
    }

    [TestMethod]
    public void TheRemovalsHashIsTheSameForTheSameNamesInAnyOrder()
    {
        Assert.AreEqual(AptPlan.HashOf(["b", "a", "a"]), AptPlan.HashOf(["a", "b"]));
        Assert.AreNotEqual(AptPlan.HashOf(["a"]), AptPlan.HashOf(["a", "b"]));
        Assert.AreEqual(16, AptPlan.HashOf([]).Length);
    }

    [TestMethod]
    public void SizesComeFromAptsAssumeNoInSiUnits()
    {
        Assert.AreEqual(((long?)5_046_000, (long?)10_200), AptParsers.ParseSizes(Fixture("debian12-sizes.txt")));
        Assert.AreEqual(((long?)1_234_000, (long?)-3_500_000),
            AptParsers.ParseSizes("Need to get 1,234 kB/5,246 kB of archives.\nAfter this operation, 3.5 MB disk space will be freed."));
        Assert.AreEqual(((long?)null, (long?)null), AptParsers.ParseSizes("0 upgraded, 0 newly installed, 0 to remove and 0 not upgraded."));
    }

    [TestMethod]
    public void AptCacheShowAndDpkgQueryGiveTheSourcePackages()
    {
        var sources = AptParsers.ParseAptCacheShow(Fixture("debian12-apt-cache-show.txt"));
        Assert.AreEqual(("openssl", "3.0.22-1~deb12u2"), sources[("libssl3", "3.0.22-1~deb12u2")]);
        Assert.AreEqual(("openssh", "1:9.2p1-2+deb12u10"), sources[("openssh-client", "1:9.2p1-2+deb12u10")]);
        Assert.AreEqual(("tzdata", "2026f-0+deb12u1"), sources[("tzdata", "2026f-0+deb12u1")], "No Source: field: the package itself.");
        var binNmu = AptParsers.ParseAptCacheShow("Package: libcap2-bin\nSource: libcap2 (1:2.66-4+deb12u3)\nVersion: 1:2.66-4+deb12u3+b1\n");
        Assert.AreEqual(("libcap2", "1:2.66-4+deb12u3"), binNmu[("libcap2-bin", "1:2.66-4+deb12u3+b1")]);
        var installed = AptParsers.ParseDpkgQuery(Fixture("debian12-dpkg-query.txt"));
        Assert.AreEqual(("3.0.20-1~deb12u2", "openssl", "3.0.20-1~deb12u2"), installed[("libssl3", "arm64")]);
        Assert.IsTrue(installed.ContainsKey(("tzdata", "all")));
    }

    // --- an update's and an upgrade's output -----------------------------------------------------------------------------

    [TestMethod]
    public void AnUpdateThatReachedEverythingSucceeded()
    {
        var output = AptParsers.ParseUpdate(Fixture("debian12-update-ok.txt"));
        Assert.AreEqual(3, output.Reached);
        Assert.AreEqual(0, output.Failed);
        StringAssert.StartsWith(output.Fetched, "Fetched 467 kB");
        Assert.AreEqual(OsUpdateState.Succeeded, AptParsers.UpdateState(0, output));
    }

    [TestMethod]
    public void ARepositoryAnswering401MakesWarningsThoughAptExits100()
    {
        // O0: Proxmox's enterprise repository without a subscription; Debian's lists were refreshed all the same.
        var output = AptParsers.ParseUpdate(Fixture("debian12-update-401.txt"));
        Assert.AreEqual(3, output.Reached);
        Assert.AreEqual(1, output.Failed);
        Assert.IsTrue(output.Errors.Any(e => e.Contains("401  Unauthorized", StringComparison.Ordinal)));
        Assert.AreEqual(OsUpdateState.Warnings, AptParsers.UpdateState(100, output));
        var two = AptParsers.ParseUpdate(Fixture("debian12-update-401-unresolvable.txt"));
        Assert.AreEqual(2, two.Failed, "Ign then Err for the same repository is one failure.");
        Assert.AreEqual(3, two.Reached);
    }

    [TestMethod]
    public void NothingReachedOrALockIsAFailure()
    {
        var none = AptParsers.ParseUpdate("Err:1 http://deb.debian.org/debian bookworm InRelease\n  Could not resolve 'deb.debian.org'\nE: Failed to fetch …\n");
        Assert.AreEqual(OsUpdateState.Failed, AptParsers.UpdateState(100, none));
        var locked = AptParsers.ParseUpdate("Reading package lists...\nE: Could not get lock /var/lib/apt/lists/lock. It is held by process 3103 (python3)\nE: Unable to lock directory /var/lib/apt/lists/\n");
        Assert.IsTrue(locked.Locked);
        Assert.AreEqual(OsUpdateState.Failed, AptParsers.UpdateState(100, locked));
    }

    [TestMethod]
    public void AnUpgradesOutputGivesNeedrestartsServicesAndItsLogIsCleaned()
    {
        var raw = Fixture("debian12-full-upgrade.log");
        Assert.IsTrue(raw.Contains('\r'), "dpkg's progress uses carriage returns (O0).");
        var log = AptParsers.Clean(raw);
        Assert.IsFalse(log.Contains('\r'));
        StringAssert.Contains(log, "(Reading database ... 11878 files and directories currently installed.)");
        Assert.IsFalse(log.Contains("(Reading database ... 5%", StringComparison.Ordinal), "Only what the terminal showed last.");
        var (kept, services, warnings, errors) = AptParsers.ParseUpgrade(log);
        Assert.AreEqual(0, kept.Count);
        CollectionAssert.AreEqual(new[] { "ssh.service", "systemd-journald.service", "systemd-manager", "systemd-logind.service" }, services);
        Assert.AreEqual(0, warnings.Count + errors.Count);
        Assert.AreEqual("a\tb\nc", AptParsers.Clean("a\tb\u001b[0m\nc"), "ANSI escapes go, tabs stay.");
    }

    [TestMethod]
    public void KeptConfigurationFilesAreFound()
    {
        var (kept, _, _, _) = AptParsers.ParseUpgrade("""
            Setting up openssh-server (1:9.2p1-2+deb12u10) ...

            Configuration file '/etc/ssh/moduli'
             ==> Modified (by you or by a script) since installation.
             ==> Package distributor has shipped an updated version.
             ==> Keeping old config file as default.
            Setting up foo (1.2) ...
            """);
        CollectionAssert.AreEqual(new[] { "/etc/ssh/moduli" }, kept);
    }

    [TestMethod]
    public void NeedrestartsBatchModeIsRead()
    {
        var info = AptParsers.ParseNeedrestart(Fixture("debian12-needrestart.txt"));
        CollectionAssert.Contains(info.Services.ToList(), "ssh.service");
        Assert.IsFalse(info.KernelPending);
        var kernel = AptParsers.ParseNeedrestart("NEEDRESTART-VER: 3.6\nNEEDRESTART-KCUR: 6.1.0-26-amd64\nNEEDRESTART-KEXP: 6.1.0-28-amd64\nNEEDRESTART-KSTA: 3\n");
        Assert.IsTrue(kernel.KernelPending);
        Assert.AreEqual("6.1.0-28-amd64", kernel.ExpectedKernel);
    }

    [TestMethod]
    public void HistoryLogGivesEachRunWithItsTagAndPackages()
    {
        var entries = AptParsers.ParseHistory(Fixture("debian12-history.log"), TimeZoneInfo.Utc);
        Assert.IsTrue(entries.Count >= 3);
        var downgrade = entries.First(e => e.Commandline?.Contains("--allow-downgrades", StringComparison.Ordinal) == true);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 21, 9, 55, 13, TimeSpan.Zero), downgrade.StartedAt);
        Assert.AreEqual(8, downgrade.Upgraded.Count, "Downgrades are changes from one version to another too.");
        Assert.IsTrue(downgrade.Installed.Any(p => p is { Name: "needrestart", To: "3.6-4+deb12u4", From: null }));
        var tagged = entries.Single(e => e.Commandline?.Contains("PNMT::Run=0123abcd", StringComparison.Ordinal) == true);
        Assert.AreEqual(new PackageChange("tzdata", "arm64", "2026b-0+deb12u1", "2026f-0+deb12u1"), tagged.Upgraded.Single());
        Assert.AreEqual(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        var zurich = AptParsers.ParseHistory("Start-Date: 2026-07-01  10:00:00\nCommandline: apt upgrade\nRequested-By: alice (1000)\nEnd-Date: 2026-07-01  10:01:00\n",
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich")).Single();
        Assert.AreEqual(TimeSpan.FromHours(2), zurich.StartedAt.Offset, "The server's local time, summer time included.");
        Assert.AreEqual("alice (1000)", zurich.RequestedBy);
    }

    // --- versions and changelogs --------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("1.0", "1.0", 0)]
    [DataRow("1.0", "1.0-0", 0)]
    [DataRow("1:1.0", "2.0", 1)]
    [DataRow("1.0~rc1", "1.0", -1)]
    [DataRow("1.0", "1.0+b1", -1)]
    [DataRow("1.0-1", "1.0-1~deb12u1", 1)]
    [DataRow("3.0.20-1~deb12u2", "3.0.22-1~deb12u2", -1)]
    [DataRow("1:9.2p1-2+deb12u9", "1:9.2p1-2+deb12u10", -1)]
    [DataRow("2.38.1-5+deb12u3", "2.38.1-5+deb12u10", -1)]
    [DataRow("1.0a", "1.0+", -1)]
    [DataRow("1.0", "1.00", 0)]
    [DataRow("1.2.3", "1.2.10", -1)]
    [DataRow("1.0~~", "1.0~", -1)]
    [DataRow("2026b-0+deb12u1", "2026f-0+deb12u1", -1)]
    public void DebianVersionsCompareAsDpkgDoes(string a, string b, int sign)
    {
        Assert.AreEqual(sign, Math.Sign(DebianVersion.Compare(a, b)), $"{a} vs {b}");
        Assert.AreEqual(-sign, Math.Sign(DebianVersion.Compare(b, a)), $"{b} vs {a}");
    }

    [TestMethod]
    public void TheChangelogSinceTheInstalledVersionNamesItsCvesAndUrgencies()
    {
        var changelog = Fixture("debian12-openssl-changelog.txt");
        var (text, cves, urgencies) = DebianChangelog.Since(changelog, "3.0.20-1~deb12u2");
        CollectionAssert.Contains(cves, "CVE-2026-84782");
        CollectionAssert.Contains(cves, "CVE-2026-42767");
        CollectionAssert.DoesNotContain(cves, "CVE-2026-7383", "That one was fixed in the installed version already.");
        CollectionAssert.AreEqual(new[] { "high", "medium" }, urgencies);
        StringAssert.StartsWith(text, "openssl (3.0.22-1~deb12u2) bookworm-security; urgency=high");
        Assert.IsFalse(text.Contains("3.0.20-1~deb12u2) bookworm", StringComparison.Ordinal));
        var newest = DebianChangelog.Since(changelog, "3.0.22-1~deb12u1");
        CollectionAssert.DoesNotContain(newest.Cves, "CVE-2026-42767");
        var (cut, all, _) = DebianChangelog.Since(changelog, null, maxChars: 200);
        Assert.IsTrue(cut.Length <= 200);
        Assert.IsTrue(all.Count > 10, "The CVEs of every newer entry count, though the text is cut.");
    }

    // --- config.yaml and the schedule ------------------------------------------------------------------------------------------

    [TestMethod]
    public void OsUpdatesAreOnUnlessConfigYamlSaysFalse()
    {
        Assert.AreEqual(OsUpdateRights.On, AgentRights.Defaults.OsUpdates, "D1: no file, on.");
        Assert.AreEqual(OsUpdateRights.On, ConfigYaml.Parse("ssh_keys:\n  remove: false\n").Rights.OsUpdates, "No os_updates block, on.");
        Assert.AreEqual(OsUpdateRights.On, ConfigYaml.Parse("os_updates:\n  update:\n").Rights.OsUpdates, "A key without a value, on.");
        var off = ConfigYaml.Parse("os_updates:\n  upgrade: false\n  full_upgrade: false\n").Rights;
        Assert.AreEqual(new OsUpdateRights(true, false, false, true), off.OsUpdates);
        Assert.AreEqual("OS updates: update allowed", off.OsUpdates.Describe());
        var typo = ConfigYaml.Parse("os_updates:\n  upgrade: yes\n  restart_services: auto\n").Rights;
        Assert.IsFalse(typo.OsUpdates.Upgrade, "yes is no boolean: that setting is off, with a warning.");
        Assert.IsTrue(typo.OsUpdates.Update);
        Assert.IsTrue(typo.Warnings.Any(w => w.StartsWith("os_updates.upgrade", StringComparison.Ordinal)));
        Assert.IsTrue(typo.Warnings.Any(w => w.StartsWith("os_updates.restart_services", StringComparison.Ordinal)), "D4: no such setting.");
        Assert.AreEqual(OsUpdateRights.Off, ConfigYaml.Parse("os_updates: true\n").Rights.OsUpdates, "Not a group: all of it off.");
        Assert.AreEqual(OsUpdateRights.Off, AgentRights.RefusedBecause("it is writable by others").OsUpdates, "A refused file grants nothing.");
        Assert.AreEqual(OsUpdateRights.Off, ConfigYaml.Parse("[").Rights.OsUpdates);
        var summary = ConfigYaml.Parse("os_updates:\n  changelogs: false\n").Rights.Summary();
        Assert.AreEqual(new OsUpdatesRightsSummary(true, true, true, false), summary.OsUpdates);
    }

    [TestMethod]
    public void TheTemplateNamesEveryOsUpdatesSettingAsTrue()
    {
        var template = File.ReadAllText(Path.Combine(FindRepository(), "PNMTD.Sensor.Linux", "Rights", "config.yaml"));
        var rights = ConfigYaml.Parse(template).Rights;
        Assert.AreEqual(OsUpdateRights.On, rights.OsUpdates);
        Assert.AreEqual(0, rights.Warnings.Count, string.Join("; ", rights.Warnings));
        Assert.IsFalse(rights.Scripts.Enabled, "Everything else stays off.");
    }

    private static string FindRepository()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "PNMTD.slnx")))
        {
            folder = folder.Parent;
        }
        return folder?.FullName ?? throw new InvalidOperationException("The repository wasn't found.");
    }

    [TestMethod]
    public void TheDailySlotIsStablePerAgentAndDueOncePerDay()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");
        var config = new OsUpdatesConfig(true, true, "04:00", 120);
        var agent = Guid.Parse("0b7d3c1e-0000-4000-8000-000000000001");
        var day = new DateOnly(2026, 11, 21);
        var slot = OsUpdatesFeature.Slot(day, config, agent, zone);
        Assert.AreEqual(slot, OsUpdatesFeature.Slot(day, config, agent, zone));
        var local = TimeZoneInfo.ConvertTime(slot, zone);
        Assert.IsTrue(local.Hour is 4 or 5, $"{local} is in the window.");
        var started = slot.AddHours(-5);
        var lastUpdate = slot.AddHours(-20);
        var schedule = new ScheduleState(day.AddDays(-1), lastUpdate, OsUpdateState.Succeeded, Guid.NewGuid());
        Assert.IsNull(OsUpdatesFeature.Due(slot.AddMinutes(-1), schedule, config, OsUpdateRights.On, agent, started, zone), "Before the slot.");
        Assert.AreEqual(day, OsUpdatesFeature.Due(slot.AddMinutes(1), schedule, config, OsUpdateRights.On, agent, started, zone));
        Assert.IsNull(OsUpdatesFeature.Due(slot.AddMinutes(1), schedule with { LastScheduledDay = day }, config, OsUpdateRights.On, agent, started, zone), "Once a day.");
        Assert.IsNull(OsUpdatesFeature.Due(slot.AddMinutes(1), schedule, config with { DailyUpdate = false }, OsUpdateRights.On, agent, started, zone));
        Assert.IsNull(OsUpdatesFeature.Due(slot.AddMinutes(1), schedule, config, OsUpdateRights.On with { Update = false }, agent, started, zone));
        Assert.IsNull(OsUpdatesFeature.Due(slot.AddMinutes(1), schedule, null, OsUpdateRights.On, agent, started, zone), "An older PNMTD sends no window.");
        Assert.IsNull(OsUpdatesFeature.Due(slot.AddMinutes(1), schedule, config, OsUpdateRights.On, agent, slot, zone), "Not in the first two minutes.");
        var overdue = schedule with { LastUpdateAt = slot.AddHours(-30) };
        Assert.AreEqual(day, OsUpdatesFeature.Due(slot.AddHours(-3), overdue, config, OsUpdateRights.On, agent, started, zone), "Off at yesterday's slot: at once.");
    }
}
