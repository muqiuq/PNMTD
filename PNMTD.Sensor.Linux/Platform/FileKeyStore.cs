using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PNMTD.Sensor.Core;

namespace PNMTD.Sensor.Linux.Platform;

/// <summary>
/// The agent's keys as files (docs/linux-sensor-plan.md §4.5): Linux has no machine key store like CNG's, so the ECDSA P-256 signing
/// key and the ECDH P-256 encryption key are PKCS#8 PEM files, 0600, in <c>/var/lib/pnmt-agent/keys/</c> (0700, root). The signing key
/// is created at the join, named <c>agent-…</c> (agent.json's <c>keyName</c>); the encryption key on first use, per agent id, as on
/// Windows. Root can copy them; root controls the server anyway, and PNMT's Revoke ends a copied key as it ends the original.
/// </summary>
internal sealed partial class FileKeyStore(string folder) : IAgentKeyStore
{
    [GeneratedRegex("^[a-z0-9-]{1,80}$")]
    private static partial Regex SafeName();

    public string NewName() => $"agent-{Guid.NewGuid():N}";

    public ECDsa Create(string name)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Write(PathOf(name), key.ExportPkcs8PrivateKeyPem());
        return key;
    }

    public ECDsa Open(string name)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(Read(PathOf(name)));
        return key;
    }

    public void Delete(string name)
    {
        if (SafeName().IsMatch(name))
        {
            TryDelete(PathOf(name));
        }
    }

    public ECDiffieHellman EncryptionKey(Guid agentId)
    {
        var path = PathOf($"encryption-{agentId:N}");
        try
        {
            var key = ECDiffieHellman.Create();
            if (File.Exists(path))
            {
                key.ImportFromPem(Read(path));
                return key;
            }
            key.Dispose();
            var created = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            Write(path, created.ExportPkcs8PrivateKeyPem());
            return created;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // As the machine store fails on Windows: the service reports it and connects without the key.
            throw new CryptographicException($"The encryption key {path}: {e.Message}", e);
        }
    }

    public void DeleteEncryptionKey(Guid agentId) => TryDelete(PathOf($"encryption-{agentId:N}"));

    /// <summary>Every key file, for uninstall.</summary>
    public void DeleteAll()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private string PathOf(string name) =>
        SafeName().IsMatch(name) ? Path.Combine(folder, name + ".pem") : throw new CryptographicException($"\"{name}\" is not a key name of this agent.");

    private static string Read(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            throw new CryptographicException($"The key {path} does not exist: join again (pnmt-agent join).");
        }
    }

    /// <summary>Written 0600 next to its final name, then renamed: a key is never half written.</summary>
    private void Write(string path, string pem)
    {
        LinuxPlatform.SecureFolder(folder);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(pem);
        }
        File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
