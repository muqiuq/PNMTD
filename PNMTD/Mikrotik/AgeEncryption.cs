using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Security;

namespace PNMTD.Mikrotik;

/// <summary>
/// One-way encryption of backups in the age format (docs/mikrotik-backup-plan.md §3.2, https://age-encryption.org/v1)
/// to one X25519 recipient (<c>age1…</c>). PNMT only encrypts: it never holds an identity, so it cannot decrypt.
/// Readable with the standard tools: <c>age -d -i key.txt file.age</c>.
/// </summary>
public static class AgeEncryption
{
    private const string Version = "age-encryption.org/v1";
    private const string X25519Label = "age-encryption.org/v1/X25519";
    private const int ChunkSize = 64 * 1024;

    /// <summary>The recipient's public key, or null when it is not an age X25519 recipient (<c>age1…</c>).</summary>
    public static byte[]? ParseRecipient(string? recipient)
    {
        var text = recipient?.Trim();
        if (text is null || !text.StartsWith("age1", StringComparison.Ordinal) || text != text.ToLowerInvariant())
        {
            return null;
        }
        return Bech32.Decode(text) is ("age", { Length: 32 } key) ? key : null;
    }

    /// <summary>A short name for a key, for lists and for "which key does this backup need": 12 hex digits.</summary>
    public static string Fingerprint(byte[] publicKey) => Convert.ToHexStringLower(SHA256.HashData(publicKey))[..12];

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] recipient)
    {
        if (recipient.Length != 32)
        {
            throw new ArgumentException("An X25519 public key has 32 bytes.", nameof(recipient));
        }
        var fileKey = RandomNumberGenerator.GetBytes(16);

        // The X25519 stanza: an ephemeral share, and the file key wrapped with the shared secret.
        var ephemeral = new byte[X25519.ScalarSize];
        X25519.GeneratePrivateKey(new SecureRandom(), ephemeral);
        var share = new byte[X25519.PointSize];
        X25519.GeneratePublicKey(ephemeral, 0, share, 0);
        var shared = new byte[X25519.PointSize];
        if (!X25519.CalculateAgreement(ephemeral, 0, recipient, 0, shared, 0))
        {
            throw new CryptographicException("The recipient is not a valid X25519 key.");
        }
        var wrapKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, [.. share, .. recipient], Encoding.ASCII.GetBytes(X25519Label));
        var wrapped = new byte[fileKey.Length + 16];
        using (var aead = new ChaCha20Poly1305(wrapKey))
        {
            aead.Encrypt(new byte[12], fileKey, wrapped.AsSpan(0, fileKey.Length), wrapped.AsSpan(fileKey.Length));
        }

        var header = new StringBuilder()
            .Append(Version).Append('\n')
            .Append("-> X25519 ").Append(Base64(share)).Append('\n')
            .Append(Base64(wrapped)).Append('\n')
            .Append("---")
            .ToString();
        var macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, 32, [], "header"u8.ToArray());
        var mac = HMACSHA256.HashData(macKey, Encoding.ASCII.GetBytes(header));

        using var output = new MemoryStream(plaintext.Length + 256 + (plaintext.Length / ChunkSize + 1) * 16);
        output.Write(Encoding.ASCII.GetBytes($"{header} {Base64(mac)}\n"));

        // The payload: a nonce, then STREAM over 64 KiB chunks, the last one flagged.
        var nonce = RandomNumberGenerator.GetBytes(16);
        output.Write(nonce);
        var payloadKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, 32, nonce, "payload"u8.ToArray());
        using var stream = new ChaCha20Poly1305(payloadKey);
        var chunkNonce = new byte[12];
        var counter = 0UL;
        var offset = 0;
        do
        {
            var length = Math.Min(ChunkSize, plaintext.Length - offset);
            var last = offset + length >= plaintext.Length;
            for (var i = 0; i < 8; i++)
            {
                chunkNonce[10 - i] = (byte)(counter >> (8 * i));
            }
            chunkNonce[11] = last ? (byte)1 : (byte)0;
            var sealedChunk = new byte[length + 16];
            stream.Encrypt(chunkNonce, plaintext.Slice(offset, length), sealedChunk.AsSpan(0, length), sealedChunk.AsSpan(length));
            output.Write(sealedChunk);
            offset += length;
            counter++;
        }
        while (offset < plaintext.Length);
        return output.ToArray();
    }

    /// <summary>age's base64: the standard alphabet, without padding.</summary>
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');
}

/// <summary>Bech32 (BIP 173) as age writes its keys: <c>age1…</c>, <c>AGE-SECRET-KEY-1…</c>.</summary>
public static class Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    /// <summary>The human-readable part and the data, or null when the string or its checksum is wrong.</summary>
    public static (string Hrp, byte[] Data)? Decode(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower != text && text.ToUpperInvariant() != text)
        {
            return null;
        }
        var separator = lower.LastIndexOf('1');
        if (separator < 1 || separator + 7 > lower.Length)
        {
            return null;
        }
        var hrp = lower[..separator];
        var values = new byte[lower.Length - separator - 1];
        for (var i = 0; i < values.Length; i++)
        {
            var v = Charset.IndexOf(lower[separator + 1 + i], StringComparison.Ordinal);
            if (v < 0)
            {
                return null;
            }
            values[i] = (byte)v;
        }
        if (Polymod([.. ExpandHrp(hrp), .. values]) != 1)
        {
            return null;
        }
        var data = Convert(values.AsSpan(0, values.Length - 6), 5, 8, pad: false);
        return data is null ? null : (hrp, data);
    }

    public static string Encode(string hrp, byte[] data)
    {
        var values = Convert(data, 8, 5, pad: true)!;
        var polymod = Polymod([.. ExpandHrp(hrp), .. values, 0, 0, 0, 0, 0, 0]) ^ 1;
        var text = new StringBuilder(hrp).Append('1');
        foreach (var v in values)
        {
            text.Append(Charset[v]);
        }
        for (var i = 0; i < 6; i++)
        {
            text.Append(Charset[(int)(polymod >> (5 * (5 - i))) & 31]);
        }
        return text.ToString();
    }

    private static uint Polymod(byte[] values)
    {
        uint[] generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (var i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) == 1)
                {
                    chk ^= generator[i];
                }
            }
        }
        return chk;
    }

    private static byte[] ExpandHrp(string hrp) =>
        [.. hrp.Select(c => (byte)(c >> 5)), 0, .. hrp.Select(c => (byte)(c & 31))];

    private static byte[]? Convert(ReadOnlySpan<byte> data, int from, int to, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var max = (1 << to) - 1;
        var result = new List<byte>();
        foreach (var value in data)
        {
            acc = (acc << from) | value;
            bits += from;
            while (bits >= to)
            {
                bits -= to;
                result.Add((byte)((acc >> bits) & max));
            }
        }
        if (pad)
        {
            if (bits > 0)
            {
                result.Add((byte)((acc << (to - bits)) & max));
            }
        }
        else if (bits >= from || ((acc << (to - bits)) & max) != 0)
        {
            return null;
        }
        return [.. result];
    }
}
