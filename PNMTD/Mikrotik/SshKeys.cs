using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace PNMTD.Mikrotik;

/// <summary>An Ed25519 key pair in OpenSSH's formats: the public line, and the private key file SSH.NET reads.</summary>
public sealed record SshKeyPair(string PublicLine, string PrivatePem, string Comment);

/// <summary>
/// PNMTD's SSH keys (docs/mikrotik-plan.md §4.2): Ed25519, which RouterOS accepts for users since 7.12.
/// .NET has no Ed25519, so BouncyCastle generates the pair; the formats are OpenSSH's
/// (<c>ssh-ed25519 AAAA… comment</c>, and <c>openssh-key-v1</c> without a passphrase, encrypted at rest
/// by Data Protection instead).
/// </summary>
public static class SshKeys
{
    private const string KeyType = "ssh-ed25519";

    public static SshKeyPair GenerateEd25519(string comment)
    {
        var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom());
        var seed = privateKey.GetEncoded();
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        try
        {
            return new SshKeyPair(PublicLine(publicKey, comment), PrivatePem(seed, publicKey, comment), comment);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>The wire form of the public key: string "ssh-ed25519", string key.</summary>
    public static byte[] PublicBlob(byte[] publicKey)
    {
        var w = new SshWriter();
        w.String(KeyType);
        w.String(publicKey);
        return w.ToArray();
    }

    public static string PublicLine(byte[] publicKey, string comment) =>
        $"{KeyType} {Convert.ToBase64String(PublicBlob(publicKey))} {comment}";

    /// <summary>The SHA-256 fingerprint of a public line, as <c>ssh-keygen -l</c> and RouterOS 7.25 show it ("SHA256:…").</summary>
    public static string Fingerprint(string publicLine)
    {
        var parts = publicLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var blob = Convert.FromBase64String(parts[1]);
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
    }

    /// <summary>openssh-key-v1 (PROTOCOL.key), unencrypted, one key.</summary>
    private static string PrivatePem(byte[] seed, byte[] publicKey, string comment)
    {
        var check = RandomNumberGenerator.GetBytes(4);
        var secret = new SshWriter();
        secret.Raw(check);
        secret.Raw(check);
        secret.String(KeyType);
        secret.String(publicKey);
        secret.String([.. seed, .. publicKey]);
        secret.String(comment);
        for (byte pad = 1; secret.Length % 8 != 0; pad++)
        {
            secret.Raw([pad]);
        }

        var file = new SshWriter();
        file.Raw(Encoding.ASCII.GetBytes("openssh-key-v1\0"));
        file.String("none");
        file.String("none");
        file.String([]);
        file.UInt32(1);
        file.String(PublicBlob(publicKey));
        var secretBytes = secret.ToArray();
        file.String(secretBytes);
        CryptographicOperations.ZeroMemory(secretBytes);

        var base64 = Convert.ToBase64String(file.ToArray());
        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (var i = 0; i < base64.Length; i += 70)
        {
            pem.Append(base64, i, Math.Min(70, base64.Length - i)).Append('\n');
        }
        return pem.Append("-----END OPENSSH PRIVATE KEY-----\n").ToString();
    }

    private sealed class SshWriter
    {
        private readonly MemoryStream stream = new();

        public long Length => stream.Length;

        public void Raw(byte[] bytes) => stream.Write(bytes);

        public void UInt32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            stream.Write(b);
        }

        public void String(byte[] bytes)
        {
            UInt32((uint)bytes.Length);
            stream.Write(bytes);
        }

        public void String(string text) => String(Encoding.UTF8.GetBytes(text));

        public byte[] ToArray() => stream.ToArray();
    }
}
