using System.Text;

namespace PNMTD.Lib.Security;

/// <summary>
/// RFC 4648 Base32 for TOTP secrets, ported from PortUpAcademy as it is. Encodes without padding,
/// as authenticator apps expect. Decoding ignores case, spaces and padding.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                builder.Append(Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }

        if (bitsLeft > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }

        return builder.ToString();
    }

    public static byte[] Decode(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return [];
        }

        var cleaned = input.Trim().TrimEnd('=').Replace(" ", string.Empty).ToUpperInvariant();
        int buffer = 0;
        int bitsLeft = 0;
        var output = new List<byte>(cleaned.Length * 5 / 8);

        foreach (var c in cleaned)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0)
            {
                throw new FormatException($"Invalid Base32 character '{c}'.");
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        return output.ToArray();
    }
}
