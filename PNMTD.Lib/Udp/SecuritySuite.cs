namespace PNMTD.Lib.Udp;

/// <summary>The 4-bit SUITE of a packet (docs/udp-protocol.md §3.5): how it is signed, and whether it is encrypted.</summary>
public enum SecuritySuite : byte
{
    None = 0,
    Token32 = 1,
    HmacSha256_32 = 2,
    HmacSha256_64 = 3,
    AesCmac32 = 4,
    AesCmac64 = 5,
    AesCcm32 = 6,
    AesCcm64 = 7,
    HmacMd5_32 = 8,
    HmacMd5_64 = 9,
    HmacSha1_32 = 10,
    HmacSha1_64 = 11,
}

/// <summary>
/// The algorithms a node's 16-byte key is used with. One key is never used with two families
/// (§3.5); encryption needs <see cref="Aes"/>.
/// </summary>
public enum KeyFamily : short
{
    HmacSha256 = 0,
    Aes = 1,
    HmacSha1 = 2,
    HmacMd5 = 3,
}

public static class SecuritySuites
{
    public const int KeyLength = 16;

    public const int TokenLength = 4;

    public static bool IsDefined(byte value) => value <= (byte)SecuritySuite.HmacSha1_64;

    /// <summary>0, 4 or 8 bytes.</summary>
    public static int TagLength(this SecuritySuite suite) => suite switch
    {
        SecuritySuite.None => 0,
        SecuritySuite.Token32 => 4,
        SecuritySuite.HmacSha256_64 or SecuritySuite.AesCmac64 or SecuritySuite.AesCcm64
            or SecuritySuite.HmacMd5_64 or SecuritySuite.HmacSha1_64 => 8,
        _ => 4,
    };

    /// <summary>Signed with the node's key (2–11), with the counter enforced: not NONE or TOKEN32.</summary>
    public static bool IsKeyed(this SecuritySuite suite) => suite >= SecuritySuite.HmacSha256_32;

    public static bool IsEncrypted(this SecuritySuite suite) => suite is SecuritySuite.AesCcm32 or SecuritySuite.AesCcm64;

    /// <summary>HMAC-MD5 and HMAC-SHA1: for devices that have nothing better (§3.5).</summary>
    public static bool IsLegacy(this SecuritySuite suite) =>
        suite is SecuritySuite.HmacMd5_32 or SecuritySuite.HmacMd5_64 or SecuritySuite.HmacSha1_32 or SecuritySuite.HmacSha1_64;

    /// <summary>The family of a keyed suite; null for NONE and TOKEN32.</summary>
    public static KeyFamily? Family(this SecuritySuite suite) => suite switch
    {
        SecuritySuite.HmacSha256_32 or SecuritySuite.HmacSha256_64 => KeyFamily.HmacSha256,
        SecuritySuite.AesCmac32 or SecuritySuite.AesCmac64 or SecuritySuite.AesCcm32 or SecuritySuite.AesCcm64 => KeyFamily.Aes,
        SecuritySuite.HmacMd5_32 or SecuritySuite.HmacMd5_64 => KeyFamily.HmacMd5,
        SecuritySuite.HmacSha1_32 or SecuritySuite.HmacSha1_64 => KeyFamily.HmacSha1,
        _ => null,
    };

    /// <summary>NONE, TOKEN32 and the family's own suites: what a node of that family may allow.</summary>
    public static bool BelongsTo(this SecuritySuite suite, KeyFamily family) => suite.Family() is not { } f || f == family;

    public static IReadOnlyList<SecuritySuite> Of(KeyFamily family) => family switch
    {
        KeyFamily.HmacSha256 => [SecuritySuite.HmacSha256_32, SecuritySuite.HmacSha256_64],
        KeyFamily.Aes => [SecuritySuite.AesCmac32, SecuritySuite.AesCmac64, SecuritySuite.AesCcm32, SecuritySuite.AesCcm64],
        KeyFamily.HmacSha1 => [SecuritySuite.HmacSha1_32, SecuritySuite.HmacSha1_64],
        KeyFamily.HmacMd5 => [SecuritySuite.HmacMd5_32, SecuritySuite.HmacMd5_64],
        _ => [],
    };

