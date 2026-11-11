using System.Security.Cryptography;

namespace PNMTD.Lib.Security;

/// <summary>
/// Random passwords for the first admin, resets and new users (ported from PortUpAcademy): drawn
/// uniformly by a cryptographic generator from letters and digits without look-alikes (no 0/O, 1/l/I),
/// because people type them from a terminal.
/// </summary>
public static class SecurePasswordGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>About 116 bits.</summary>
    public const int DefaultLength = 20;

    public static string Generate(int length = DefaultLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        return RandomNumberGenerator.GetString(Alphabet, length);
    }
}
