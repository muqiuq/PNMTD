using System.Security.Cryptography;

namespace PNMTD.Lib.Udp;

/// <summary>
/// AES-CMAC (RFC 4493) and AES-CCM (RFC 3610, L = 2), both on AES-128 in ECB mode. .NET has no CMAC,
/// and its <see cref="AesCcm"/> is not supported on every platform (macOS), so both are built here
/// on the block cipher alone and checked against the RFCs' vectors.
/// </summary>
public static class AesModes
{
    private const int Block = 16;

    /// <summary>The full 16-byte CMAC of <paramref name="message"/> under the 16-byte <paramref name="key"/>.</summary>
    public static byte[] Cmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message)
    {
        using var aes = Create(key);
        Span<byte> l = stackalloc byte[Block];
        aes.EncryptEcb(new byte[Block], l, PaddingMode.None);
        Span<byte> k1 = stackalloc byte[Block];
        Span<byte> k2 = stackalloc byte[Block];
        Subkey(l, k1);
        Subkey(k1, k2);

        var blocks = Math.Max(1, (message.Length + Block - 1) / Block);
        var complete = message.Length > 0 && message.Length % Block == 0;
        Span<byte> x = stackalloc byte[Block];
        Span<byte> y = stackalloc byte[Block];
        for (var i = 0; i < blocks - 1; i++)
        {
            Xor(x, message.Slice(i * Block, Block), y);
            aes.EncryptEcb(y, x, PaddingMode.None);
        }
        Span<byte> last = stackalloc byte[Block];
        var tail = message[((blocks - 1) * Block)..];
        if (complete)
        {
            Xor(tail, k1, last);
        }
        else
        {
            last.Clear();
            tail.CopyTo(last);
            last[tail.Length] = 0x80;
            Xor(last, k2, last);
        }
        Xor(x, last, y);
        var tag = new byte[Block];
        aes.EncryptEcb(y, tag, PaddingMode.None);
        return tag;
    }

    /// <summary>
    /// CCM encryption with a 13-byte nonce: the ciphertext (as long as the plaintext) and the tag of
    /// <paramref name="tagLength"/> bytes (4–16, even).
    /// </summary>
    public static (byte[] Ciphertext, byte[] Tag) CcmEncrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext, int tagLength)
    {
        using var aes = Create(key);
        var mac = CcmMac(aes, nonce, associatedData, plaintext, tagLength);
        var ciphertext = new byte[plaintext.Length];
        CcmCtr(aes, nonce, plaintext, ciphertext);
        var tag = new byte[tagLength];
        Span<byte> s0 = stackalloc byte[Block];
        CounterBlock(aes, nonce, 0, s0);
        for (var i = 0; i < tagLength; i++)
        {
            tag[i] = (byte)(mac[i] ^ s0[i]);
        }
        return (ciphertext, tag);
    }

    /// <summary>CCM decryption: the plaintext, or null when the tag does not verify (compared in constant time).</summary>
    public static byte[]? CcmDecrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag)
    {
        using var aes = Create(key);
        var plaintext = new byte[ciphertext.Length];
        CcmCtr(aes, nonce, ciphertext, plaintext);
        var mac = CcmMac(aes, nonce, associatedData, plaintext, tag.Length);
        Span<byte> s0 = stackalloc byte[Block];
        CounterBlock(aes, nonce, 0, s0);
        Span<byte> expected = stackalloc byte[tag.Length];
        for (var i = 0; i < tag.Length; i++)
        {
            expected[i] = (byte)(mac[i] ^ s0[i]);
        }
        return CryptographicOperations.FixedTimeEquals(expected, tag) ? plaintext : null;
    }

    /// <summary>One block of AES-128-ECB, for the AES family's session key (§3.8).</summary>
    public static byte[] EncryptBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block)
    {
        using var aes = Create(key);
        var output = new byte[Block];
        aes.EncryptEcb(block, output, PaddingMode.None);
        return output;
    }

    private static Aes Create(ReadOnlySpan<byte> key)
    {
        if (key.Length != 16)
        {
            throw new ArgumentException("AES-128 needs a 16-byte key.", nameof(key));
        }
        var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes;
    }

    private static void Subkey(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var carry = 0;
        for (var i = Block - 1; i >= 0; i--)
        {
            var b = input[i];
            output[i] = (byte)((b << 1) | carry);
            carry = b >> 7;
        }
        if ((input[0] & 0x80) != 0)
        {
            output[Block - 1] ^= 0x87;
        }
    }

    private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> output)
    {
        for (var i = 0; i < Block; i++)
        {
            output[i] = (byte)(a[i] ^ b[i]);
        }
    }

    private static void CheckCcm(ReadOnlySpan<byte> nonce, int tagLength, int length)
    {
        if (nonce.Length != 13)
        {
            throw new ArgumentException("PNMT-UDP's CCM takes a 13-byte nonce (L = 2).", nameof(nonce));
        }
        if (tagLength is < 4 or > 16 || tagLength % 2 != 0)
        {
            throw new ArgumentException("A CCM tag is 4 to 16 bytes, even.", nameof(tagLength));
        }
        if (length > 0xFFFF)
        {
            throw new ArgumentException("With L = 2 a message is at most 65535 bytes.", nameof(length));
        }
    }

    /// <summary>The CBC-MAC T of RFC 3610 §2.2 (the first <paramref name="tagLength"/> bytes).</summary>
    private static byte[] CcmMac(Aes aes, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> message, int tagLength)
    {
        CheckCcm(nonce, tagLength, message.Length);
        if (aad.Length >= 0xFF00)
        {
            throw new ArgumentException("Associated data of PNMT-UDP stay below 65280 bytes.", nameof(aad));
        }
        // B0, then l(a) ‖ a padded, then m padded.
        var length = Block + (aad.Length > 0 ? Pad(2 + aad.Length) : 0) + Pad(message.Length);
        var input = new byte[length];
        input[0] = (byte)((aad.Length > 0 ? 0x40 : 0) | (((tagLength - 2) / 2) << 3) | (2 - 1));
        nonce.CopyTo(input.AsSpan(1));
        input[14] = (byte)(message.Length >> 8);
        input[15] = (byte)message.Length;
        var at = Block;
        if (aad.Length > 0)
        {
            input[at] = (byte)(aad.Length >> 8);
            input[at + 1] = (byte)aad.Length;
            aad.CopyTo(input.AsSpan(at + 2));
            at += Pad(2 + aad.Length);
        }
        message.CopyTo(input.AsSpan(at));

        Span<byte> x = stackalloc byte[Block];
        Span<byte> y = stackalloc byte[Block];
        for (var i = 0; i < length; i += Block)
        {
            Xor(x, input.AsSpan(i, Block), y);
            aes.EncryptEcb(y, x, PaddingMode.None);
        }
        return x[..tagLength].ToArray();
    }

    private static void CcmCtr(Aes aes, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> s = stackalloc byte[Block];
        for (var i = 0; i < input.Length; i += Block)
        {
            CounterBlock(aes, nonce, (ushort)(i / Block + 1), s);
            var n = Math.Min(Block, input.Length - i);
            for (var j = 0; j < n; j++)
            {
                output[i + j] = (byte)(input[i + j] ^ s[j]);
            }
        }
    }

    /// <summary>S_i = E(K, A_i), with A_i = flags (L − 1) ‖ nonce ‖ i.</summary>
    private static void CounterBlock(Aes aes, ReadOnlySpan<byte> nonce, ushort counter, Span<byte> output)
    {
        Span<byte> a = stackalloc byte[Block];
        a[0] = 2 - 1;
        nonce.CopyTo(a[1..]);
        a[14] = (byte)(counter >> 8);
        a[15] = (byte)counter;
        aes.EncryptEcb(a, output, PaddingMode.None);
    }

    private static int Pad(int length) => (length + Block - 1) / Block * Block;
}