    /// <summary>The family's 64-bit MAC suite, the only one a JOIN may use (§3.8).</summary>
    public static SecuritySuite JoinSuite(this KeyFamily family) => family switch
    {
        KeyFamily.Aes => SecuritySuite.AesCmac64,
        KeyFamily.HmacSha1 => SecuritySuite.HmacSha1_64,
        KeyFamily.HmacMd5 => SecuritySuite.HmacMd5_64,
        _ => SecuritySuite.HmacSha256_64,
    };

    public static bool IsJoinSuite(this SecuritySuite suite) =>
        suite is SecuritySuite.HmacSha256_64 or SecuritySuite.AesCmac64 or SecuritySuite.HmacMd5_64 or SecuritySuite.HmacSha1_64;

    /// <summary>The name in the specification: NONE, TOKEN32, HMAC-SHA256/32, AES-CCM/64, …</summary>
    public static string Name(this SecuritySuite suite) => suite switch
    {
        SecuritySuite.None => "NONE",
        SecuritySuite.Token32 => "TOKEN32",
        SecuritySuite.HmacSha256_32 => "HMAC-SHA256/32",
        SecuritySuite.HmacSha256_64 => "HMAC-SHA256/64",
        SecuritySuite.AesCmac32 => "AES-CMAC/32",
        SecuritySuite.AesCmac64 => "AES-CMAC/64",
        SecuritySuite.AesCcm32 => "AES-CCM/32",
        SecuritySuite.AesCcm64 => "AES-CCM/64",
        SecuritySuite.HmacMd5_32 => "HMAC-MD5/32",
        SecuritySuite.HmacMd5_64 => "HMAC-MD5/64",
        SecuritySuite.HmacSha1_32 => "HMAC-SHA1/32",
        SecuritySuite.HmacSha1_64 => "HMAC-SHA1/64",
        _ => $"suite {(byte)suite}",
    };

    /// <summary>The name <c>pnmtd udp send --suite</c> takes: none, token32, hmac32, cmac64, ccm64, sha1-32, md5-64, …</summary>
    public static string CliName(this SecuritySuite suite) => suite switch
    {
        SecuritySuite.None => "none",
        SecuritySuite.Token32 => "token32",
        SecuritySuite.HmacSha256_32 => "hmac32",
        SecuritySuite.HmacSha256_64 => "hmac64",
        SecuritySuite.AesCmac32 => "cmac32",
        SecuritySuite.AesCmac64 => "cmac64",
        SecuritySuite.AesCcm32 => "ccm32",
        SecuritySuite.AesCcm64 => "ccm64",
        SecuritySuite.HmacMd5_32 => "md5-32",
        SecuritySuite.HmacMd5_64 => "md5-64",
        SecuritySuite.HmacSha1_32 => "sha1-32",
        SecuritySuite.HmacSha1_64 => "sha1-64",
        _ => ((byte)suite).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static SecuritySuite? FromCliName(string name) =>
        Enum.GetValues<SecuritySuite>().Cast<SecuritySuite?>().FirstOrDefault(s => string.Equals(s!.Value.CliName(), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Value.Name(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The bitmask of a set of suites, as <c>udp_nodes.AllowedSuites</c> stores it.</summary>
    public static int Mask(IEnumerable<SecuritySuite> suites) => suites.Aggregate(0, (m, s) => m | (1 << (byte)s));

    public static IReadOnlyList<SecuritySuite> FromMask(int mask) =>
        Enum.GetValues<SecuritySuite>().Where(s => (mask & (1 << (byte)s)) != 0).ToList();

    public static bool InMask(this SecuritySuite suite, int mask) => (mask & (1 << (byte)suite)) != 0;
}
