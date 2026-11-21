namespace PNMTD.Lib.Agent;

/// <summary>
/// dpkg's version comparison (deb-version(7), docs/linux-os-updates-plan.md §6.3): <c>[epoch:]upstream[-revision]</c>. The epoch
/// compares as a number; upstream and revision part by part, alternating non-digits (letters before other characters, <c>~</c>
/// before everything, even the end) and digits (as numbers). The revision is the part after the last hyphen; none is "0".
/// </summary>
public static class DebianVersion
{
    /// <summary>Negative when <paramref name="a"/> is older, 0 when equal, positive when newer; as <c>dpkg --compare-versions</c>.</summary>
    public static int Compare(string? a, string? b)
    {
        var (epochA, upstreamA, revisionA) = Split(a ?? "");
        var (epochB, upstreamB, revisionB) = Split(b ?? "");
        var epoch = epochA.CompareTo(epochB);
        if (epoch != 0)
        {
            return epoch;
        }
        var upstream = ComparePart(upstreamA, upstreamB);
        return upstream != 0 ? upstream : ComparePart(revisionA, revisionB);
    }

    public static bool IsNewer(string? a, string? b) => Compare(a, b) > 0;

    public static bool IsOlder(string? a, string? b) => Compare(a, b) < 0;

    private static (long Epoch, string Upstream, string Revision) Split(string version)
    {
        var text = version.Trim();
        long epoch = 0;
        var colon = text.IndexOf(':');
        if (colon > 0 && long.TryParse(text.AsSpan(0, colon), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var e))
        {
            epoch = e;
            text = text[(colon + 1)..];
        }
        var hyphen = text.LastIndexOf('-');
        return hyphen >= 0 ? (epoch, text[..hyphen], text[(hyphen + 1)..]) : (epoch, text, "0");
    }

    private static int ComparePart(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            // The non-digit run, character by character.
            while ((i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])))
            {
                var ca = i < a.Length && !char.IsAsciiDigit(a[i]) ? Order(a[i]) : 0;
                var cb = j < b.Length && !char.IsAsciiDigit(b[j]) ? Order(b[j]) : 0;
                if (ca != cb)
                {
                    return ca - cb;
                }
                if (i < a.Length && !char.IsAsciiDigit(a[i]))
                {
                    i++;
                }
                if (j < b.Length && !char.IsAsciiDigit(b[j]))
                {
                    j++;
                }
            }
            // The digit run, as a number: leading zeros first, then length, then digit by digit.
            while (i < a.Length && a[i] == '0')
            {
                i++;
            }
            while (j < b.Length && b[j] == '0')
            {
                j++;
            }
            var startA = i;
            var startB = j;
            while (i < a.Length && char.IsAsciiDigit(a[i]))
            {
                i++;
            }
            while (j < b.Length && char.IsAsciiDigit(b[j]))
            {
                j++;
            }
            var lengthA = i - startA;
            var lengthB = j - startB;
            if (lengthA != lengthB)
            {
                return lengthA - lengthB;
            }
            var digits = string.CompareOrdinal(a, startA, b, startB, lengthA);
            if (digits != 0)
            {
                return digits;
            }
        }
        return 0;
    }

    /// <summary>dpkg's order of a non-digit: <c>~</c> first (before the end), the end, letters, then everything else.</summary>
    private static int Order(char c) => c == '~' ? -1 : char.IsAsciiLetter(c) ? c : c + 256;
}
