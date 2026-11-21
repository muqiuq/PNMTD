using System.Text;
using System.Text.Json;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.OsUpdates;

namespace PNMTD.Cli;

/// <summary>
/// OS updates for the seeded Linux servers (docs/linux-os-updates-plan.md): their pending packages, two weeks of daily updates (pve-01's with
/// the enterprise repository's 401), upgrades (web-01's by a person, one by unattended-upgrades, an admin's apt), pve-01's failed full upgrade
/// marked handled, the advisory sources with fixes and risks for openssl and expat, the findings the evaluator's code rates from them (web-01's
/// high risk open, pve-01's acknowledged), and an advisory text the AI verified. The sensors stay OK: the check's 466 comes with the next
/// heartbeat, when the check is on and a server reports.
/// </summary>
internal static class DevSeedOsUpdates
{
    private static readonly string[] OpensslCves = ["CVE-2026-84782", "CVE-2026-35189", "CVE-2026-75806"];

    public static int Seed(PnmtdDbContext db, List<(HostEntity Host, SensorEntity Sensor)> servers, DateTime now)
    {
        var count = 0;
        var sources = SeedSources(db, now);
        foreach (var (host, sensor) in servers)
        {
            var proxmox = host.Name == "pve-01";
            var agent = db.Agents.Local.Single(a => a.SensorId == sensor.Id);
            var section = SimulatedLinux.OsUpdates(proxmox);
            foreach (var p in section.Packages)
            {
                db.OsPendingPackages.Add(new OsPendingPackageEntity
                {
                    SensorId = sensor.Id, Name = p.Name, Arch = p.Arch ?? "", Installed = p.Installed, Candidate = p.Candidate, Source = p.Source ?? p.Name,
                    SourceVersion = p.SourceVersion ?? p.Candidate, InstalledSourceVersion = p.InstalledSourceVersion, Origin = p.Origin, Security = p.Security,
                    InUpgrade = p.InUpgrade, Phased = p.Phased, FirstSeenAt = now.AddDays(-2),
                });
            }
            agent.PendingUpdates = section.PackagesTotal;
            agent.PendingSecurity = section.Packages.Count(p => p.Security);
            agent.ListsUpdatedAt = now.Date.AddHours(4).AddMinutes(proxmox ? 12 : 37) is var today && today < now ? today : today.AddDays(-1);
            agent.OsDistribution = section.Distribution;
            count += SeedRuns(db, agent, sensor, proxmox, now);
            count += SeedFindings(db, agent, sensor, proxmox, sources, now);
        }
        return count;
    }

    // --- runs ----------------------------------------------------------------------------------------------------------------

