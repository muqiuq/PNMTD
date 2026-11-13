using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace PNMTD.WireGuard;

/// <summary>
/// WireGuard key pairs (docs/mikrotik-plan.md §3.2): X25519, 32 bytes each, in Base64 as <c>wg genkey</c> and
/// <c>wg pubkey</c> write them. .NET has no X25519, so BouncyCastle computes them; the private key is clamped
/// as RFC 7748 and WireGuard want it.
/// </summary>
public static class WireGuardKeys
{
    public static (string PrivateKey, string PublicKey) Generate()
    {
        var key = new X25519PrivateKeyParameters(new SecureRandom());
        var raw = key.GetEncoded();
        Clamp(raw);
        return (Convert.ToBase64String(raw), PublicKey(raw));
    }

    public static string PublicKey(string privateKey) => PublicKey(Convert.FromBase64String(privateKey));

    private static string PublicKey(byte[] privateKey) =>
        Convert.ToBase64String(new X25519PrivateKeyParameters(privateKey).GeneratePublicKey().GetEncoded());

    private static void Clamp(byte[] k)
    {
        k[0] &= 248;
        k[31] &= 127;
        k[31] |= 64;
    }
}
