using System.Security.Cryptography;
using System.Text;

namespace PNMTD.Lib.Mikrotik;

/// <summary>An export's keyed digests: its hash, and one per line (docs/mikrotik-backup-plan.md §3.7).</summary>
public sealed record ExportDigests(string Hash, byte[] Lines);

/// <summary>
/// Keyed digests of a normalised export (docs/mikrotik-backup-plan.md §3.7): whether it changed and by how many lines,
/// without the previous text, which PNMT cannot read back from an encrypted backup. HMAC-SHA256 with a key of the
/// installation, so a copy of the database alone cannot confirm a guessed configuration or line.
/// </summary>
public static class ExportDigest
{
    /// <summary>A line's digest: the first 16 bytes of its HMAC.</summary>
    public const int LineBytes = 16;

    public static ExportDigests Of(byte[] key, string normalised) => new(Hash(key, normalised), Lines(key, normalised));

    public static string Hash(byte[] key, string normalised) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("export\n" + normalised)));

    /// <summary>A binary backup's keyed hash, of the file as the router wrote it.</summary>
    public static string Hash(byte[] key, ReadOnlySpan<byte> file)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData("binary\n"u8);
        hmac.AppendData(file);
        return Convert.ToHexStringLower(hmac.GetHashAndReset());
    }

    /// <summary>The digests of the lines, as <see cref="LineDiff.Lines"/> splits them, one after the other.</summary>
    public static byte[] Lines(byte[] key, string normalised)
    {
        var lines = LineDiff.Lines(normalised);
        var digests = new byte[lines.Length * LineBytes];
        for (var i = 0; i < lines.Length; i++)
        {
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("line\n" + lines[i])).AsSpan(0, LineBytes).CopyTo(digests.AsSpan(i * LineBytes));
        }
        return digests;
    }

    /// <summary>Lines added and removed from one export to the next, by their digests: the counts the text would give.</summary>
    public static (int Added, int Removed) Count(byte[] oldLines, byte[] newLines) => LineDiff.Count(Split(oldLines), Split(newLines));

    private static string[] Split(byte[] digests)
    {
        var result = new string[digests.Length / LineBytes];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToHexString(digests, i * LineBytes, LineBytes);
        }
        return result;
    }
}
