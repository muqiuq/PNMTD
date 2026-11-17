using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PNMTD.Dev;
using PNMTD.Lib.Certificates;

namespace PNMTD.Tests.Certificates;

/// <summary>
/// The probe against a loopback TLS server (docs/ssl-certificate-plan.md §5.1, §10): what it reads, how it validates under
/// each trust mode, how it fails, and that it never fetches a URL a certificate names.
/// </summary>
[TestClass]
public sealed class CertificateProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static FakeIdentity root = null!;
    private static FakeIdentity intermediate = null!;
    private static FakeIdentity otherRoot = null!;
    private static FakeTlsServer server = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        root = FakePki.Root("Probe Root");
        intermediate = FakePki.Intermediate(root, "Probe Sub CA");
        otherRoot = FakePki.Root("Other Root", "Other CA");
        server = new FakeTlsServer(IPAddress.Loopback);
        server.Set("valid.test", FakePki.Leaf(intermediate, ["valid.test", "www.valid.test"]), intermediate);
        server.Set("noint.test", FakePki.Leaf(intermediate, ["noint.test"]));
        server.Set("expired.test", FakePki.Leaf(intermediate, ["expired.test"], DateTimeOffset.UtcNow.AddDays(-100), DateTimeOffset.UtcNow.AddDays(-1)), intermediate);
        server.Set("future.test", FakePki.Leaf(intermediate, ["future.test"], DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(90)), intermediate);
        server.Set("selfsigned.test", FakePki.Leaf(null, ["selfsigned.test"]));
        server.Set("wrongname.test", FakePki.Leaf(intermediate, ["something-else.test"]), intermediate);
        server.Set("127.0.0.1", FakePki.Leaf(intermediate, ["127.0.0.1"]), intermediate);
        server.Fallback = "127.0.0.1";
        server.Start();
    }

    [ClassCleanup]
    public static async Task Stop() => await server.DisposeAsync();

    private static Task<CertificateProbeResult> ReadAsync(string name, CertificateTrustInput? trust = null, int? port = null) =>
        CertificateProbe.ReadAsync(new TlsEndpoint("127.0.0.1", port ?? server.Port), name, null, trust ?? CertificateTrustInput.Public, Timeout, CancellationToken.None);

    private static CertificateTrustInput Private(params FakeIdentity[] anchors) =>
        new(CertificateTrust.Private, anchors.Select(a => a.Certificate).ToList(), [],
            anchors.ToDictionary(a => CertificateParser.Sha256(a.Certificate), a => a.Certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)));

    /// <summary>§5.1's check: the callback's ExtraStore holds exactly what the server sent, the leaf left out.</summary>
    [TestMethod]
    public async Task TheChainAsSentIsWhatTheServerSends()
    {
        var with = (await ReadAsync("valid.test")).Read!;
        Assert.HasCount(1, with.Sent, "the intermediate");
        Assert.AreEqual(CertificateParser.Sha256(intermediate.Certificate), CertificateParser.Sha256(with.Sent[0]));
        var without = (await ReadAsync("noint.test")).Read!;
        Assert.IsEmpty(without.Sent, "only the leaf");
    }

    [TestMethod]
    public async Task ReadsTheFacts()
    {
        var result = await ReadAsync("valid.test");
        Assert.IsTrue(result.Success, result.Error);
        var read = result.Read!;
        var facts = read.Facts;
        CollectionAssert.AreEqual(new[] { "valid.test", "www.valid.test" }, facts.Names.ToArray());
        Assert.AreEqual("valid.test", facts.Subject.CommonName);
        Assert.AreEqual("PNMT Test", facts.Subject.Organization);
        Assert.AreEqual("Probe Sub CA", facts.Issuer.CommonName);
        Assert.AreEqual("PNMT Test CA", facts.Issuer.Organization);
        Assert.AreEqual(new CertificateKey("ECDSA", 256, "P-256"), facts.Key);
        Assert.AreEqual("ECDSA with SHA-256", facts.SignatureAlgorithm);
        Assert.AreEqual("DV", facts.ValidationLevel);
        CollectionAssert.Contains(facts.ExtendedKeyUsage.ToArray(), "TLS server");
        CollectionAssert.Contains(facts.KeyUsage.ToArray(), "Digital signature");
        Assert.IsFalse(facts.SelfSigned);
        Assert.IsFalse(facts.IsCa);
        Assert.IsNotNull(facts.AuthorityKeyId);
        Assert.IsNotNull(facts.SubjectKeyId);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(read.Leaf)), facts.Sha256);
        Assert.AreEqual(Convert.ToHexString(SHA1.HashData(read.Leaf)), facts.Sha1);
        Assert.IsGreaterThanOrEqualTo(6, facts.Extensions.Count, "every extension, with its text");
        Assert.IsTrue(facts.Extensions.All(e => e.Value.Length > 0));
        Assert.AreEqual("valid.test", read.Connection.ServerName);
        Assert.AreEqual("127.0.0.1", read.Connection.Address);
        Assert.StartsWith("TLS 1.", read.Connection.Protocol);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", read.ChainPem);
        Assert.AreEqual(2, Regex.Count(read.ChainPem, "BEGIN CERTIFICATE"), "the leaf and the intermediate");
    }

    [TestMethod]
    public async Task TrustByMode()
    {
        // The test CA isn't public: untrusted in the public mode, naming the CA the chain stops at. The server sends what it
        // should (everything but the root), so the chain is complete.
        var @public = (await ReadAsync("valid.test")).Read!.Validation;
        Assert.IsFalse(@public.Trusted);
        Assert.IsFalse(@public.PubliclyTrusted);
        Assert.AreEqual("the chain ends at Probe Root, a CA PNMT doesn't trust (a private CA?)", string.Join(";", @public.Problems));
        Assert.IsTrue(@public.Complete);
        Assert.IsTrue(@public.NameMatches);
        Assert.IsFalse((await ReadAsync("noint.test")).Read!.Validation.Complete, "the leaf alone");

        var @private = (await ReadAsync("valid.test", Private(root))).Read!;
        Assert.IsTrue(@private.Validation.Trusted, string.Join(";", @private.Validation.Problems));
        Assert.AreEqual("Probe Root", @private.Validation.TrustedBy);
        Assert.IsTrue(@private.Validation.Complete);
        Assert.HasCount(3, @private.Chain);
        Assert.AreEqual(CertificateSource.Server, @private.Chain[1].Source);
        Assert.AreEqual(CertificateSource.PrivateCa, @private.Chain[2].Source);
        Assert.IsTrue(@private.Chain[2].TrustAnchor);

        var other = (await ReadAsync("valid.test", Private(otherRoot))).Read!.Validation;
        Assert.IsFalse(other.Trusted);
        Assert.AreEqual("signed by Probe Root, not by the sensor's private CA", string.Join(";", other.Problems));

        var pinned = (await ReadAsync("selfsigned.test", CertificateTrustInput.Pinned)).Read!;
        Assert.IsFalse(pinned.Validation.Trusted, "the pinned mode has no chain verdict");
        Assert.IsEmpty(pinned.Validation.Problems);
        Assert.IsTrue(pinned.Validation.SelfSigned);
        Assert.IsTrue(pinned.Facts.SelfSigned);
    }

    [TestMethod]
    public async Task AnIntermediateOnlyThePrivateCaHasIsTrustedButIncomplete()
    {
        var trust = new CertificateTrustInput(CertificateTrust.Private, [root.Certificate], [intermediate.Certificate],
            new Dictionary<string, string> { [CertificateParser.Sha256(root.Certificate)] = "Probe CA", [CertificateParser.Sha256(intermediate.Certificate)] = "Probe CA" });
        var read = (await ReadAsync("noint.test", trust)).Read!;
        Assert.IsTrue(read.Validation.Trusted, string.Join(";", read.Validation.Problems));
        Assert.IsFalse(read.Validation.Complete);
        Assert.AreEqual(CertificateSource.PrivateCa, read.Chain[1].Source);
        Assert.AreEqual("Probe CA", read.Chain[1].SourceName);

        var withoutIntermediate = (await ReadAsync("noint.test", Private(root))).Read!.Validation;
        Assert.IsFalse(withoutIntermediate.Trusted);
        Assert.IsFalse(withoutIntermediate.Complete);
        Assert.Contains("doesn't send its intermediate", string.Join(";", withoutIntermediate.Problems));
    }

    [TestMethod]
    public async Task NameAndTimes()
    {
        var wrong = (await ReadAsync("wrongname.test", Private(root))).Read!.Validation;
        Assert.IsFalse(wrong.NameMatches);
        Assert.IsTrue(wrong.Trusted, "the chain is fine; the name is the sensor's check");

        // Time validity is the verdict's, not the chain's: an expired certificate stays trusted.
        var expired = (await ReadAsync("expired.test", Private(root))).Read!;
        Assert.IsTrue(expired.Validation.Trusted, string.Join(";", expired.Validation.Problems));
        Assert.IsLessThan(DateTime.UtcNow, expired.Facts.NotAfter);
        Assert.IsTrue(expired.Validation.ChainExpiresIsLeaf);

        var future = (await ReadAsync("future.test", Private(root))).Read!;
        Assert.IsGreaterThan(DateTime.UtcNow, future.Facts.NotBefore);
    }

    [TestMethod]
    public async Task AnIntermediateThatExpiresFirstIsTheChainsExpiry()
    {
        var shortSub = FakePki.Intermediate(root, "Short Sub CA", notAfter: DateTimeOffset.UtcNow.AddDays(20));
        await using var local = new FakeTlsServer(IPAddress.Loopback);
        local.Set("short.test", FakePki.Leaf(shortSub, ["short.test"], notAfter: DateTimeOffset.UtcNow.AddDays(80)), shortSub);
        local.Start();
        var read = (await ReadAsync("short.test", Private(root), local.Port)).Read!;
        Assert.IsFalse(read.Validation.ChainExpiresIsLeaf);
        Assert.AreEqual("Short Sub CA", read.Validation.ChainExpiresSubject);
        Assert.IsLessThan(read.Facts.NotAfter.AddDays(-50), read.Validation.ChainExpiresAt);
    }

    [TestMethod]
    public async Task AnAddressEndpointMatchesItsIpName()
    {
        var result = await CertificateProbe.ReadAsync(new TlsEndpoint("127.0.0.1", server.Port), null, null, Private(root), Timeout, CancellationToken.None);
        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNull(result.Read!.Connection.ServerName, "no SNI for an address");
        Assert.IsTrue(result.Read.Validation.NameMatches);
        CollectionAssert.Contains(result.Read.Facts.Names.ToArray(), "127.0.0.1");
    }

    [TestMethod]
    public async Task IPv6Loopback()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Inconclusive("No IPv6 here.");
        }
        await using var v6 = new FakeTlsServer(IPAddress.IPv6Loopback);
        v6.Set("v6.test", FakePki.Leaf(intermediate, ["v6.test", "::1"]), intermediate);
        try
        {
            v6.Start();
        }
        catch (SocketException)
        {
            Assert.Inconclusive("No IPv6 loopback here.");
        }
        Assert.IsTrue(TlsEndpoint.TryParse($"[::1]:{v6.Port}", out var endpoint, out _));
        var result = await CertificateProbe.ReadAsync(endpoint, "v6.test", null, Private(root), Timeout, CancellationToken.None);
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("::1", result.Read!.Connection.Address);
    }

    [TestMethod]
    public async Task Failures()
    {
        // A port nothing listens on.
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var refused = await ReadAsync("valid.test", port: port);
        Assert.AreEqual(CertificateFailureKind.Refused, refused.Failure, refused.Error);

        // Plain HTTP.
        using var http = new TcpListener(IPAddress.Loopback, 0);
        http.Start();
        _ = Task.Run(async () =>
        {
            using var client = await http.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            await Task.Delay(200);
        });
        var notTls = await ReadAsync("valid.test", port: ((IPEndPoint)http.LocalEndpoint).Port);
        Assert.AreEqual(CertificateFailureKind.NotTls, notTls.Failure, notTls.Error);
        Assert.Contains("plain HTTP", notTls.Error!);

        // Accepts, then stays silent.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        TcpClient? held = null;
        _ = Task.Run(async () => held = await silent.AcceptTcpClientAsync());
        var timeout = await CertificateProbe.ReadAsync(new TlsEndpoint("127.0.0.1", ((IPEndPoint)silent.LocalEndpoint).Port), "valid.test", null,
            CertificateTrustInput.Public, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.AreEqual(CertificateFailureKind.Timeout, timeout.Failure, timeout.Error);
        held?.Dispose();

        var noName = await CertificateProbe.ReadAsync(new TlsEndpoint("no-such-host.invalid", 443), null, null, CertificateTrustInput.Public,
            Timeout, CancellationToken.None);
        Assert.AreEqual(CertificateFailureKind.NoSuchName, noName.Failure, noName.Error);
    }

    /// <summary>
    /// §5.1: a certificate's AIA, OCSP and CRL URLs are never fetched, in the handshake or in PNMTD's own chains. On Linux,
    /// where PNMT runs: .NET reads the peer's chain from OpenSSL and builds with the probe's policy. On macOS the system's TLS
    /// stack completes the peer's chain itself during the handshake (Apple's trust evaluation, before any policy of
    /// PNMTD's applies), so there the handshake's part is inconclusive; PNMTD's own chains are checked everywhere.
    /// </summary>
    [TestMethod]
    public async Task NoUrlOfTheCertificateIsFetched()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var requests = 0;
        var aia = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                Interlocked.Increment(ref requests);
            }
        });
        await using var local = new FakeTlsServer(IPAddress.Loopback);
        // Without its intermediate, so that a chain builder would want to fetch it from the CA-issuer URL.
        // PNMTD's own chains, from the bytes alone, in every mode.
        var leaf = FakePki.Leaf(intermediate, ["aia.test"], aiaBase: aia);
        foreach (var trust in new[] { CertificateTrustInput.Public, Private(root), CertificateTrustInput.Pinned })
        {
            CertificateProbe.Evaluate(leaf.Certificate.RawData, [], false, trust, true, new CertificateConnection("127.0.0.1", 443, "aia.test", "TLS 1.3", "", 0));
        }
        await Task.Delay(300);
        Assert.AreEqual(0, requests, "PNMTD's own chain building fetched a URL of the certificate");

        local.Set("aia.test", leaf);
        local.Start();
        var read = (await ReadAsync("aia.test", Private(root), local.Port)).Read!;
        await ReadAsync("aia.test", null, local.Port);
        await Task.Delay(300);
        if (OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive($"macOS's TLS stack fetched {requests} time(s) during the handshake; PNMT runs on Linux, where this test asserts none.");
        }
        Assert.AreEqual(0, requests);
        Assert.IsFalse(read.Validation.Complete);
        CollectionAssert.Contains(read.Facts.OcspUrls.ToArray(), $"{aia}/ocsp");
        CollectionAssert.Contains(read.Facts.CaIssuerUrls.ToArray(), $"{aia}/ca.crt");
        CollectionAssert.Contains(read.Facts.CrlUrls.ToArray(), $"{aia}/ca.crl");
    }

    [TestMethod]
    public void SctsAndMustStaple()
    {
        // An SCT list of two entries (lengths 3 and 2) in its OCTET STRING, and a TLS feature of status_request (5).
        byte[] list = [0x00, 0x09, 0x00, 0x03, 1, 2, 3, 0x00, 0x02, 4, 5];
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        writer.WriteOctetString(list);
        Assert.AreEqual(2, CertificateParser.SctCount(writer.Encode()));
        var features = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (features.PushSequence())
        {
            features.WriteInteger(5);
        }
        CollectionAssert.AreEqual(new long[] { 5 }, CertificateParser.TlsFeatures(features.Encode()));
    }

    [TestMethod]
    [DataRow("example.com", "example.com:443")]
    [DataRow("Example.COM:8443", "example.com:8443")]
    [DataRow("https://shop.example.com/cart?x=1", "shop.example.com:443")]
    [DataRow("https://user@shop.example.com:9443/", "shop.example.com:9443")]
    [DataRow("ldaps://dc01.corp.local", "dc01.corp.local:636")]
    [DataRow("10.0.0.5:443", "10.0.0.5:443")]
    [DataRow("[2001:db8::1]:8443", "[2001:db8::1]:8443")]
    [DataRow("2001:db8::1", "[2001:db8::1]:443")]
    [DataRow("bücher.example", "bücher.example:443")]
    [DataRow("internal_host.lan.", "internal_host.lan:443")]
    public void Endpoints(string text, string normalized)
    {
        Assert.IsTrue(TlsEndpoint.TryParse(text, out var endpoint, out var error), error);
        Assert.AreEqual(normalized, endpoint.Normalized);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("http://example.com")]
    [DataRow("example.com:0")]
    [DataRow("example.com:70000")]
    [DataRow("exa mple.com")]
    [DataRow("*.example.com")]
    [DataRow("[2001:db8::1")]
    [DataRow("123")]
    [DataRow("-bad.example.com")]
    public void BadEndpoints(string text) => Assert.IsFalse(TlsEndpoint.TryParse(text, out _, out _));

    [TestMethod]
    public void InternationalNamesGoOutAsPunycode() => Assert.AreEqual("xn--bcher-kva.example", TlsEndpoint.Ascii("bücher.example"));
}
