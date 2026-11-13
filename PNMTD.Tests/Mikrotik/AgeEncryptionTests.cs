using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Math.EC.Rfc7748;
using PNMTD.Mikrotik;

namespace PNMTD.Tests.Mikrotik;

/// <summary>
/// The backups' one-way encryption (docs/mikrotik-backup-plan.md §3.2): age v1 with an X25519 recipient, decrypted
/// here by an independent reading of the spec. PNMTD_AGE_OUT=dir also writes files to check with the age CLI:
/// <c>age -d -i test.age-key file.age</c>.
/// </summary>
[TestClass]
public sealed class AgeEncryptionTests
{
    /// <summary>A key made with age-keygen 1.2.1, for tests only (also in the seed and the browser test).</summary>
    public const string TestIdentity = "AGE-SECRET-KEY-1T3QXPSTXSMJ5CL3W263Q9066GCFT5G3HN47A2AVJMC0RQVW7Q0ZQU3HV2W";

    public const string TestRecipient = "age1d35svtrde4pf0x47gje95qx9yzzm6cqm8jtr8f95ye4xzye2n3nqglc96t";

    [TestMethod]
    public void A_recipient_is_an_age_X25519_key()
    {
        Assert.AreEqual(32, AgeEncryption.ParseRecipient(TestRecipient)!.Length);
        Assert.IsNotNull(AgeEncryption.ParseRecipient($"  {TestRecipient}\n"));
        Assert.IsNull(AgeEncryption.ParseRecipient(TestRecipient.ToUpperInvariant()), "age writes recipients in lower case");
        Assert.IsNull(AgeEncryption.ParseRecipient(TestRecipient[..^1] + "q"), "a wrong checksum");
        Assert.IsNull(AgeEncryption.ParseRecipient(TestIdentity), "a secret key is no recipient");
        Assert.IsNull(AgeEncryption.ParseRecipient("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI"));
        // The identity's public key is the recipient, both read as age writes them.
        var (hrp, secret) = Bech32.Decode(TestIdentity)!.Value;
        Assert.AreEqual("age-secret-key-", hrp);
        var publicKey = new byte[32];
        X25519.GeneratePublicKey(secret, 0, publicKey, 0);
        Assert.AreEqual(TestRecipient, Bech32.Encode("age", publicKey));
        Assert.AreEqual(12, AgeEncryption.Fingerprint(publicKey).Length);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(64 * 1024)]
    [DataRow(64 * 1024 + 1)]
    [DataRow(200_000)]
    public void Encrypted_data_decrypts_with_the_identity(int size)
    {
        var plaintext = RandomNumberGenerator.GetBytes(size);
        var file = AgeEncryption.Encrypt(plaintext, AgeEncryption.ParseRecipient(TestRecipient)!);
        StringAssert.StartsWith(Encoding.ASCII.GetString(file, 0, 40), "age-encryption.org/v1\n-> X25519 ");
        CollectionAssert.AreEqual(plaintext, Decrypt(file, TestIdentity));
        Assert.Throws<CryptographicException>(() => Decrypt(file, Bech32.Encode("age-secret-key-", RandomNumberGenerator.GetBytes(32))));
        if (Environment.GetEnvironmentVariable("PNMTD_AGE_OUT") is { Length: > 0 } dir)
        {
            File.WriteAllBytes(Path.Combine(dir, $"{size}.age"), file);
            File.WriteAllBytes(Path.Combine(dir, $"{size}.plain"), plaintext);
        }
    }

    /// <summary>The browser's check of this encryption (useBackupKey.test.ts) decrypts a file written here.</summary>
    [TestMethod]
    public void An_export_for_the_browsers_test()
    {
        var file = AgeEncryption.Encrypt(BackupStore.Gzip(BrowserExport), AgeEncryption.ParseRecipient(TestRecipient)!);
        Assert.AreEqual(BrowserExport, BackupStore.Gunzip(Decrypt(file, TestIdentity)));
        if (Environment.GetEnvironmentVariable("PNMTD_AGE_OUT") is { Length: > 0 } dir)
        {
            File.WriteAllText(Path.Combine(dir, "export.rsc.gz.age.b64"), Convert.ToBase64String(file));
        }
    }

    public const string BrowserExport = "/ip dns set servers=1.1.1.1,8.8.8.8\n/system identity set name=st-gallen-gw\n";

    /// <summary>age v1 decryption of a file with one X25519 stanza, from the spec.</summary>
    public static byte[] Decrypt(byte[] file, string identity)
    {
        var secret = Bech32.Decode(identity)!.Value.Data;
        var text = Encoding.ASCII.GetString(file);
        var macAt = text.IndexOf("\n--- ", StringComparison.Ordinal) + 1;
        var lines = text[..macAt].Split('\n');
        Assert.AreEqual("age-encryption.org/v1", lines[0]);
        var stanza = lines[1].Split(' ');
        Assert.AreEqual("X25519", stanza[1]);
        var share = Unbase64(stanza[2]);
        var wrapped = Unbase64(lines[2]);
        var recipient = new byte[32];
        X25519.GeneratePublicKey(secret, 0, recipient, 0);
        var shared = new byte[32];
        X25519.CalculateAgreement(secret, 0, share, 0, shared, 0);
        var wrapKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, [.. share, .. recipient], "age-encryption.org/v1/X25519"u8.ToArray());
        var fileKey = new byte[16];
        using (var aead = new ChaCha20Poly1305(wrapKey))
        {
            aead.Decrypt(new byte[12], wrapped.AsSpan(0, 16), wrapped.AsSpan(16), fileKey);
        }
        var macLineEnd = text.IndexOf('\n', macAt);
        var macKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, 32, [], "header"u8.ToArray());
        var expected = HMACSHA256.HashData(macKey, Encoding.ASCII.GetBytes(text[..(macAt + 3)]));
        CollectionAssert.AreEqual(expected, Unbase64(text[(macAt + 4)..macLineEnd]), "header MAC");
        var payload = file.AsSpan(macLineEnd + 1);
        var nonce = payload[..16].ToArray();
        var payloadKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey, 32, nonce, "payload"u8.ToArray());
        using var stream = new ChaCha20Poly1305(payloadKey);
        var plaintext = new List<byte>();
        var rest = payload[16..];
        for (var counter = 0UL; ; counter++)
        {
            var length = Math.Min(64 * 1024 + 16, rest.Length);
            var last = length == rest.Length;
            var chunkNonce = new byte[12];
            for (var i = 0; i < 8; i++)
            {
                chunkNonce[10 - i] = (byte)(counter >> (8 * i));
            }
            chunkNonce[11] = last ? (byte)1 : (byte)0;
            var chunk = new byte[length - 16];
            stream.Decrypt(chunkNonce, rest[..(length - 16)], rest.Slice(length - 16, 16), chunk);
            plaintext.AddRange(chunk);
            rest = rest[length..];
            if (last)
            {
                return [.. plaintext];
            }
        }
    }

    private static byte[] Unbase64(string text) => Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
}
