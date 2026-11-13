using Microsoft.Win32;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Windows;

/// <summary>
/// The version check (docs/windows-sensor-plan.md §3.9), at every start: the product, edition, build and UBR from the registry,
/// against the list. Another version is logged, reported and flagged in PNMT; the agent runs anyway.
/// </summary>
internal static class WindowsVersion
{
    private const string Key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>Windows Server 2016, 2019, 2022 and 2025.</summary>
    private static readonly int[] ServerBuilds = [14393, 17763, 20348];

    private static readonly string[] Windows10Editions = ["Professional", "ProfessionalN", "ProfessionalWorkstation", "Enterprise", "EnterpriseN", "EnterpriseS",
        "EnterpriseSN", "IoTEnterprise", "IoTEnterpriseS", "Education", "EducationN"];

    public static AgentOs Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        var product = key?.GetValue("ProductName") as string ?? "Windows";
        var edition = key?.GetValue("EditionID") as string;
        var buildText = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
        var ubr = key?.GetValue("UBR") is int u ? u : (int?)null;
        var display = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string;
        var installation = key?.GetValue("InstallationType") as string;
        var major = key?.GetValue("CurrentMajorVersionNumber") is int m ? m : 10;
        var minor = key?.GetValue("CurrentMinorVersionNumber") is int n ? n : 0;
        int.TryParse(buildText, out var build);
        var server = installation?.StartsWith("Server", StringComparison.OrdinalIgnoreCase) == true;
        // ProductName says "Windows 10" on Windows 11 too.
        var caption = !server && build >= 22000 ? product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal) : product;
        if (!caption.StartsWith("Microsoft", StringComparison.Ordinal))
        {
            caption = "Microsoft " + caption;
        }
        var supported = server
            ? ServerBuilds.Contains(build) || build >= 26100
            : build >= 22000 || (build >= 17763 && edition is not null && Windows10Editions.Contains(edition, StringComparer.OrdinalIgnoreCase));
        var productType = server ? (IsDomainController() ? "DomainController" : "Server") : "Workstation";
        return new AgentOs(caption, edition, $"{major}.{minor}.{build}", ubr is null ? buildText : $"{buildText}.{ubr}", display, supported, productType);
    }

    private static bool IsDomainController()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        return key?.GetValue("ProductType") as string == "LanmanNT";
    }

    public static string? MachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid") as string;
    }
}
