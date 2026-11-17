using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PNMTD.Lib.Helper;

namespace PNMTD.Lib.Certificates;

/// <summary>Why a check read no certificate (docs/ssl-certificate-plan.md §5.1).</summary>
public enum CertificateFailureKind { NoSuchName, Refused, Timeout, Unreachable, HandshakeFailed, NotTls, NoCertificate }

/// <summary>
/// What the sensor trusts: the public CAs, private CAs' roots (anchors) and intermediates, or nothing but the pin.
/// <see cref="Names"/> names the private CA of each anchor and intermediate, by SHA-256.
/// </summary>
public sealed record CertificateTrustInput(CertificateTrust Mode, IReadOnlyList<X509Certificate2> Anchors,
    IReadOnlyList<X509Certificate2> Intermediates, IReadOnlyDictionary<string, string> Names)
{
    public static readonly CertificateTrustInput Public = new(CertificateTrust.Public, [], [], new Dictionary<string, string>());

    public static readonly CertificateTrustInput Pinned = new(CertificateTrust.Pinned, [], [], new Dictionary<string, string>());
}

/// <summary>How the connection went: the address that answered, the SNI sent, the protocol and cipher, the handshake's time.</summary>
public sealed record CertificateConnection(string Address, int Port, string? ServerName, string Protocol, string CipherSuite, int HandshakeMs);

/// <summary>
/// What PNMTD made of the chain under the sensor's trust (§5.1): trusted (and by what), also by public CAs, the name, whether
/// the server sends its intermediates, whether it sends the root, and the chain's expiry with the certificate it comes from.
/// </summary>
public sealed record CertificateValidation(
    bool Trusted,
    string? TrustedBy,
    bool PubliclyTrusted,
    bool NameMatches,
    bool Complete,
    bool SendsRoot,
    bool SelfSigned,
    DateTime ChainExpiresAt,
    string ChainExpiresSubject,
    bool ChainExpiresIsLeaf,
    IReadOnlyList<string> Problems);

/// <summary>A successful read: the leaf and the certificates the server sent, parsed, with the built chain and the validation.</summary>
public sealed record CertificateRead(byte[] Leaf, IReadOnlyList<byte[]> Sent, CertificateFacts Facts, IReadOnlyList<CertificateChainEntry> Chain,
    CertificateConnection Connection, CertificateValidation Validation, string ChainPem, bool ChainTruncated);

public sealed record CertificateProbeResult(CertificateRead? Read, CertificateFailureKind? Failure, string? Error)
{
    public bool Success => Read is not null;

    public static CertificateProbeResult Failed(CertificateFailureKind kind, string error) => new(null, kind, error);
}

/// <summary>
/// Reads the certificate of a TLS endpoint (docs/ssl-certificate-plan.md §5.1): connects, completes the handshake whatever
/// the certificate, and validates the chain itself under the sensor's trust. <b>It never fetches a URL a server names:</b>
/// AIA, OCSP and CRL downloads are off in the handshake's chain policy and in every chain PNMTD builds.
/// </summary>
public static class CertificateProbe
{
    /// <summary>At most this many certificates of a chain are kept (§4.4).</summary>
    public const int MaxChain = 10;

    public const int MaxPem = 64 * 1024;

    public static async Task<CertificateProbeResult> ReadAsync(TlsEndpoint endpoint, string? serverName, string? connectTo,
        CertificateTrustInput trust, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        var sni = serverName ?? (endpoint.IsAddress ? null : endpoint.Host);
        var target = TlsEndpoint.Ascii(sni ?? endpoint.Host);
        var connectHost = TlsEndpoint.Ascii(connectTo ?? endpoint.Host);

        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(connectHost, endpoint.Port, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CertificateProbeResult.Failed(CertificateFailureKind.Timeout, $"timeout: no connection to {connectHost}:{endpoint.Port} within {timeout.TotalSeconds:0} s");
        }
        catch (SocketException e)
        {
            return e.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                    CertificateProbeResult.Failed(CertificateFailureKind.NoSuchName, $"no such name: {connectHost}"),
                SocketError.ConnectionRefused =>
                    CertificateProbeResult.Failed(CertificateFailureKind.Refused, $"connection refused by {connectHost}:{endpoint.Port}"),
                SocketError.TimedOut =>
                    CertificateProbeResult.Failed(CertificateFailureKind.Timeout, $"timeout: no connection to {connectHost}:{endpoint.Port}"),
                _ => CertificateProbeResult.Failed(CertificateFailureKind.Unreachable, $"{connectHost}:{endpoint.Port} unreachable: {e.Message}"),
            };
        }