    private static int SeedRuns(PnmtdDbContext db, AgentEntity agent, SensorEntity sensor, bool proxmox, DateTime now)
    {
        var count = 0;
        for (var day = 13; day >= 0; day--)
        {
            var at = now.Date.AddDays(-day).AddHours(4).AddMinutes(proxmox ? 12 : 37);
            if (at > now)
            {
                continue;
            }
            var log = proxmox
                ? $"PNMT: apt-get -q update (daily), started {at:yyyy-MM-dd HH:mm:ss} UTC\nHit:1 http://deb.debian.org/debian trixie InRelease\nHit:2 http://deb.debian.org/debian trixie-updates InRelease\n"
                  + "Hit:3 http://security.debian.org/debian-security trixie-security InRelease\nHit:4 http://download.proxmox.com/debian/pve trixie InRelease\n"
                  + "Err:5 https://enterprise.proxmox.com/debian/pve trixie InRelease\n  401  Unauthorized [IP: 45.84.67.184 443]\nReading package lists...\n"
                  + "E: Failed to fetch https://enterprise.proxmox.com/debian/pve/dists/trixie/InRelease  401  Unauthorized [IP: 45.84.67.184 443]\n"
                  + "E: The repository 'https://enterprise.proxmox.com/debian/pve trixie InRelease' is not signed.\n"
                : $"PNMT: apt-get -q update (daily), started {at:yyyy-MM-dd HH:mm:ss} UTC\nHit:1 http://deb.debian.org/debian bookworm InRelease\n"
                  + "Get:2 http://deb.debian.org/debian bookworm-updates InRelease [55.4 kB]\nGet:3 http://deb.debian.org/debian-security bookworm-security InRelease [34.8 kB]\n"
                  + "Get:4 http://deb.debian.org/debian-security bookworm-security/main amd64 Packages [377 kB]\nFetched 467 kB in 0s (2162 kB/s)\nReading package lists...\n";
            var summary = proxmox
                ? OsUpdateSummary.Empty with
                {
                    RepositoriesReached = 4, RepositoriesFailed = 1,
                    Errors = ["Failed to fetch https://enterprise.proxmox.com/debian/pve/dists/trixie/InRelease  401  Unauthorized [IP: 45.84.67.184 443]"],
                }
                : OsUpdateSummary.Empty with { RepositoriesReached = 3, Fetched = "Fetched 467 kB in 0s (2162 kB/s)" };
            Run(db, sensor.Id, OsUpdateKind.Update, null, OsUpdateTrigger.Schedule, null, at, TimeSpan.FromSeconds(proxmox ? 9 : 4),
                proxmox ? OsUpdateState.Warnings : OsUpdateState.Succeeded, proxmox ? 100 : 0, summary,
                proxmox ? "1 of 5 repositories failed: Failed to fetch https://enterprise.proxmox.com/debian/pve/dists/trixie/InRelease  401  Unauthorized [IP: 45.84.67.184 443]" : null,
                "apt-get -q -o APT::Color=0 -o Dpkg::Progress-Fancy=0 -o DPkg::Lock::Timeout=600 update", log);
            count++;
        }
        if (!proxmox)
        {
            var upgraded = now.AddDays(-12).Date.AddHours(9).AddMinutes(14);
            var packages = new[] { ("curl", "7.88.1-10+deb12u12", "7.88.1-10+deb12u14"), ("libcurl4", "7.88.1-10+deb12u12", "7.88.1-10+deb12u14"),
                ("nginx", "1.22.1-9+deb12u2", "1.22.1-9+deb12u3"), ("libxml2", "2.9.14+dfsg-1.3~deb12u2", "2.9.14+dfsg-1.3~deb12u4") };
            var summary = OsUpdateSummary.Empty with
            {
                Upgraded = packages.Select(p => new PackageChange(p.Item1, "amd64", p.Item2, p.Item3)).ToList(), ServicesToRestart = ["nginx.service"],
                ConfigFilesKept = ["/etc/nginx/nginx.conf"],
            };
            var log = $"PNMT: apt-get -y -q upgrade --with-new-pkgs (for Philipp Albrecht), started {upgraded:yyyy-MM-dd HH:mm:ss} UTC\nReading package lists...\n"
                      + "Calculating upgrade...\nThe following packages will be upgraded:\n  curl libcurl4 libxml2 nginx\n4 upgraded, 0 newly installed, 0 to remove and 0 not upgraded.\n"
                      + "Need to get 2121 kB of archives.\n"
                      + string.Concat(packages.Select(p => $"Preparing to unpack .../{p.Item1}_{p.Item3}_amd64.deb ...\nUnpacking {p.Item1}:amd64 ({p.Item3}) over ({p.Item2}) ...\n"))
                      + "Setting up nginx (1.22.1-9+deb12u3) ...\n\nConfiguration file '/etc/nginx/nginx.conf'\n ==> Modified (by you or by a script) since installation.\n"
                      + " ==> Package distributor has shipped an updated version.\n ==> Keeping old config file as default.\n\nServices to be restarted:\n systemctl restart nginx.service\n";
            Run(db, sensor.Id, OsUpdateKind.Upgrade, OsUpgradeMode.Upgrade, OsUpdateTrigger.Person, "Philipp Albrecht", upgraded, TimeSpan.FromSeconds(41),
                OsUpdateState.Succeeded, 0, summary, null, "apt-get -y -q -o APT::Color=0 upgrade --with-new-pkgs", log);
            Event(db, sensor.Id, upgraded.AddSeconds(41), PNMTStatusCodes.LINUX_UPDATES_INSTALLED,
                "web-01: 4 packages upgraded, 0 installed, 0 removed, for Philipp Albrecht; 1 services need a restart");
            agent.LastUpgradeAt = upgraded.AddSeconds(41);
            var unattended = now.AddDays(-3).Date.AddHours(6).AddMinutes(21);
            External(db, sensor.Id, unattended, "/usr/bin/unattended-upgrade", null,
                [new PackageChange("libc6", "amd64", "2.36-9+deb12u13", "2.36-9+deb12u14"), new PackageChange("libc-bin", "amd64", "2.36-9+deb12u13", "2.36-9+deb12u14")], []);
            External(db, sensor.Id, now.AddDays(-20).Date.AddHours(15).AddMinutes(2), "apt install htop", "philipp (1000)", [],
                [new PackageChange("htop", "amd64", null, "3.2.2-2")]);
            count += 3;
        }
        else
        {
            var failed = now.AddDays(-1).Date.AddHours(21).AddMinutes(5);
            var log = $"PNMT: apt-get -y -q full-upgrade (for Philipp Albrecht), started {failed:yyyy-MM-dd HH:mm:ss} UTC\nReading package lists...\nCalculating upgrade...\n"
                      + "Setting up pve-manager (9.0.11) ...\nJob for pveproxy.service failed because the control process exited with error code.\n"
                      + "dpkg: error processing package pve-manager (--configure):\n installed pve-manager package post-installation script subprocess returned error exit status 1\n"
                      + "Errors were encountered while processing:\n pve-manager\nE: Sub-process /usr/bin/dpkg returned an error code (1)\n";
            var run = Run(db, sensor.Id, OsUpdateKind.Upgrade, OsUpgradeMode.FullUpgrade, OsUpdateTrigger.Person, "Philipp Albrecht", failed, TimeSpan.FromMinutes(3),
                OsUpdateState.Failed, 100, OsUpdateSummary.Empty with { Errors = ["Sub-process /usr/bin/dpkg returned an error code (1)"] },
                "E: Sub-process /usr/bin/dpkg returned an error code (1)", "apt-get -y -q -o APT::Color=0 full-upgrade", log);
            run.DismissedAt = now.AddHours(-10);
            run.DismissedByName = "Philipp Albrecht";
            Event(db, sensor.Id, failed.AddMinutes(4), PNMTStatusCodes.LINUX_UPDATE_FAILED,
                $"pve-01: the upgrade on {failed.AddMinutes(3):yyyy-MM-dd HH:mm} UTC failed (exit 100): E: Sub-process /usr/bin/dpkg returned an error code (1)");
            Event(db, sensor.Id, now.AddHours(-10), PNMTStatusCodes.LINUX_OK, $"pve-01: {SimulatedLinux.ProxmoxOs.Caption}, kernel {SimulatedLinux.ProxmoxOs.Build}, agent {ServerInfo.Version}");
            agent.LastUpgradeAt = failed.AddMinutes(3);
            count++;
        }
        return count;
    }

