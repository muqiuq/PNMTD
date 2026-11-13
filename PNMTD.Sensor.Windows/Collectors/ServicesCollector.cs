using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Native;
using PNMTD.Sensor.Windows.Resilience;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// <c>services</c> (docs/windows-sensor-plan.md §4.5): every service with its state, start type, account and binary, and its
/// class by the binary's signature (the ServiceDll for svchost services): Windows, Microsoft, third-party or unsigned. Every
/// minute: the signatures are cached by path, size and time, so only new binaries are checked.
/// </summary>
internal sealed class ServicesCollector : Collector
{
    public override string Name => SectionNames.Services;

    public override TimeSpan Interval => TimeSpan.FromMinutes(1);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "pid" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    private readonly ConcurrentDictionary<string, (long Size, DateTime Written, WinTrust.Signature Signature, string? Version, string? Company)> signatures =
        new(StringComparer.OrdinalIgnoreCase);

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var self = Path.GetFullPath(AgentInfo.InstalledExecutable);
        var services = Wmi.Query(Wmi.Cimv2, "SELECT Name, DisplayName, State, StartMode, StartName, PathName, ProcessId FROM Win32_Service")
            .Select(s =>
            {
                ct.ThrowIfCancellationRequested();
                var name = s.Str("Name") ?? "";
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                var delayed = key?.GetValue("DelayedAutostart") is int d && d == 1;
                var trigger = key?.OpenSubKey("TriggerInfo") is { } triggerKey && Dispose(triggerKey);
                var startMode = s.Str("StartMode") ?? "";
                var startType = startMode switch
                {
                    "Auto" when delayed => "Delayed",
                    "Auto" => "Automatic",
                    "Manual" when trigger => "Trigger",
                    "Manual" => "Manual",
                    "Disabled" => "Disabled",
                    "Boot" or "System" => startMode,
                    _ => startMode,
                };
                var path = Binary(s.Str("PathName"));
                var serviceDll = key?.OpenSubKey("Parameters")?.GetValue("ServiceDll") as string ?? key?.GetValue("ServiceDll") as string;
                serviceDll = serviceDll is null ? null : Environment.ExpandEnvironmentVariables(serviceDll);
                var subject = serviceDll ?? path;
                var (signature, version, company) = subject is null ? (new WinTrust.Signature(false, false, null), null, null) : Signature(subject, errors);
                var serviceClass = Classify(subject, signature, windows, self);
                var state = s.Str("State") ?? "Unknown";
                return new ServiceEntry(name, s.Str("DisplayName") ?? name, state, startType, s.Str("StartName"), path, serviceDll, version, company, signature.Signer,
                    serviceClass, startMode == "Auto" && !trigger && state != "Running", s.Int("ProcessId") is > 0 and var pid ? pid : null);
            })
            .OrderBy(s => s.Class == ServiceClass.Windows ? 1 : 0).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ServicesSection(services);
    }

    private static bool Dispose(RegistryKey key)
    {
        key.Dispose();
        return true;
    }

    private (WinTrust.Signature Signature, string? Version, string? Company) Signature(string path, List<string> errors)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return (new WinTrust.Signature(false, false, null), null, null);
            }
            if (signatures.TryGetValue(path, out var cached) && cached.Size == info.Length && cached.Written == info.LastWriteTimeUtc)
            {
                return (cached.Signature, cached.Version, cached.Company);
            }
            var signature = WinTrust.Check(path);
            var version = FileVersionInfo.GetVersionInfo(path);
            signatures[path] = (info.Length, info.LastWriteTimeUtc, signature, version.FileVersion, version.CompanyName);
            return (signature, version.FileVersion, version.CompanyName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{path}: {e.Message}");
            return (new WinTrust.Signature(false, false, null), null, null);
        }
    }

    /// <summary>The class (§4.5): Windows' own (under %SystemRoot%, signed by Microsoft Windows), Microsoft's, a third party's, or unsigned.</summary>
    private static ServiceClass Classify(string? path, WinTrust.Signature signature, string windows, string self)
    {
        if (path is not null && string.Equals(Path.GetFullPath(path), self, StringComparison.OrdinalIgnoreCase))
        {
            return ServiceClass.Pnmt;
        }
        if (!signature.Valid)
        {
            return ServiceClass.Unsigned;
        }
        var underWindows = path?.StartsWith(windows, StringComparison.OrdinalIgnoreCase) == true;
        var signer = signature.Signer ?? "";
        if (underWindows && (signature.Catalog || signer.StartsWith("Microsoft Windows", StringComparison.OrdinalIgnoreCase)))
        {
            return ServiceClass.Windows;
        }
        return signer.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || signature.Catalog ? ServiceClass.Microsoft : ServiceClass.ThirdParty;
    }

    /// <summary>The executable of a service's command line: quoted, or up to ".exe".</summary>
    public static string? Binary(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }
        var text = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : text.Trim('"');
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        var path = exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[@"\SystemRoot\".Length..]);
        }
        else if (path.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path);
        }
        return path;
    }
}
