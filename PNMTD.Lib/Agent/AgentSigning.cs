using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PNMTD.Lib.Agent;

/// <summary>
/// The signatures of the agent protocol (docs/windows-sensor-plan.md §3.4): ECDSA P-256 with SHA-256, IEEE P1363
/// (r‖s), Base64. Public keys travel as SubjectPublicKeyInfo, Base64. Fingerprints are the first 16 bytes of the
/// key's SHA-256 in groups of four hex digits, "AB12 CD34 …", as the window and PNMT's agent log show them.
/// </summary>
public static class AgentSigning
{
    private const string HelloDomain = "pnmt-agent-v1";
    private const string SignedDomain = "pnmt-signed-v1";

    public static string Fingerprint(byte[] subjectPublicKeyInfo)
    {
        var hash = SHA256.HashData(subjectPublicKeyInfo);
        var hex = Convert.ToHexString(hash, 0, 16);
        return string.Join(' ', Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
    }

    public static string Fingerprint(string subjectPublicKeyInfoBase64) => Fingerprint(Convert.FromBase64String(subjectPublicKeyInfoBase64));

    /// <summary>A P-256 public key from SPKI Base64, or null when it is anything else.</summary>
    public static ECDsa? ImportPublicKey(string? subjectPublicKeyInfoBase64)
    {
        if (string.IsNullOrWhiteSpace(subjectPublicKeyInfoBase64) || subjectPublicKeyInfoBase64.Length > 1024)
        {
            return null;
        }
        ECDsa? key = null;
        try
        {
            key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(subjectPublicKeyInfoBase64), out _);
            if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                key.Dispose();
                return null;
            }
            return key;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            key?.Dispose();
            return null;
        }
    }

    public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// What the agent signs in its hello: the domain, PNMT's origin as the agent sees it, its id, the nonce, its time, and its
    /// encryption key (W6), so the key PNMTD encrypts passwords to is the agent's own.
    /// </summary>
    public static byte[] HelloPayload(string origin, Guid agentId, string nonce, DateTimeOffset agentTime, string? encryptionKey = null) =>
        Encoding.UTF8.GetBytes($"{HelloDomain}\n{origin}\n{agentId:D}\n{nonce}\n{agentTime.ToUniversalTime():O}\n{encryptionKey}");

    public static string SignHello(ECDsa key, string origin, Guid agentId, string nonce, DateTimeOffset agentTime, string? encryptionKey = null) =>
        Convert.ToBase64String(key.SignData(HelloPayload(origin, agentId, nonce, agentTime, encryptionKey), HashAlgorithmName.SHA256));

    public static bool VerifyHello(ECDsa publicKey, Hello hello, string nonce)
    {
        try
        {
            return publicKey.VerifyData(HelloPayload(hello.Origin, hello.AgentId, nonce, hello.AgentTime, hello.EncryptionKey),
                Convert.FromBase64String(hello.Signature), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The origin of a URL as both sides compare it: scheme, host and port, lower case, default ports left out.</summary>
    public static string Origin(Uri url) =>
        url.IsDefaultPort ? $"{url.Scheme}://{url.Host}".ToLowerInvariant() : $"{url.Scheme}://{url.Host}:{url.Port}".ToLowerInvariant();

    /// <summary>
    /// <paramref name="session"/>: the nonce of the connection's challenge, for everything sent to a connected agent (§3.4);
    /// null for the join grant and the download manifest, which belong to no connection.
    /// </summary>
    public static SignedEnvelope Sign<T>(ECDsa key, string type, Guid agentId, T body, TimeSpan? lifetime = null, string? requestId = null,
        string? session = null)
    {
        var now = DateTimeOffset.UtcNow;
        var content = new SignedContent(type, agentId, requestId ?? NewRequestId(), now, lifetime is { } l ? now + l : null,
            AgentJson.ToElement(body), session);
        var text = JsonSerializer.Serialize(content, AgentJson.Options);
        var signature = key.SignData(SignedPayload(text), HashAlgorithmName.SHA256);
        return new SignedEnvelope(text, Convert.ToBase64String(signature));
    }

    /// <summary>The content of a signed envelope, if PNMTD's key signed it. The caller checks type, agent, expiry and replay.</summary>
    public static SignedContent? Verify(ECDsa publicKey, SignedEnvelope? envelope)
    {
        if (envelope is null || string.IsNullOrEmpty(envelope.P) || string.IsNullOrEmpty(envelope.S))
        {
            return null;
        }
        try
        {
            if (!publicKey.VerifyData(SignedPayload(envelope.P), Convert.FromBase64String(envelope.S), HashAlgorithmName.SHA256))
            {
                return null;
            }
            return JsonSerializer.Deserialize<SignedContent>(envelope.P, AgentJson.Options);
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    public static string NewRequestId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static byte[] SignedPayload(string text) => Encoding.UTF8.GetBytes($"{SignedDomain}\n{text}");
}

/// <summary>
/// Join tokens (§3.3, §8.7): <c>xxxx-xxxx</c>, 8 characters from 31 (no 0 o 1 i l), about 40 bits. Only the hash is
/// stored; the first two characters name it in lists.
/// </summary>
public static class JoinTokens
{
    public const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public static string Generate()
    {
        Span<char> chars = stackalloc char[9];
        for (var i = 0; i < 9; i++)
        {
            chars[i] = i == 4 ? '-' : Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }
        return new string(chars);
    }

    /// <summary>The token as typed, trimmed and lower case, with or without its dash; null when it is not a token.</summary>
    public static string? Normalize(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }
        var compact = typed.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "");
        if (compact.Length != 8 || compact.Any(c => !Alphabet.Contains(c)))
        {
            return null;
        }
        return $"{compact[..4]}-{compact[4..]}";
    }

    public static string Hash(string normalized) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();

    public static string Prefix(string normalized) => normalized[..2];
}