        var remote = tcp.Client.RemoteEndPoint as IPEndPoint;
        var address = remote?.Address is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4().ToString() : remote?.Address.ToString() ?? connectHost;
        byte[]? leafDer = null;
        var sentDer = new List<byte[]>();
        var errors = SslPolicyErrors.None;
        var recorder = new FirstBytesStream(tcp.GetStream());
        await using var ssl = new SslStream(recorder, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = target,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            // §5.1: without this policy, .NET follows the AIA and OCSP URLs of the server's own certificate.
            CertificateChainPolicy = NoDownloads(),
            RemoteCertificateValidationCallback = (_, certificate, chain, policyErrors) =>
            {
                errors = policyErrors;
                if (certificate is not null)
                {
                    leafDer = certificate.GetRawCertData();
                }
                if (chain is not null)
                {
                    foreach (var extra in chain.ChainPolicy.ExtraStore)
                    {
                        if (leafDer is null || !extra.RawData.AsSpan().SequenceEqual(leafDer))
                        {
                            sentDer.Add(extra.RawData);
                        }
                    }
                }
                return true;
            },
        };
        var watch = Stopwatch.StartNew();
        try
        {
            await ssl.AuthenticateAsClientAsync(options, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return recorder.Received > 0 && !recorder.LooksLikeTls
                ? CertificateProbeResult.Failed(CertificateFailureKind.NotTls, NotTls(recorder, endpoint))
                : CertificateProbeResult.Failed(CertificateFailureKind.Timeout,
                    $"timeout: {connectHost}:{endpoint.Port} accepted the connection but finished no TLS handshake within {timeout.TotalSeconds:0} s");
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            if (recorder.Received > 0 && !recorder.LooksLikeTls)
            {
                return CertificateProbeResult.Failed(CertificateFailureKind.NotTls, NotTls(recorder, endpoint));
            }
            if (leafDer is null)
            {
                var reason = Innermost(e);
                return CertificateProbeResult.Failed(CertificateFailureKind.HandshakeFailed,
                    $"handshake failed: {reason}{(LooksLegacy(reason) ? " (the server may be too old for PNMT's TLS: TLS 1.0/1.1 or a weak key)" : "")}");
            }
            // The certificate arrived before the handshake broke off: it is read all the same.
        }
        watch.Stop();
        if (leafDer is null)
        {
            return CertificateProbeResult.Failed(CertificateFailureKind.NoCertificate, "the server presented no certificate");
        }

        string protocol, cipher;
        try
        {
            protocol = Protocol(ssl.SslProtocol);
            cipher = ssl.NegotiatedCipherSuite.ToString();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            protocol = "?";
            cipher = "?";
        }
        var connection = new CertificateConnection(address, remote?.Port ?? endpoint.Port, sni, protocol, cipher, (int)watch.ElapsedMilliseconds);
        var nameMatches = !errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
        return new CertificateProbeResult(Evaluate(leafDer, sentDer.Take(MaxChain).ToList(), sentDer.Count > MaxChain, trust, nameMatches, connection), null, null);
    }

    /// <summary>
    /// The validation and the facts of a certificate as received (public for the tests and the seed): the built chain under
    /// the trust, completeness from the server's certificates alone, the chain's expiry.
    /// </summary>
    public static CertificateRead Evaluate(byte[] leafDer, IReadOnlyList<byte[]> sentDer, bool chainTruncated, CertificateTrustInput trust,
        bool nameMatches, CertificateConnection connection)
    {
        using var leaf = X509CertificateLoader.LoadCertificate(leafDer);
        var sent = sentDer.Select(d => X509CertificateLoader.LoadCertificate(d)).ToList();
        var built = new List<List<X509Certificate2>>();
        (bool, List<string>, List<X509Certificate2>) Tracked((bool Trusted, List<string> Problems, List<X509Certificate2> Chain) result)
        {
            built.Add(result.Chain);
            return result;
        }
        try
        {
            var facts = CertificateParser.Parse(leaf);
            var sentHashes = sent.Select(CertificateParser.Sha256).ToHashSet(StringComparer.Ordinal);
            var anchorHashes = trust.Anchors.Select(CertificateParser.Sha256).ToHashSet(StringComparer.Ordinal);
            var privateHashes = trust.Intermediates.Select(CertificateParser.Sha256).ToHashSet(StringComparer.Ordinal);

            // The public view, for every mode: trusted by the system's roots (the tab's "also trusted by public CAs").
            var (publicTrusted, publicProblems, publicChain) = Tracked(Build(leaf, sent, null, null));
            var trusted = publicTrusted;
            var problems = publicProblems;
            var chainCerts = publicChain;
            string? trustedBy = publicTrusted ? CertificateParser.Name(publicChain[^1].SubjectName).Short : null;
            List<X509Certificate2>? privateChain = null;
            if (trust.Mode == CertificateTrust.Private)
            {
                (trusted, problems, privateChain) = Tracked(Build(leaf, sent, trust.Anchors, trust.Intermediates));
                chainCerts = privateChain;
                var anchor = CertificateParser.Sha256(privateChain[^1]);
                trustedBy = trusted ? trust.Names.GetValueOrDefault(anchor) ?? "a private CA" : null;
                if (!trusted && problems.Count == 0)
                {
                    problems = ["not signed by the sensor's private CA"];
                }
            }
            else if (trust.Mode == CertificateTrust.Pinned)
            {
                trustedBy = null;
            }
            var complete = Complete(chainCerts, sentHashes);
            var stopsAt = chainCerts[^1];
            if (!trusted && !CertificateParser.IsSelfSigned(stopsAt) && CertificateParser.IsCa(stopsAt))
            {
                // The chain stops at a CA whose issuer PNMTD doesn't have: name it, rather than "an intermediate is missing".
                var issuer = CertificateParser.Name(stopsAt.IssuerName).Short;
                var text = trust.Mode == CertificateTrust.Private
                    ? $"signed by {issuer}, not by the sensor's private CA"
                    : $"the chain ends at {issuer}, a CA PNMT doesn't trust (a private CA?)";
                problems = problems.Select(p => p == PartialChainText ? text : p).ToList();
            }
            if (trust.Mode == CertificateTrust.Private)
            {
                problems = problems.Select(p => p == UntrustedRootText ? "the chain ends at a root that isn't the sensor's private CA" : p).ToList();
            }
            var chain = new List<CertificateChainEntry>();
            for (var i = 0; i < chainCerts.Count; i++)
            {
                var c = chainCerts[i];
                var sha = CertificateParser.Sha256(c);
                var last = i == chainCerts.Count - 1;
                var selfSigned = CertificateParser.IsSelfSigned(c);
                var (source, sourceName) = i == 0 || sentHashes.Contains(sha)
                    ? (CertificateSource.Server, (string?)null)
                    : anchorHashes.Contains(sha) || privateHashes.Contains(sha)
                        ? (CertificateSource.PrivateCa, trust.Names.GetValueOrDefault(sha))
                        : last && publicTrusted && selfSigned ? (CertificateSource.PublicRoot, null) : (CertificateSource.Store, null);
                chain.Add(new CertificateChainEntry(i, CertificateParser.Name(c.SubjectName), CertificateParser.Name(c.IssuerName),
                    DateTime.SpecifyKind(c.NotBefore.ToUniversalTime(), DateTimeKind.Utc), DateTime.SpecifyKind(c.NotAfter.ToUniversalTime(), DateTimeKind.Utc),
                    sha, CertificateParser.Key(c).Label, source, sourceName, last && trusted && trust.Mode != CertificateTrust.Pinned, selfSigned));
            }

            // The chain's expiry: the leaf, the intermediates the server sends and, in the private mode, the anchor.
            var expiring = new List<X509Certificate2> { leaf };
            expiring.AddRange(sent.Where(s => !CertificateParser.IsSelfSigned(s)));
            if (trust.Mode == CertificateTrust.Private && privateChain is { Count: > 0 } pc && trusted)
            {
                expiring.Add(pc[^1]);
            }
            var first = expiring.MinBy(c => c.NotAfter)!;
            var firstIsLeaf = ReferenceEquals(first, leaf) || first.NotAfter == leaf.NotAfter;

            var validation = new CertificateValidation(
                trust.Mode != CertificateTrust.Pinned && trusted,
                trustedBy,
                publicTrusted,
                nameMatches,
                complete,
                sent.Any(CertificateParser.IsSelfSigned),
                facts.SelfSigned,
                DateTime.SpecifyKind((firstIsLeaf ? leaf : first).NotAfter.ToUniversalTime(), DateTimeKind.Utc),
                CertificateParser.Name((firstIsLeaf ? leaf : first).SubjectName).Short,
                firstIsLeaf,
                trust.Mode == CertificateTrust.Pinned ? [] : problems);

            var pem = new StringBuilder(leaf.ExportCertificatePem()).Append('\n');
            var truncated = chainTruncated;
            foreach (var s in sent)
            {
                var next = s.ExportCertificatePem() + "\n";
                if (pem.Length + next.Length > MaxPem)
                {
                    truncated = true;
                    break;
                }
                pem.Append(next);
            }
            return new CertificateRead(leafDer, sentDer, facts with { Truncated = facts.Truncated || truncated }, chain, connection, validation,
                pem.ToString(), truncated);
        }
        finally
        {
            foreach (var c in built.SelectMany(b => b).Where(c => !ReferenceEquals(c, leaf)).Concat(sent))
            {
                c.Dispose();
            }
        }
    }

    private const string PartialChainText = "the chain doesn't reach a trusted root: the server doesn't send its intermediate";

    /// <summary>
    /// Every certificate between the leaf and the root came from the server (§5.1). A chain that stops at a CA is complete:
    /// what is missing is its root, which servers needn't send. One that stops at the leaf is not.
    /// </summary>
    private static bool Complete(List<X509Certificate2> chain, HashSet<string> sent)
    {
        for (var i = 1; i < chain.Count; i++)
        {
            var root = i == chain.Count - 1 && CertificateParser.IsSelfSigned(chain[i]);
            if (!root && !sent.Contains(CertificateParser.Sha256(chain[i])))
            {
                return false;
            }
        }
        var last = chain[^1];
        return CertificateParser.IsSelfSigned(last) || (chain.Count > 1 && CertificateParser.IsCa(last));
    }
    private const string UntrustedRootText = "the chain ends at a root PNMT doesn't know (self-signed?)";

    /// <summary>
    /// A chain at the current time, downloads off, time validity ignored (expiry is the verdict's): with
    /// <paramref name="anchors"/> null against the system's roots, otherwise against those anchors only.
    /// </summary>
    private static (bool Trusted, List<string> Problems, List<X509Certificate2> Chain) Build(X509Certificate2 leaf, IReadOnlyList<X509Certificate2> sent,
        IReadOnlyList<X509Certificate2>? anchors, IReadOnlyList<X509Certificate2>? intermediates)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy = NoDownloads();
        chain.ChainPolicy.VerificationTime = Clock.UtcNow;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.IgnoreCtlNotTimeValid
            | X509VerificationFlags.IgnoreNotTimeNested;
        chain.ChainPolicy.ExtraStore.AddRange(sent.ToArray());
        if (anchors is not null)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(anchors.ToArray());
            if (intermediates is not null)
            {
                chain.ChainPolicy.ExtraStore.AddRange(intermediates.ToArray());
            }
        }
        var built = chain.Build(leaf);
        var problems = chain.ChainStatus
            .Where(s => s.Status is not (X509ChainStatusFlags.NoError or X509ChainStatusFlags.NotTimeValid or X509ChainStatusFlags.NotTimeNested
                or X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation))
            .Select(s => Problem(s.Status, s.StatusInformation))
            .Distinct()
            .ToList();
        var elements = chain.ChainElements.Select(e => X509CertificateLoader.LoadCertificate(e.Certificate.RawData)).ToList();
        if (elements.Count > 0)
        {
            elements[0].Dispose();
            elements[0] = leaf;
        }
        return (built || problems.Count == 0, problems, elements.Count > 0 ? elements : [leaf]);
    }

