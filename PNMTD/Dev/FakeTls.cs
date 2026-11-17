using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PNMTD.Dev;

/// <summary>A certificate with its private key, and the key itself, so that a fake CA can sign and a renewal can keep the key.</summary>
public sealed record FakeIdentity(X509Certificate2 Certificate, ECDsa Key)
{
    public string Pem => Certificate.ExportCertificatePem() + "\n";
}

/// <summary>
/// Certificates for the tests, the seed and <c>pnmtd dev fake-integrations</c> (docs/ssl-certificate-plan.md §9, §10): roots,
/// intermediates and leaves with the usual extensions, signed through a signature generator, so that an intermediate may
/// expire before its leaf. The keys go through PKCS#12 once, so that SslStream serves them on macOS as on Linux.
/// </summary>
public static class FakePki
{
    public static FakeIdentity Root(string commonName, string organization = "PNMT Test CA", DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}, O={organization}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddYears(-1), notAfter ?? DateTimeOffset.UtcNow.AddYears(10));
        return new FakeIdentity(Reload(certificate), key);
    }

    public static FakeIdentity Intermediate(FakeIdentity issuer, string commonName, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}, {Organization(issuer)}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer.Certificate, true, false));
        return Sign(request, key, issuer, notBefore ?? DateTimeOffset.UtcNow.AddMonths(-6), notAfter ?? DateTimeOffset.UtcNow.AddYears(5));
    }

    /// <summary>
    /// A server certificate for <paramref name="names"/> (DNS names or addresses), signed by <paramref name="issuer"/>, or
    /// self-signed without one. <paramref name="key"/> keeps a key across renewals.
    /// </summary>
    public static FakeIdentity Leaf(FakeIdentity? issuer, string[] names, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null,
        ECDsa? key = null, string? aiaBase = null, string organization = "PNMT Test")
    {
        key ??= ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={names[0]}, O={organization}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        // A DV certificate's policy (2.23.140.1.2.1), as public CAs issue them.
        request.CertificateExtensions.Add(new X509Extension("2.5.29.32", Policies("2.23.140.1.2.1"), false));
        var nb = notBefore ?? DateTimeOffset.UtcNow.AddDays(-10);
        var na = notAfter ?? DateTimeOffset.UtcNow.AddDays(80);
        if (issuer is null)
        {
            using var self = request.CreateSelfSigned(nb, na);
            return new FakeIdentity(Reload(self), key);
        }
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer.Certificate, true, false));
        if (aiaBase is not null)
        {
            request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension([$"{aiaBase}/ocsp"], [$"{aiaBase}/ca.crt"]));
            request.CertificateExtensions.Add(CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([$"{aiaBase}/ca.crl"]));
        }
        return Sign(request, key, issuer, nb, na);
    }

    private static FakeIdentity Sign(CertificateRequest request, ECDsa key, FakeIdentity issuer, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7f;
        using var signed = request.Create(issuer.Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(issuer.Key), notBefore, notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        return new FakeIdentity(Reload(withKey), key);
    }

    private static X509Certificate2 Reload(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

    private static string Organization(FakeIdentity issuer) =>
        issuer.Certificate.SubjectName.EnumerateRelativeDistinguishedNames()
            .Where(r => !r.HasMultipleElements && r.GetSingleElementType().Value == "2.5.4.10")
            .Select(r => $"O={r.GetSingleElementValue()}").FirstOrDefault() ?? "O=PNMT Test CA";

    private static byte[] Policies(params string[] oids)
    {
        var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach (var oid in oids)
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(oid);
                }
            }
        }
        return writer.Encode();
    }
}

