using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Principal;
using Microsoft.Win32;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Native;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// <c>system</c> (docs/windows-sensor-plan.md §4.2): identity, OS, uptime and the last shutdown, updates and a pending
/// reboot, time, Defender, the firewall, BitLocker, Secure Boot, the local administrators, sessions, the hypervisor.
/// Every 15 minutes.
/// </summary>
internal sealed class SystemCollector : Collector
{
    public override string Name => SectionNames.System;

    public override TimeSpan Interval => TimeSpan.FromMinutes(15);

    /// <summary>The licensing service takes its time on the first query after a boot.</summary>
    public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "sessions", "lastSync", "signatureAgeDays", "lastQuickScan", "lastFullScan" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var os = WindowsVersion.Read();
        var computer = Isolated.Part(errors, "Win32_ComputerSystem", () => Wmi.First(Wmi.Cimv2,
            "SELECT Name, Domain, PartOfDomain, DomainRole, Manufacturer, Model, HypervisorPresent FROM Win32_ComputerSystem"));
        var system = Isolated.Part(errors, "Win32_OperatingSystem", () => Wmi.First(Wmi.Cimv2,
            "SELECT InstallDate, OSArchitecture, LastBootUpTime FROM Win32_OperatingSystem"));
        var partOfDomain = computer?.Bool("PartOfDomain") ?? false;
        var domain = computer?.Str("Domain");
        var hostname = Environment.MachineName;
        var fqdn = Isolated.Part(errors, "fqdn", () =>
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            return string.IsNullOrEmpty(properties.DomainName) ? null : $"{properties.HostName}.{properties.DomainName}";
        });
        var ubr = int.TryParse(os.Build.Split('.').ElementAtOrDefault(1), out var u) ? u : (int?)null;
        var activation = Isolated.Part(errors, "activation", Activation);
        var boot = system?.Date("LastBootUpTime") ?? DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

        return new SystemSection(
            hostname,
            fqdn,
            domain,
            partOfDomain,
            DomainRole(computer?.Int("DomainRole")),
            WindowsVersion.MachineGuid(),
            new OsInfo(os.Caption, os.Edition, os.Version, os.Build.Split('.')[0], ubr, os.DisplayVersion, system?.Date("InstallDate"),
                system?.Str("OSArchitecture"), activation is null ? null : activation.Status == "Licensed", os.Supported, os.ProductType, activation),
            boot,
            Isolated.Part(errors, "last shutdown", LastShutdown),
            Isolated.Part(errors, "updates", () => Updates(errors)),
            Isolated.Part(errors, "time", Time),
            Security(errors),
            Isolated.Part(errors, "local administrators", LocalAdministrators) ?? [],
            Isolated.Part(errors, "sessions", Sessions) ?? [],
            Isolated.Part(errors, "hypervisor", () => Hypervisor(computer)));
    }

    /// <summary>Windows' license (the Software Licensing service's product with a key installed); null when none has a key.</summary>
    public static ActivationInfo? Activation()
    {
        // SELECT *: ProductKeyChannel and EvaluationEndDate are missing on older Windows, and a named missing property fails the query.
        var products = Wmi.Query(Wmi.Cimv2,
            "SELECT * FROM SoftwareLicensingProduct WHERE ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL",
            TimeSpan.FromSeconds(30));
        // The licensed one when several have a key (an upgraded edition keeps the old one).
        var product = products.OrderByDescending(p => p.Int("LicenseStatus") == 1).FirstOrDefault();
        if (product is null)
        {
            // Windows answered, and no product has a key: not activated, and that is the reason.
            return new ActivationInfo("No product key", null, null, null, null, null, null, null,
                Isolated.Part([], "firmware key", () => Wmi.First(Wmi.Cimv2, "SELECT * FROM SoftwareLicensingService")?.Str("OA3xOriginalProductKey")) is { Length: >= 5 } k
                    ? k[^5..] : null, null);
        }
        var service = Wmi.First(Wmi.Cimv2, "SELECT * FROM SoftwareLicensingService");
        var status = product.Int("LicenseStatus");
        var firmwareKey = service?.Str("OA3xOriginalProductKey");
        var kms = product.Str("KeyManagementServiceMachine") ?? service?.Str("KeyManagementServiceMachine")
            ?? product.Str("DiscoveredKeyManagementServiceMachineName") ?? service?.Str("DiscoveredKeyManagementServiceMachineName");
        if (kms is not null && (product.Int("KeyManagementServicePort") ?? service?.Int("KeyManagementServicePort")) is { } port and > 0 and not 1688)
        {
            kms = $"{kms}:{port}";
        }
        var reason = product.Long("LicenseStatusReason") is { } code and not 0 && status != 1 ? $"0x{(uint)code:X8}" : null;
        var evaluationEnds = product.Date("EvaluationEndDate") is { } ends && ends.Year is > 1601 and < 9999 ? ends : (DateTimeOffset?)null;
        return new ActivationInfo(
            status switch
            {
                0 => "Unlicensed",
                1 => "Licensed",
                2 => "Initial grace period",
                3 => "Additional grace period",
                4 => "Non-genuine grace period",
                5 => "Notification",
                6 => "Extended grace period",
                _ => "Unknown",
            },
            product.Str("Name"),
            product.Str("Description"),
            product.Str("ProductKeyChannel"),
            product.Str("PartialProductKey"),
            product.Int("GracePeriodRemaining") is { } grace and > 0 ? grace : null,
            evaluationEnds,
            kms,
            firmwareKey is { Length: >= 5 } key ? key[^5..] : null,
            reason);
    }

    private static string? DomainRole(int? role) => role switch
    {
        0 => "Standalone workstation",
        1 => "Member workstation",
        2 => "Standalone server",
        3 => "Member server",
        4 => "Backup domain controller",
        5 => "Primary domain controller",
        _ => null,
    };

    /// <summary>The newest 1074 (planned, with user and reason) or 6008 (unexpected) in the System log.</summary>
    public static ShutdownInfo? LastShutdown()
    {
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[(EventID=1074 and Provider[@Name='User32']) or (EventID=6008 and Provider[@Name='EventLog'])]]") { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        using var record = reader.ReadEvent();
        if (record is null)
        {
            return null;
        }
        var at = record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.MinValue;
        if (record.Id == 6008)
        {
            return new ShutdownInfo(at, true, "The previous shutdown was unexpected.", null);
        }
        var properties = record.Properties;
        string? Prop(int i) => i < properties.Count ? properties[i].Value?.ToString() : null;
        // 1074: process, computer, reason text, reason code, kind, comment, user.
        var reason = string.Join("; ", new[] { Prop(2), Prop(5) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new ShutdownInfo(at, false, string.IsNullOrEmpty(reason) ? null : reason, Prop(6));
    }

    private static UpdatesInfo Updates(List<string> errors)
    {
        var hotfixes = Isolated.Part(errors, "Win32_QuickFixEngineering", () => Wmi.Query(Wmi.Cimv2, "SELECT HotFixID, InstalledOn FROM Win32_QuickFixEngineering")
            .Select(h => (Id: h.Str("HotFixID"), At: ParseHotfixDate(h.Str("InstalledOn"))))
            .Where(h => h.Id is not null)
            .OrderByDescending(h => h.At ?? DateTimeOffset.MinValue)
            .ToList()) ?? [];
        var reasons = RebootReasons();
        return new UpdatesInfo(hotfixes.FirstOrDefault().Id, hotfixes.FirstOrDefault().At, reasons.Count > 0, reasons);
    }

    private static DateTimeOffset? ParseHotfixDate(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date) ? new DateTimeOffset(date)
        : DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out date) ? new DateTimeOffset(date) : null;

    /// <summary>A pending reboot (§4.2): Component Based Servicing, Windows Update, pending file renames.</summary>
    public static List<string> RebootReasons()
    {
        var reasons = new List<string>();
        using (var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        {
            if (cbs is not null)
            {
                reasons.Add("Component Based Servicing");
            }
        }
        using (var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
        {
            if (wu is not null)
            {
                reasons.Add("Windows Update");
            }
        }
        using (var sm = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
        {
            if (sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
            {
                reasons.Add("pending file renames");
            }
        }
        return reasons;
    }

    private static TimeInfo Time()
    {
        string? source = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("w32tm", "/query /source")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                if (process.WaitForExit(5000) && output.Wait(1000))
                {
                    var text = output.Result.Trim();
                    // An error is not a source: 0x80070426 is the Windows Time service not running (common on a VM or a workgroup PC).
                    source = process.ExitCode == 0 && !text.Contains("0x8", StringComparison.OrdinalIgnoreCase) ? text
                        : text.Contains("0x80070426", StringComparison.OrdinalIgnoreCase) ? "none: the Windows Time service is not running"
                        : null;
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        return new TimeInfo(TimeZoneInfo.Local.Id, string.IsNullOrWhiteSpace(source) ? null : source, null);
    }

    private static SecurityInfo Security(List<string> errors)
    {
        var defender = Isolated.Part(errors, "Defender", () => Wmi.First(Wmi.Defender,
            "SELECT RealTimeProtectionEnabled, AntivirusSignatureAge, QuickScanEndTime, FullScanEndTime, AMProductVersion FROM MSFT_MpComputerStatus") is { } d
            ? new DefenderInfo(d.Bool("RealTimeProtectionEnabled"), d.Int("AntivirusSignatureAge"), d.Date("QuickScanEndTime"), d.Date("FullScanEndTime"),
                d.Str("AMProductVersion"))
            : null);
        var firewall = Isolated.Part(errors, "firewall", Firewall.Profiles) ?? [];
        var bitlocker = Isolated.Part(errors, "BitLocker", BitLocker) ?? [];
        var secureBoot = Isolated.Part(errors, "Secure Boot", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key?.GetValue("UEFISecureBootEnabled") is int v ? v == 1 : (bool?)null;
        });
        return new SecurityInfo(defender, firewall, bitlocker, secureBoot);
    }

    public static List<BitLockerVolume> BitLocker() => Wmi.Query(Wmi.BitLocker, "SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume")
        .Where(v => v.Str("DriveLetter") is not null)
        .Select(v => new BitLockerVolume(v.Str("DriveLetter")!, v.Int("ProtectionStatus") switch { 1 => "On", 0 => "Off", _ => "Unknown" },
            v.Int("ConversionStatus") switch
            {
                0 => "Fully decrypted",
                1 => "Fully encrypted",
                2 => "Encrypting",
                3 => "Decrypting",
                4 => "Encryption paused",
                5 => "Decryption paused",
                _ => null,
            }))
        .ToList();

    /// <summary>The members of the local Administrators group, found by its SID (the name is localised).</summary>
    public static List<LocalAdmin> LocalAdministrators()
    {
        var account = (NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount));
        var group = account.Value.Split('\\').Last();
        return Win32.LocalGroupMembers(group).Select(m => new LocalAdmin(m.Name, m.Kind)).ToList();
    }

    public static List<SessionInfo> Sessions() => Win32.Sessions()
        .Select(s => new SessionInfo(s.User, s.Station.StartsWith("RDP", StringComparison.OrdinalIgnoreCase) ? "Rdp"
            : s.Station.Equals("Console", StringComparison.OrdinalIgnoreCase) ? "Console" : s.Station, s.State, s.ClientAddress, s.LogonAt))
        .ToList();

    private static HypervisorInfo? Hypervisor(System.Management.ManagementBaseObject? computer)
    {
        if (computer is null)
        {
            return null;
        }
        var manufacturer = computer.Str("Manufacturer") ?? "";
        var model = computer.Str("Model") ?? "";
        var vendor = (manufacturer + " " + model) switch
        {
            var m when m.Contains("VMware", StringComparison.OrdinalIgnoreCase) => "VMware",
            var m when m.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase) && manufacturer.Contains("Microsoft") => "Hyper-V",
            var m when m.Contains("QEMU", StringComparison.OrdinalIgnoreCase) || m.Contains("KVM", StringComparison.OrdinalIgnoreCase) => "KVM",
            var m when m.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) || m.Contains("innotek", StringComparison.OrdinalIgnoreCase) => "VirtualBox",
            var m when m.Contains("Xen", StringComparison.OrdinalIgnoreCase) => "Xen",
            var m when m.Contains("Amazon EC2", StringComparison.OrdinalIgnoreCase) => "AWS",
            var m when m.Contains("Google", StringComparison.OrdinalIgnoreCase) => "Google Cloud",
            var m when m.Contains("Parallels", StringComparison.OrdinalIgnoreCase) => "Parallels",
            _ => null,
        };
        return new HypervisorInfo(computer.Bool("HypervisorPresent") ?? vendor is not null, vendor, model.Length > 0 ? model : null);
    }
}