    private static string Problem(X509ChainStatusFlags status, string information) => status switch
    {
        X509ChainStatusFlags.UntrustedRoot => UntrustedRootText,
        X509ChainStatusFlags.PartialChain => PartialChainText,
        X509ChainStatusFlags.NotSignatureValid => "a signature in the chain doesn't verify",
        X509ChainStatusFlags.NotValidForUsage => "the certificate is not valid for this use",
        X509ChainStatusFlags.InvalidBasicConstraints => "a certificate of the chain may not sign others",
        X509ChainStatusFlags.Revoked => "a certificate of the chain is revoked",
        X509ChainStatusFlags.Cyclic => "the chain is cyclic",
        X509ChainStatusFlags.InvalidNameConstraints or X509ChainStatusFlags.HasNotPermittedNameConstraint
            or X509ChainStatusFlags.HasExcludedNameConstraint => "the CA's name constraints don't allow this name",
        X509ChainStatusFlags.ExplicitDistrust => "a certificate of the chain is distrusted",
        X509ChainStatusFlags.HasWeakSignature => "a signature in the chain is too weak",
        _ => string.IsNullOrWhiteSpace(information) ? status.ToString() : information.Trim(),
    };

    private static X509ChainPolicy NoDownloads() => new()
    {
        DisableCertificateDownloads = true,
        RevocationMode = X509RevocationMode.NoCheck,
    };

