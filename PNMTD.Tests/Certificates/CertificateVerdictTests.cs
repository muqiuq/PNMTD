using PNMTD.Certificates;
using PNMTD.Lib.Certificates;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Certificates;

/// <summary>The verdict of docs/ssl-certificate-plan.md §5.3, without a network: precedence, the boundaries, the change rules, the key.</summary>
[TestClass]
public sealed class CertificateVerdictTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    private static CertificateRules Rules(CertificateTrust trust = CertificateTrust.Public, bool checkName = true, bool checkExpiry = true,
        bool incomplete = false, int warn = 14, bool certificate = false, bool key = false, bool issuer = true,
        CertificateIssuerMatch match = CertificateIssuerMatch.Organization) =>
        new(trust, checkName, checkExpiry, incomplete, warn, certificate, key, issuer, match, "shop.example.com");

    private static CertificateSeen Seen(string sha = "AA", string spki = "key-1", string organization = "Let's Encrypt", string commonName = "R11",
        int daysLeft = 60, int validFor = 90, bool selfSigned = false, string? issuerDn = null, DateTime? firstSeen = null) =>
        new(Guid.NewGuid(), sha, spki, issuerDn ?? $"CN={commonName}, O={organization}, C=US", organization, commonName, "shop.example.com",
            ["shop.example.com"], Now.AddDays(daysLeft - validFor), Now.AddDays(daysLeft), selfSigned, firstSeen ?? Now.AddDays(-30));

    private static CertificateValidation Valid(DateTime expires, bool trusted = true, bool nameMatches = true, bool complete = true,
        string? expiringSubject = null, params string[] problems) =>
        new(trusted, trusted ? "ISRG Root X1" : null, trusted, nameMatches, complete, false, false, expires, expiringSubject ?? "shop.example.com",
            expiringSubject is null, problems);

    private static CertificateCheckState State(CertificateSeen current, int failures = 0, CertificateValidation? validation = null) =>
        new(failures, failures > 0 ? Now.AddHours(-2) : null, failures > 0 ? "connection refused by shop.example.com:443" : null,
            validation ?? Valid(current.NotAfter));

    private static CertificateVerdictResult Verdict(CertificateRules rules, CertificateSeen current, CertificateSeen? accepted = null,
        Guid? announced = null, int failures = 0, CertificateValidation? validation = null) =>
        CertificateVerdict.Evaluate(rules, State(current, failures, validation), current, accepted ?? current, announced ?? current.Id, Now)!;

    [TestMethod]
    public void OkAndTheWarningBoundary()
    {
        var ok = Verdict(Rules(), Seen(daysLeft: 60));
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, ok.Code);
        Assert.StartsWith("Valid until ", ok.Message);
        Assert.Contains("Let's Encrypt R11", ok.Message);
        Assert.DoesNotContain(" days", ok.Message, "an absolute date, never \"in N days\"");

        Assert.AreEqual(PNMTStatusCodes.SSL_ABOUT_TO_EXPIRE, Verdict(Rules(warn: 14), Seen(daysLeft: 14)).Code, "exactly the warning days");
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, Verdict(Rules(warn: 14), Seen(daysLeft: 15)).Code);
        Assert.Contains("within the 14 days' warning", Verdict(Rules(warn: 14), Seen(daysLeft: 3)).Message);
    }

    [TestMethod]
    public void ExpiredAtExactlyItsEnd()
    {
        var current = Seen(daysLeft: 0);
        Assert.AreEqual(Now, current.NotAfter);
        var expired = Verdict(Rules(), current);
        Assert.AreEqual(PNMTStatusCodes.SSL_EXPIRED, expired.Code);
        Assert.StartsWith("Expired on ", expired.Message);
        var notYet = CertificateVerdict.Evaluate(Rules(), State(current), current, current, current.Id, Now.AddSeconds(-1))!;
        Assert.AreEqual(PNMTStatusCodes.SSL_ABOUT_TO_EXPIRE, notYet.Code);
    }

    [TestMethod]
    public void AnIntermediateThatExpiresFirst()
    {
        var current = Seen(daysLeft: 60);
        var soon = Verdict(Rules(), current, validation: Valid(Now.AddDays(5), expiringSubject: "R11"));
        Assert.AreEqual(PNMTStatusCodes.SSL_ABOUT_TO_EXPIRE, soon.Code);
        Assert.StartsWith("The intermediate R11 expires on", soon.Message);
        var gone = Verdict(Rules(), current, validation: Valid(Now.AddDays(-1), expiringSubject: "R11"));
        Assert.AreEqual(PNMTStatusCodes.SSL_EXPIRED, gone.Code);
        Assert.StartsWith("The intermediate R11 expired on", gone.Message);
    }

    [TestMethod]
    public void CheckExpiryOffIgnoresTime()
    {
        var expired = Seen(daysLeft: -400, validFor: 3650, selfSigned: true);
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, Verdict(Rules(CertificateTrust.Pinned, checkExpiry: false, certificate: true, issuer: false), expired).Code);
        var future = Seen(daysLeft: 100, validFor: 50);
        Assert.IsGreaterThan(Now, future.NotBefore);
        Assert.AreEqual(PNMTStatusCodes.SSL_INVALID, Verdict(Rules(), future).Code, "not valid yet");
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, Verdict(Rules(checkExpiry: false), future).Code);
    }

    [TestMethod]
    public void FailuresInARow()
    {
        var current = Seen();
        var one = CertificateVerdict.Evaluate(Rules(), State(current, failures: 1), current, current, current.Id, Now)!;
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, one.Code, "one failure is no event: the stored certificate's verdict stands");
        var two = Verdict(Rules(), current, failures: 2);
        Assert.AreEqual(PNMTStatusCodes.SSL_UNREACHABLE, two.Code);
        Assert.StartsWith("Unreachable since ", two.Message);
        Assert.Contains("connection refused", two.Message);
        Assert.Contains("the last certificate seen is valid until", two.Message);
        Assert.AreEqual("489", two.Key);
        Assert.IsNull(CertificateVerdict.Evaluate(Rules(), new CertificateCheckState(1, Now, "refused", null), null, null, null, Now), "nothing yet");
        Assert.AreEqual(PNMTStatusCodes.SSL_UNREACHABLE,
            CertificateVerdict.Evaluate(Rules(), new CertificateCheckState(2, Now, "refused", null), null, null, null, Now)!.Code);
    }

    [TestMethod]
    public void TrustAndChecksByMode()
    {
        var current = Seen();
        var untrusted = Valid(current.NotAfter, trusted: false, problems: "the chain ends at a root PNMT doesn't know (self-signed?)");
        var invalid = Verdict(Rules(), current, validation: untrusted);
        Assert.AreEqual(PNMTStatusCodes.SSL_INVALID, invalid.Code);
        Assert.StartsWith("Not trusted: the chain ends at a root PNMT doesn't know", invalid.Message);
        Assert.AreEqual(PNMTStatusCodes.SSL_INVALID, Verdict(Rules(CertificateTrust.Private), current, validation: untrusted).Code);
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, Verdict(Rules(CertificateTrust.Pinned, certificate: true), current, validation: untrusted).Code,
            "the pinned mode has no chain verdict");

        var mismatch = Valid(current.NotAfter, nameMatches: false);
        Assert.AreEqual("Name mismatch: the certificate is for shop.example.com, not shop.example.com",
            Verdict(Rules(), current, validation: mismatch).Message.Split(" · ")[0]);
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, Verdict(Rules(checkName: false), current, validation: mismatch).Code);

        var incomplete = Valid(current.NotAfter, complete: false);
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, Verdict(Rules(), current, validation: incomplete).Code, "a finding only, by default");
        Assert.AreEqual(PNMTStatusCodes.SSL_INVALID, Verdict(Rules(incomplete: true), current, validation: incomplete).Code);
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, Verdict(Rules(CertificateTrust.Pinned, incomplete: true, certificate: true), current, validation: incomplete).Code);
    }

    [TestMethod]
    public void PrecedenceWithTheOthersInTheMessage()
    {
        var accepted = Seen(sha: "AA", organization: "Let's Encrypt");
        var current = Seen(sha: "BB", organization: "ZeroSSL", commonName: "ZeroSSL ECC Domain Secure Site CA", daysLeft: 3);
        var verdict = Verdict(Rules(), current, accepted, validation: Valid(current.NotAfter, trusted: false, problems: "x"));
        Assert.AreEqual(PNMTStatusCodes.SSL_INVALID, verdict.Code, "488 before 487 before 485");
        Assert.Contains("; also changed unexpectedly on", verdict.Message);
        Assert.Contains("issuer Let's Encrypt R11 → ZeroSSL ECC Domain Secure Site CA", verdict.Message);
        Assert.Contains("; also expires on", verdict.Message);

        var expiredAndChanged = Verdict(Rules(), Seen(sha: "BB", organization: "ZeroSSL", daysLeft: -1), accepted);
        Assert.AreEqual(PNMTStatusCodes.SSL_EXPIRED, expiredAndChanged.Code);
        Assert.Contains("also changed unexpectedly", expiredAndChanged.Message);

        var unreachableAndExpired = Verdict(Rules(), Seen(daysLeft: -1), failures: 3);
        Assert.AreEqual(PNMTStatusCodes.SSL_UNREACHABLE, unreachableAndExpired.Code, "without a connection, a renewal can't be ruled out");
    }

    [TestMethod]
    public void EveryCombinationOfTheChangeRules()
    {
        var accepted = Seen(sha: "AA", spki: "key-1", organization: "Let's Encrypt");
        var renewal = Seen(sha: "BB", spki: "key-1", organization: "Let's Encrypt");
        var newKey = Seen(sha: "CC", spki: "key-2", organization: "Let's Encrypt");
        var newIssuer = Seen(sha: "DD", spki: "key-1", organization: "ZeroSSL");
        foreach (var certificate in new[] { false, true })
        {
            foreach (var key in new[] { false, true })
            {
                foreach (var issuer in new[] { false, true })
                {
                    var rules = Rules(certificate: certificate, key: key, issuer: issuer);
                    Assert.AreEqual(certificate, CertificateVerdict.Compare(rules, accepted, renewal).Alarm, $"renewal, {rules}");
                    Assert.AreEqual(certificate || key, CertificateVerdict.Compare(rules, accepted, newKey).Alarm, $"new key, {rules}");
                    Assert.AreEqual(certificate || issuer, CertificateVerdict.Compare(rules, accepted, newIssuer).Alarm, $"new issuer, {rules}");
                }
            }
        }
        // Key and issuer: the useful middle. A renewal with its key and CA passes; a new key fails.
        Assert.AreEqual(PNMTStatusCodes.SSL_CHANGED, Verdict(Rules(key: true, issuer: true), renewal, accepted, announced: accepted.Id).Code);
        var failed = Verdict(Rules(key: true, issuer: true), newKey, accepted, announced: accepted.Id);
        Assert.AreEqual(PNMTStatusCodes.SSL_CHANGED_UNEXPECTEDLY, failed.Code);
        Assert.Contains("new key", failed.Message);
        Assert.Contains("Accept the new certificate on the sensor's page", failed.Message);
        Assert.AreEqual($"487|{newKey.Id}|{accepted.Id}", failed.Key);
    }

    [TestMethod]
    public void TheIssuerByOrganizationOrExactly()
    {
        var r10 = Seen(sha: "AA", commonName: "R10");
        var r11 = Seen(sha: "BB", commonName: "R11");
        Assert.IsFalse(CertificateVerdict.Compare(Rules(), r10, r11).Alarm, "Let's Encrypt's intermediates rotate");
        Assert.IsTrue(CertificateVerdict.Compare(Rules(match: CertificateIssuerMatch.Exact), r10, r11).Alarm);

        // No O=: the full name.
        var a = Seen(sha: "AA", organization: null!, issuerDn: "CN=corp-ca-01");
        var b = Seen(sha: "BB", organization: null!, issuerDn: "CN=corp-ca-02");
        Assert.IsTrue(CertificateVerdict.Compare(Rules(), a with { IssuerOrganization = null }, b with { IssuerOrganization = null }).Issuer);
        // The pinned mode has no issuer rule.
        Assert.IsFalse(CertificateVerdict.Compare(Rules(CertificateTrust.Pinned, issuer: true, key: true),
            Seen(sha: "AA", organization: "X"), Seen(sha: "BB", organization: "Y")).Alarm);
    }

    [TestMethod]
    public void AChangeThatDoesntAlarmMovesTheBaselineAndSaysSoOnce()
    {
        var accepted = Seen(sha: "AA", commonName: "R10");
        var renewal = Seen(sha: "BB", commonName: "R11", daysLeft: 89);
        var first = Verdict(Rules(), renewal, accepted, announced: accepted.Id);
        Assert.AreEqual(PNMTStatusCodes.SSL_CHANGED, first.Code);
        Assert.IsTrue(first.MoveBaseline);
        Assert.StartsWith("New certificate: valid until", first.Message);
        // After the event: accepted = announced = current. The key stays, so nothing more is written.
        var then = Verdict(Rules(), renewal, renewal, announced: renewal.Id);
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, then.Code);
        Assert.AreEqual(first.Key, then.Key, "the step from 286 to 285 writes nothing");
        Assert.IsFalse(then.MoveBaseline);
    }

    [TestMethod]
    public void TheAcceptedCertificateReturning()
    {
        var accepted = Seen(sha: "AA");
        var intruder = Seen(sha: "BB", organization: "Evil Proxy");
        Assert.AreEqual(PNMTStatusCodes.SSL_CHANGED_UNEXPECTEDLY, Verdict(Rules(), intruder, accepted, announced: accepted.Id).Code);
        var back = Verdict(Rules(), accepted, accepted, announced: intruder.Id);
        Assert.AreEqual(PNMTStatusCodes.SSL_CHANGED, back.Code, "the accepted certificate is back: an OK event of its own");
    }

    [TestMethod]
    public void AnAfterAcceptOk()
    {
        // 487 announced the new certificate; once accepted, the verdict is 285 with a key of its own.
        var accepted = Seen(sha: "AA");
        var current = Seen(sha: "BB", organization: "ZeroSSL");
        var changed = Verdict(Rules(), current, accepted, announced: current.Id);
        var ok = Verdict(Rules(), current, current, announced: current.Id);
        Assert.AreEqual(PNMTStatusCodes.SSL_OK, ok.Code);
        Assert.AreNotEqual(changed.Key, ok.Key);
    }

    [TestMethod]
    [DataRow("Let's Encrypt", "R11", "Let's Encrypt R11")]
    [DataRow("DigiCert Inc", "DigiCert Global G2 TLS RSA SHA256 2020 CA1", "DigiCert Global G2 TLS RSA SHA256 2020 CA1")]
    [DataRow(null, "corp-ca", "corp-ca")]
    [DataRow("ACME Corp", null, "ACME Corp")]
    public void IssuerLabels(string? organization, string? commonName, string label) =>
        Assert.AreEqual(label, CertificateVerdict.IssuerLabel(organization, commonName, "CN=x", false));
}