    private static OsUpdateRunEntity Run(PnmtdDbContext db, Guid sensorId, OsUpdateKind kind, OsUpgradeMode? mode, OsUpdateTrigger trigger, string? who, DateTime at,
        TimeSpan took, OsUpdateState state, int? exit, OsUpdateSummary summary, string? message, string command, string log)
    {
        var bytes = Encoding.UTF8.GetByteCount(log);
        var run = new OsUpdateRunEntity
        {
            Id = Guid.NewGuid(), SensorId = sensorId, Kind = kind, Mode = mode, Trigger = trigger, RequestedByName = who, RequestedAt = at, StartedAt = at,
            FinishedAt = at + took, State = state, ExitCode = exit, Summary = JsonSerializer.Serialize(summary, AgentJson.Options), Message = message, Command = command,
            LogBytes = bytes, ReportedLogBytes = bytes, LastHeardAt = at + took,
            LogSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(log))).ToLowerInvariant(),
        };
        db.OsUpdateRuns.Add(run);
        db.OsUpdateLogChunks.Add(new OsUpdateLogChunkEntity { RunId = run.Id, Offset = 0, Text = log, Bytes = bytes });
        return run;
    }

    private static void External(PnmtdDbContext db, Guid sensorId, DateTime at, string command, string? who, IReadOnlyList<PackageChange> upgraded,
        IReadOnlyList<PackageChange> installed) => db.OsUpdateRuns.Add(new OsUpdateRunEntity
    {
        Id = Guid.NewGuid(), SensorId = sensorId, Kind = OsUpdateKind.Upgrade, Trigger = OsUpdateTrigger.External, RequestedByName = who, RequestedAt = at, StartedAt = at,
        FinishedAt = at.AddSeconds(30), State = OsUpdateState.Succeeded, Command = command, ExternalId = Guid.NewGuid().ToString("N")[..16], LastHeardAt = at.AddMinutes(5),
        Summary = JsonSerializer.Serialize(OsUpdateSummary.Empty with { Upgraded = upgraded, Installed = installed }, AgentJson.Options),
    });

    private static void Event(PnmtdDbContext db, Guid sensorId, DateTime at, int code, string message) => db.Events.Add(new EventEntity
    {
        Id = Guid.NewGuid(), SensorId = sensorId, Code = code, Message = message, Created = at, Source = AgentPlatformInfo.Linux.EventSource,
    });

    // --- the check ------------------------------------------------------------------------------------------------------------

    private static List<OsAdvisorySourceEntity> SeedSources(PnmtdDbContext db, DateTime now)
    {
        var sources = OsAdvisorySourceDefaults.All.Select(d => new OsAdvisorySourceEntity
        {
            Id = Guid.NewGuid(), Kind = d.Kind, Name = d.Name, Url = d.Url, Enabled = d.Enabled, IntervalMinutes = d.Minutes, CreatedAt = now.AddDays(-14),
            // Fetched just now, so a development server doesn't download them at its start.
            LastFetchAt = d.Enabled ? now.AddMinutes(-20) : null, LastSuccessAt = d.Enabled ? now.AddMinutes(-20) : null,
            Entries = d.Kind switch
            {
                OsAdvisorySourceKind.DebianTracker => 148_211,
                OsAdvisorySourceKind.UbuntuNotices => 0,
                OsAdvisorySourceKind.CisaKev => 1_790,
                OsAdvisorySourceKind.Epss => 96_412,
                _ => 0,
            },
        }).ToList();
        db.OsAdvisorySources.AddRange(sources);
        var tracker = sources.First(s => s.Kind == OsAdvisorySourceKind.DebianTracker);
        foreach (var cve in OpensslCves)
        {
            db.OsFixes.Add(new OsFixEntity { SourceId = tracker.Id, Distribution = "debian:bookworm", SourcePackage = "openssl", Cve = cve, FixedVersion = "3.0.22-1~deb12u2",
                Urgency = "not yet assigned", Scope = cve == "CVE-2026-75806" ? null : "local" });
            db.OsFixes.Add(new OsFixEntity { SourceId = tracker.Id, Distribution = "debian:trixie", SourcePackage = "openssl", Cve = cve, FixedVersion = "3.5.7-1~deb13u3",
                Urgency = "not yet assigned" });
        }
        db.OsFixes.Add(new OsFixEntity { SourceId = tracker.Id, Distribution = "debian:bookworm", SourcePackage = "expat", Cve = "CVE-2026-61123", FixedVersion = "2.5.0-1+deb12u5",
            Urgency = "medium" });
        db.OsCveRisks.Add(new OsCveRiskEntity { Cve = "CVE-2026-84782", Kev = true, KevAddedAt = DateOnly.FromDateTime(now.AddDays(-3)), KevName = "OpenSSL DTLS Out-of-Bounds Read",
            Epss = 0.71, EpssPercentile = 0.997, EpssDate = DateOnly.FromDateTime(now), UpdatedAt = now });
        db.OsCveRisks.Add(new OsCveRiskEntity { Cve = "CVE-2026-35189", Epss = 0.002, EpssPercentile = 0.41, EpssDate = DateOnly.FromDateTime(now), UpdatedAt = now });
        db.OsCveRisks.Add(new OsCveRiskEntity { Cve = "CVE-2026-75806", Epss = 0.06, EpssPercentile = 0.93, EpssDate = DateOnly.FromDateTime(now), UpdatedAt = now });
        db.OsCveRisks.Add(new OsCveRiskEntity { Cve = "CVE-2026-61123", Epss = 0.01, EpssPercentile = 0.8, EpssDate = DateOnly.FromDateTime(now), UpdatedAt = now });
        var dsa = sources.First(s => s.Kind == OsAdvisorySourceKind.Rss);
        var text = "DSA-6061-1 openssl -- security update\n\nIt was discovered that OpenSSL mishandled DTLS retransmissions (CVE-2026-84782). "
                   + "This issue is being actively exploited in the wild.\n\nFor the oldstable distribution (bookworm), these problems have been fixed in version 3.0.22-1~deb12u2.";
        var claims = new[] { new OsAdvisoryClaim("CVE-2026-84782", ["OpenSSL"], true, false, true, "This issue is being actively exploited in the wild.") };
        db.OsAdvisoryItems.Add(new OsAdvisoryItemEntity
        {
            Id = Guid.NewGuid(), SourceId = dsa.Id, Guid = "https://lists.debian.org/debian-security-announce/2026/msg00561.html", Title = "DSA-6061-1 openssl - security update",
            Link = "https://lists.debian.org/debian-security-announce/2026/msg00561.html", PublishedAt = now.AddDays(-3), FirstSeenAt = now.AddDays(-3), Text = text,
            Cves = ["CVE-2026-84782"], Keywords = ["actively", "exploited", "in the wild"], AssessedBy = "verified", AssessedAt = now.AddDays(-3).AddMinutes(4),
            Assessment = new OsAdvisoryAssessment(claims, "OpenSSL's DTLS issue CVE-2026-84782 is being exploited.", new Dictionary<string, string> { ["CVE-2026-84782"] = "agree" },
                [], 0.02).ToJson(),
            Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
        });
        db.OsPackageChangelogs.Add(new OsPackageChangelogEntity
        {
            Distribution = "debian:bookworm", SourcePackage = "openssl", Version = "3.0.22-1~deb12u2", InstalledVersion = "3.0.20-1~deb12u2", Cves = [.. OpensslCves],
            Urgencies = ["high", "medium"], FetchedFrom = "the .deb", ReceivedAt = now.AddDays(-2),
            Text = "openssl (3.0.22-1~deb12u2) bookworm-security; urgency=high\n\n  * Non-maintainer upload by the LTS Team.\n"
                   + "  * CVE-2026-84782 (\"DTLS Retransmits Handshake Messages From a Stale Buffer Offset\")\n  * CVE-2026-35189 (\"Memory Allocation in Relative CRLDP Processing\")\n"
                   + "  * CVE-2026-75806 (\"Unauthenticated and Undersized DTLS 1.2 AEAD Record Causes DoS\")\n\n -- Arnaud Rebillout <arnaudr@debian.org>  Wed, 21 Oct 2026 16:54:24 +0700",
        });
        return sources;
    }

    /// <summary>The findings as the evaluator's code rates them (§6.4), stored as it would; pve-01's acknowledged by a person until next week.</summary>
    private static int SeedFindings(PnmtdDbContext db, AgentEntity agent, SensorEntity sensor, bool proxmox, List<OsAdvisorySourceEntity> sources, DateTime now)
    {
        var tracker = sources.First(s => s.Kind == OsAdvisorySourceKind.DebianTracker);
        var distribution = proxmox ? "debian:trixie" : "debian:bookworm";
        var pending = db.OsPendingPackages.Local.Where(p => p.SensorId == sensor.Id && p.Installed is not null)
            .GroupBy(p => (p.Source, p.SourceVersion))
            .Select(g => new OsPendingSource(g.Key.Source, g.First().InstalledSourceVersion, g.Key.SourceVersion, g.Select(p => p.Name).Order().ToList(), g.Any(p => p.Security)))
            .ToList();
        var facts = new OsAdvisoryFacts(
            db.OsFixes.Local.Where(f => f.Distribution == distribution).Select(f => (tracker.Name, f.SourcePackage, f.Cve, f.FixedVersion, f.Urgency, f.Scope, OsAdvisorySourceKind.DebianTracker)).ToList(),
            [], db.OsCveRisks.Local.ToDictionary(r => r.Cve),
            [new OsAdvisoryClaimFact("CVE-2026-84782", true, true, false, "Debian security advisories (DSA)", "This issue is being actively exploited in the wild.",
                "https://lists.debian.org/debian-security-announce/2026/msg00561.html")], []);
        var rated = OsAdvisoryEvaluator.Rate(pending, facts, 0.99, 0.90);
        var rows = new List<OsUpdateFindingEntity>();
        foreach (var finding in rated)
        {
            var row = new OsUpdateFindingEntity
            {
                Id = Guid.NewGuid(), SensorId = sensor.Id, SourcePackage = finding.SourcePackage, Cve = finding.Cve, Packages = string.Join(", ", finding.Packages),
                Installed = finding.Installed, Candidate = finding.Candidate, Rating = finding.Rating, FirstSeenAt = now.AddDays(-2),
                Reasons = JsonSerializer.Serialize(finding.Reasons, OsAdvisoryEvaluator.Json),
            };
            if (proxmox && finding.Rating == OsFindingRating.HighRisk)
            {
                row.AcknowledgedAt = now.AddHours(-6);
                row.AcknowledgedByName = "Philipp Albrecht";
                row.AcknowledgedReason = "The cluster's upgrade waits for the maintenance window on Saturday; DTLS isn't used on pve-01.";
                row.AcknowledgedUntil = now.AddDays(5);
                row.AcknowledgedKinds = finding.Reasons.Where(r => r.High).Select(r => r.Kind).Distinct().ToList();
            }
            db.OsUpdateFindings.Add(row);
            rows.Add(row);
        }
        agent.HighRiskMessage = OsAdvisoryEvaluator.Message(agent.Hostname, rows, now, 0.99);
        return rows.Count;
    }
}