    private static string Protocol(SslProtocols protocol) => protocol switch
    {
#pragma warning disable SYSLIB0039 // reported, not chosen
        SslProtocols.Tls => "TLS 1.0",
        SslProtocols.Tls11 => "TLS 1.1",
#pragma warning restore SYSLIB0039
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.Tls13 => "TLS 1.3",
        _ => protocol.ToString(),
    };

    private static string NotTls(FirstBytesStream recorder, TlsEndpoint endpoint) =>
        recorder.LooksLikeHttp
            ? $"not TLS: {endpoint.Normalized} answered in plain HTTP"
            : $"not TLS: {endpoint.Normalized} answered something else ({recorder.Preview})";

    private static string Innermost(Exception e)
    {
        while (e.InnerException is not null)
        {
            e = e.InnerException;
        }
        return e.Message.Trim().TrimEnd('.');
    }

    private static bool LooksLegacy(string reason) =>
        reason.Contains("unsupported protocol", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("no protocols available", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("dh key too small", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("ee key too small", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("ca md too weak", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("sslv3 alert handshake failure", StringComparison.OrdinalIgnoreCase);

    /// <summary>The network stream, recording the first bytes the server sends: TLS starts with a handshake (0x16) or an alert (0x15) record.</summary>
    private sealed class FirstBytesStream(Stream inner) : Stream
    {
        private readonly byte[] first = new byte[16];

        public int Received { get; private set; }

        public bool LooksLikeTls => Received == 0 || first[0] is 0x16 or 0x15;

        public bool LooksLikeHttp => Received >= 4 && Encoding.ASCII.GetString(first, 0, Math.Min(Received, first.Length)).StartsWith("HTTP", StringComparison.Ordinal);

        public string Preview
        {
            get
            {
                var text = new string(Encoding.ASCII.GetString(first, 0, Math.Min(Received, first.Length)).Select(c => c is >= ' ' and < '\x7f' ? c : '.').ToArray());
                return $"\"{text}\"";
            }
        }

        private void Record(ReadOnlySpan<byte> data)
        {
            if (Received < first.Length)
            {
                var take = Math.Min(first.Length - Received, data.Length);
                data[..take].CopyTo(first.AsSpan(Received));
                Received += take;
            }
            else
            {
                Received += data.Length > 0 ? 1 : 0;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Record(buffer.Span[..read]);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Record(buffer.AsSpan(offset, read));
            return read;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
