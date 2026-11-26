using System.Globalization;

namespace PNMTD.Lib.Network;

/// <summary>
/// MAC addresses as the network map keeps them (docs/network-map-plan.md §4.2): upper case with colons, as the agents
/// send them (<c>AA:BB:CC:DD:EE:FF</c>), whatever RouterOS, Windows or Linux wrote.
/// </summary>
public static class Mac
{
    /// <summary>The address in the map's form, or null when it is no 48-bit MAC (empty, all zeros, or not hex).</summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        Span<char> hex = stackalloc char[12];
        var n = 0;
        foreach (var c in text.Trim())
        {
            if (c is ':' or '-' or '.')
            {
                continue;
            }
            if (!char.IsAsciiHexDigit(c) || n == 12)
            {
                return null;
            }
            hex[n++] = char.ToUpperInvariant(c);
        }
        if (n != 12 || hex.IndexOfAnyExcept('0') < 0)
        {
            return null;
        }
        return string.Create(17, hex.ToString(), static (span, h) =>
        {
            for (var i = 0; i < 6; i++)
            {
                span[i * 3] = h[i * 2];
                span[i * 3 + 1] = h[i * 2 + 1];
                if (i < 5)
                {
                    span[i * 3 + 2] = ':';
                }
            }
        });
    }

    /// <summary>
    /// Locally administered (bit 0x02 of the first octet): phones' private addresses, and virtual machines' made-up ones.
    /// Such a MAC is kept and shown, but never matched or suggested (decision 5).
    /// </summary>
    public static bool IsRandom(string mac) =>
        mac.Length >= 2 && byte.TryParse(mac.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first) && (first & 0x02) != 0;

    /// <summary>A group address (bit 0x01): multicast and broadcast, never a device.</summary>
    public static bool IsMulticast(string mac) =>
        mac.Length >= 2 && byte.TryParse(mac.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first) && (first & 0x01) != 0;

    /// <summary>
    /// VRRP's virtual router MAC (<c>00:00:5E:00:01:xx</c>, and <c>…:02:xx</c> for IPv6): shared by every router of the group by
    /// design, so it says nothing about a cable (docs/network-suggestion-rules-plan.md §3.6).
    /// </summary>
    public static bool IsVrrp(string mac) =>
        mac.StartsWith("00:00:5E:00:01:", StringComparison.Ordinal) || mac.StartsWith("00:00:5E:00:02:", StringComparison.Ordinal);
}