/// <summary>
/// A TLS server that picks its certificate by the SNI (docs/ssl-certificate-plan.md §9): one port, many names. A name gets a
/// leaf and the intermediates it sends; <see cref="Set"/> replaces them, so a test or the fake's HTTP endpoint can rotate one.
/// A connection without SNI, or with an unknown name, gets <see cref="Fallback"/>.
/// </summary>
public sealed class FakeTlsServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<string, SslStreamCertificateContext> sites = new(StringComparer.OrdinalIgnoreCase);
    private Task? accepting;

    public FakeTlsServer(IPAddress address, int port = 0)
    {
        listener = new TcpListener(address, port);
        if (address.Equals(IPAddress.IPv6Any))
        {
            listener.Server.DualMode = true;
        }
    }

    public string? Fallback { get; set; }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>The number of handshakes served, for the tests.</summary>
    public int Handshakes;

    public void Set(string name, FakeIdentity leaf, params FakeIdentity[] intermediates)
    {
        var extra = new X509Certificate2Collection(intermediates.Select(i => i.Certificate).ToArray());
        sites[name] = SslStreamCertificateContext.Create(leaf.Certificate, extra, offline: true);
        Fallback ??= name;
    }

    public FakeTlsServer Start()
    {
        listener.Start();
        accepting = AcceptAsync();
        return this;
    }

    private async Task AcceptAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopping.Token);
            }
            catch (Exception) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync((_, hello, _, _) => ValueTask.FromResult(new SslServerAuthenticationOptions
                {
                    ServerCertificateContext = Site(hello.ServerName),
                }), null, timeout.Token);
                Interlocked.Increment(ref Handshakes);
                var buffer = new byte[256];
                while (await ssl.ReadAsync(buffer, timeout.Token) > 0)
                {
                }
            }
            catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException
                or SocketException or ObjectDisposedException or InvalidOperationException)
            {
            }
        }
    }

    private SslStreamCertificateContext Site(string? name) =>
        name is not null && sites.TryGetValue(name, out var site) ? site
        : Fallback is not null && sites.TryGetValue(Fallback, out var fallback) ? fallback
        : throw new InvalidOperationException("No certificate for this name.");

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        if (accepting is not null)
        {
            await accepting;
        }
        stopping.Dispose();
    }
}

/// <summary>
/// <c>pnmtd dev fake-integrations --tls-port</c> (docs/ssl-certificate-plan.md §9): the e2e tests' TLS endpoints, chosen by
/// SNI. Everything but <c>selfsigned.pnmt.test</c> is signed by one fake private CA (its root at <c>/tls/private-ca.pem</c>);
/// a rotation gives a name a new certificate, keeping its key or signed by another fake CA if asked.
/// </summary>
public sealed class FakeTlsSites
{
    public static readonly string[] Names = ["valid.pnmt.test", "soon.pnmt.test", "expired.pnmt.test", "noint.pnmt.test", "selfsigned.pnmt.test"];

    private readonly FakeTlsServer server;
    private readonly FakeIdentity root = FakePki.Root("PNMT Fake Root", "PNMT Fake CA");
    private readonly FakeIdentity intermediate;
    private readonly FakeIdentity otherRoot = FakePki.Root("PNMT Other Root", "PNMT Other CA");
    private readonly FakeIdentity otherIntermediate;
    private readonly Dictionary<string, FakeIdentity> leaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();

    public FakeTlsSites(int port)
    {
        intermediate = FakePki.Intermediate(root, "PNMT Fake Sub CA");
        otherIntermediate = FakePki.Intermediate(otherRoot, "PNMT Other Sub CA");
        server = new FakeTlsServer(IPAddress.IPv6Any, port);
        Issue("valid.pnmt.test", null, false);
        Issue("soon.pnmt.test", null, false, DateTimeOffset.UtcNow.AddDays(5));
        Issue("expired.pnmt.test", null, false, DateTimeOffset.UtcNow.AddDays(-1));
        Issue("noint.pnmt.test", null, false);
        Issue("selfsigned.pnmt.test", null, false);
        server.Fallback = "valid.pnmt.test";
        server.Start();
    }

    public string PrivateCaPem => root.Pem;

    public bool Rotate(string name, bool keepKey, bool otherIssuer)
    {
        lock (gate)
        {
            if (!leaves.TryGetValue(name, out var old))
            {
                return false;
            }
            Issue(name, keepKey ? old.Key : null, otherIssuer);
            return true;
        }
    }

    private void Issue(string name, ECDsa? key, bool otherIssuer, DateTimeOffset? notAfter = null)
    {
        var selfSigned = name.StartsWith("selfsigned.", StringComparison.Ordinal);
        var sub = otherIssuer ? otherIntermediate : intermediate;
        var expired = notAfter < DateTimeOffset.UtcNow;
        var leaf = FakePki.Leaf(selfSigned ? null : sub, [name], expired ? DateTimeOffset.UtcNow.AddDays(-90) : null,
            notAfter ?? DateTimeOffset.UtcNow.AddDays(80), key, organization: selfSigned ? "PNMT Test Device" : "PNMT Test");
        leaves[name] = leaf;
        if (selfSigned || name.StartsWith("noint.", StringComparison.Ordinal))
        {
            server.Set(name, leaf);
        }
        else
        {
            server.Set(name, leaf, sub);
        }
    }
}
