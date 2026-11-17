using PNMTD.Certificates;
using PNMTD.Data;
using PNMTD.Dev;
using PNMTD.Lib.Certificates;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Cli;

/// <summary>
/// SSL certificate sensors for the demo (docs/ssl-certificate-plan.md §9): a private CA <i>PNMT demo root</i>, and one sensor
/// in each state, with certificates the seed makes and the facts, chains and validations the probe would store, so their
/// tabs have content without a network. They point at <c>*.pnmt.test</c> and are next checked in 30 days; Check now makes
/// them unreachable. The failing ones are ignored, so the demo's problems stay the six the dashboard's targets count.
/// </summary>
internal static class DevSeedCertificates
{
    public static int Seed(PnmtdDbContext db, Dictionary<string, Guid> hosts, DateTime now)
    {
        var demoRoot = FakePki.Root("PNMT demo root", "PNMT Demo", DateTimeOffset.UtcNow.AddYears(-3), DateTimeOffset.UtcNow.AddYears(12));
        var demoSub = FakePki.Intermediate(demoRoot, "PNMT demo issuing CA");
        var publicRoot = FakePki.Root("PNMT Demo Public Root", "Let's Encrypt");
        var publicSub = FakePki.Intermediate(publicRoot, "R11");
        var otherRoot = FakePki.Root("PNMT Other Public Root", "ZeroSSL");
        var otherSub = FakePki.Intermediate(otherRoot, "ZeroSSL ECC Domain Secure Site CA");

        var authority = new CertificateAuthorityEntity
        {
            Id = Guid.NewGuid(), Name = "PNMT demo root", Pem = demoRoot.Pem + demoSub.Pem, RootSha256 = CertificateParser.Sha256(demoRoot.Certificate),
            RootSubject = demoRoot.Certificate.SubjectName.Name, RootNotBefore = demoRoot.Certificate.NotBefore.ToUniversalTime(),
            RootNotAfter = demoRoot.Certificate.NotAfter.ToUniversalTime(), IntermediateCount = 1, CreatedAt = now.AddDays(-40), CreatedBy = "admin",
        };
        db.CertificateAuthorities.Add(authority);
        var privateTrust = CertificateAuthorityService.TrustFor(CertificateTrust.Private, [authority]);

        var count = 0;
        void Add(string host, string name, int port, CertificateTrust trust, FakeIdentity leaf, FakeIdentity? sub, bool publiclyTrusted,
            FakeIdentity? previous = null, FakeIdentity? previousSub = null, bool unreachable = false)
        {
            if (!hosts.TryGetValue(host, out var hostId))
            {
                return;
            }
            var sensor = new SensorEntity
            {
                Id = Guid.NewGuid(), ParentId = hostId, Type = SensorType.SSL_CERTIFICATE, Name = $"{name}:{port}", Parameters = $"{name}:{port}",
                Enabled = true, Interval = CertificateCheckService.DefaultIntervalSeconds, Created = now.AddDays(-60),
            };
            sensor.SetNewSecretToken();
            db.Sensors.Add(sensor);
            var defaults = CertificateSensorService.Defaults(trust);
            var row = new CertificateSensorEntity
            {
                SensorId = sensor.Id, Trust = trust, CheckName = defaults.CheckName, CheckExpiry = defaults.CheckExpiry,
                IncompleteChainFails = defaults.IncompleteChainFails, AlertOnCertificateChange = defaults.Certificate, AlertOnKeyChange = defaults.Key,
                AlertOnIssuerChange = defaults.Issuer, WarnDays = 14, Created = now.AddDays(-60), NextCheckAt = now.AddDays(30),
            };
            if (trust == CertificateTrust.Private)
            {
                row.Authorities.Add(new CertificateSensorAuthorityEntity { SensorId = sensor.Id, AuthorityId = authority.Id });
            }
            db.CertificateSensors.Add(row);
            CertificateVersionEntity Version(FakeIdentity certificate, FakeIdentity? issuer, DateTime seen)
            {
                var input = trust == CertificateTrust.Private ? privateTrust : trust == CertificateTrust.Pinned ? CertificateTrustInput.Pinned : CertificateTrustInput.Public;
                var read = CertificateProbe.Evaluate(certificate.Certificate.RawData, issuer is null ? [] : [issuer.Certificate.RawData], false, input, true,
                    new CertificateConnection("192.0.2.10", port, name, "TLS 1.3", "TLS_AES_256_GCM_SHA384", 38));
                var validation = read.Validation;
                var chain = read.Chain;
                if (publiclyTrusted)
                {
                    // A demo stands in for a public CA: what a check against Debian's roots would have found.
                    var anchor = (issuer == otherSub ? otherRoot : publicRoot).Certificate;
                    var name_ = CertificateParser.Name(anchor.SubjectName);
                    validation = validation with { Trusted = true, PubliclyTrusted = true, TrustedBy = name_.Short, Complete = true, Problems = [] };
                    chain = [.. chain, new CertificateChainEntry(chain.Count, name_, name_, anchor.NotBefore.ToUniversalTime(), anchor.NotAfter.ToUniversalTime(),
                        CertificateParser.Sha256(anchor), CertificateParser.Key(anchor).Label, CertificateSource.PublicRoot, null, true, true)];
                }
                var facts = read.Facts;
                var version = new CertificateVersionEntity
                {
                    Id = Guid.NewGuid(), SensorId = sensor.Id, Sha256 = facts.Sha256, SpkiSha256 = facts.SpkiSha256, Subject = facts.Subject.Dn,
                    SubjectCommonName = facts.Subject.CommonName, Issuer = facts.Issuer.Dn, IssuerOrganization = facts.Issuer.Organization,
                    IssuerCommonName = facts.Issuer.CommonName, Serial = facts.Serial, NotBefore = facts.NotBefore, NotAfter = facts.NotAfter,
                    Names = facts.Names.ToList(), SelfSigned = facts.SelfSigned, Facts = CertificateJson.Write(facts), Chain = CertificateJson.Write(chain),
                    ChainPem = read.ChainPem, FirstSeenAt = seen, LastSeenAt = unreachable ? now.AddHours(-6) : now.AddHours(-3),
                };
                db.CertificateVersions.Add(version);
                row.Connection = CertificateJson.Write(read.Connection);
                row.Validation = CertificateJson.Write(validation);
                return version;
            }
            var accepted = previous is null ? null : Version(previous, previousSub, now.AddDays(-58));
            if (accepted is not null)
            {
                accepted.LastSeenAt = now.AddDays(-1);
            }
            var current = Version(leaf, sub, previous is null ? now.AddDays(-58) : now.AddHours(-20));
            row.CurrentVersionId = current.Id;
            row.AcceptedVersionId = (accepted ?? current).Id;
            row.AcceptedAt = now.AddDays(-58);
            row.AcceptedBy = CertificateCheckService.FirstCheck;
            row.LastCheckAt = unreachable ? now.AddMinutes(-20) : now.AddHours(-3);
            row.LastSuccessAt = unreachable ? now.AddHours(-6) : now.AddHours(-3);
            if (unreachable)
            {
                row.ConsecutiveFailures = 3;
                row.FirstFailureAt = now.AddHours(-5);
                row.LastError = $"connection refused by {name}:{port}";
            }
            var verdict = CertificateVerdict.Evaluate(CertificateRules.From(row, name),
                new CertificateCheckState(row.ConsecutiveFailures, row.FirstFailureAt, row.LastError, CertificateJson.Read<CertificateValidation>(row.Validation)),
                CertificateSeen.From(current), CertificateSeen.From(accepted ?? current), current.Id, now);
            if (verdict is not null)
            {
                db.Events.Add(new EventEntity
                {
                    Id = Guid.NewGuid(), SensorId = sensor.Id, Code = verdict.Code, Message = verdict.Message, Source = CertificateCheckService.Source,
                    Created = unreachable ? now.AddHours(-4) : previous is not null ? now.AddHours(-20) : now.AddDays(-2),
                });
                row.LastCode = verdict.Code;
                row.LastVerdictKey = verdict.Key;
                row.AnnouncedVersionId = current.Id;
                if (verdict.Code >= 400)
                {
                    sensor.Ignore = true;
                    sensor.Notes = "Seeded as ignored, so that the demo's problems stay the six the dashboard counts (docs/ssl-certificate-plan.md §9).";
                }
            }
            count++;
        }

        Add("proxy-01", "shop.pnmt.test", 443, CertificateTrust.Public, FakePki.Leaf(publicSub, ["shop.pnmt.test", "www.shop.pnmt.test"]), publicSub, true);
        Add("wiki", "wiki.pnmt.test", 443, CertificateTrust.Private, FakePki.Leaf(demoSub, ["wiki.pnmt.test"], organization: "PNMT Demo"), demoSub, false);
        Add("printer-hub", "printer.pnmt.test", 443, CertificateTrust.Pinned,
            FakePki.Leaf(null, ["printer.pnmt.test"], DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(8), organization: "Printer Hub"), null, false);
        Add("mail", "mail.pnmt.test", 993, CertificateTrust.Public,
            FakePki.Leaf(publicSub, ["mail.pnmt.test"], DateTimeOffset.UtcNow.AddDays(-84), DateTimeOffset.UtcNow.AddDays(6)), publicSub, true);
        Add("proxy-02", "legacy.pnmt.test", 443, CertificateTrust.Public,
            FakePki.Leaf(publicSub, ["legacy.pnmt.test"], DateTimeOffset.UtcNow.AddDays(-93), DateTimeOffset.UtcNow.AddDays(-3)), publicSub, true);
        Add("vpn", "vpn.pnmt.test", 443, CertificateTrust.Public, FakePki.Leaf(otherSub, ["vpn.pnmt.test", "vpn2.pnmt.test"]), otherSub, true,
            previous: FakePki.Leaf(publicSub, ["vpn.pnmt.test"], DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(30)), previousSub: publicSub);
        Add("backup-srv", "backup.pnmt.test", 443, CertificateTrust.Public, FakePki.Leaf(publicSub, ["backup.pnmt.test"]), publicSub, true, unreachable: true);
        db.SaveChanges();
        return count;
    }
}
