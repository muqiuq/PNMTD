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
