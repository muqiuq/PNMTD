using System.Security.Cryptography;

namespace PNMTD.Sensor.Windows.State;

/// <summary>
/// The agent's key (docs/windows-sensor-plan.md §3.2, §3.4): ECDSA P-256 in Windows' machine key store (Microsoft Software
/// Key Storage Provider), non-exportable. Only its public half ever leaves the machine.
/// </summary>
internal static class AgentKey
{
    public static string NewName() => $"PNMT Sensor {Guid.NewGuid():N}";

    public static ECDsa Create(string name)
    {
        var parameters = new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey | CngKeyCreationOptions.OverwriteExistingKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        };
        var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, parameters);
        return new ECDsaCng(key);
    }

    public static ECDsa Open(string name) =>
        new ECDsaCng(CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey));

    /// <summary>
    /// The agent's ECDH P-256 key (§3.4, W6), in the machine store and non-exportable like the signing key: passwords typed per run
    /// and secure arguments are encrypted to it, so a TLS-inspecting proxy can't read them. Created on first use.
    /// </summary>
    public static ECDiffieHellman EncryptionKey(Guid agentId)
    {
        var name = $"PNMT Sensor encryption {agentId:N}";
        try
        {
            return new ECDiffieHellmanCng(CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey));
        }
        catch (CryptographicException)
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.KeyAgreement,
            };
            return new ECDiffieHellmanCng(CngKey.Create(CngAlgorithm.ECDiffieHellmanP256, name, parameters));
        }
    }

    public static void DeleteEncryptionKey(Guid agentId) => Delete($"PNMT Sensor encryption {agentId:N}");

    public static void Delete(string name)
    {
        try
        {
            using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey);
            key.Delete();
        }
        catch (CryptographicException)
        {
        }
    }
}
