using PNMTD.Lib.Agent;
using PNMTD.Sensor.Windows.Native;
using PNMTD.Sensor.Windows.Resilience;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// <c>hardware</c> (docs/windows-sensor-plan.md §4.3): the machine and its firmware, the TPM, CPUs, memory modules,
/// physical disks with their health and SMART, volumes, GPUs and batteries. At boot and every 6 hours.
/// </summary>
internal sealed class HardwareCollector : Collector
{
    public override string Name => SectionNames.Hardware;

    public override TimeSpan Interval => TimeSpan.FromHours(6);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "freeBytes", "chargePercent", "status" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var product = Isolated.Part(errors, "Win32_ComputerSystemProduct", () => Wmi.First(Wmi.Cimv2, "SELECT Vendor, Name, IdentifyingNumber FROM Win32_ComputerSystemProduct"));
        var bios = Isolated.Part(errors, "Win32_BIOS", () => Wmi.First(Wmi.Cimv2, "SELECT SMBIOSBIOSVersion, ReleaseDate, SerialNumber FROM Win32_BIOS"));
        var firmware = Isolated.Part(errors, "firmware type", () => Win32.GetFirmwareType(out var type) ? type switch { 1 => "BIOS", 2 => "UEFI", _ => null } : null);
        var machine = new MachineInfo(product?.Str("Vendor"), product?.Str("Name"), bios?.Str("SerialNumber") ?? product?.Str("IdentifyingNumber"),
            bios?.Str("SMBIOSBIOSVersion"), bios?.Date("ReleaseDate"), firmware);

        var tpm = Isolated.Part(errors, "TPM", () => Wmi.First(Wmi.Tpm, "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm") is { } t
            ? new TpmInfo(true, t.Str("SpecVersion")?.Split(',')[0].Trim(), (t.Bool("IsEnabled_InitialValue") ?? false) && (t.Bool("IsActivated_InitialValue") ?? false))
            : new TpmInfo(false, null, null));

        var processors = Isolated.Part(errors, "Win32_Processor", () => Wmi.Query(Wmi.Cimv2, "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor")
            .Select(p => new ProcessorInfo(p.Str("Name") ?? "CPU", p.Int("NumberOfCores"), p.Int("NumberOfLogicalProcessors"), p.Int("MaxClockSpeed")))
            .ToList()) ?? [];

        var modules = Isolated.Part(errors, "Win32_PhysicalMemory", () => Wmi.Query(Wmi.Cimv2,
                "SELECT DeviceLocator, Capacity, ConfiguredClockSpeed, Speed, Manufacturer, PartNumber FROM Win32_PhysicalMemory")
            .Select(m => new MemoryModule(m.Str("DeviceLocator"), m.Long("Capacity") ?? 0, m.Int("ConfiguredClockSpeed") ?? m.Int("Speed"), m.Str("Manufacturer"),
                m.Str("PartNumber")))
            .ToList()) ?? [];
        var total = (long)Win32.Memory().TotalPhys;

        var predicting = Isolated.Part(errors, "SMART", () => Wmi.Query(Wmi.WmiRoot, "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus")
            .Where(s => s.Bool("PredictFailure") == true).Select(s => s.Str("InstanceName") ?? "").ToList()) ?? [];
        var disks = Isolated.Part(errors, "MSFT_PhysicalDisk", () => Wmi.Query(Wmi.Storage,
                "SELECT DeviceId, FriendlyName, Size, MediaType, BusType, HealthStatus, OperationalStatus, SerialNumber FROM MSFT_PhysicalDisk")
            .Select(d => new DiskInfo(d.Str("DeviceId"), d.Str("FriendlyName") ?? "Disk", d.Long("Size"), Media(d.Int("MediaType"), d.Int("BusType")), Bus(d.Int("BusType")),
                d.Int("HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                string.Join(", ", d.Strings("OperationalStatus").Select(OperationalStatus)),
                predicting.Count == 0 ? false : predicting.Any(p => d.Str("SerialNumber") is { Length: > 0 } serial && p.Contains(serial, StringComparison.OrdinalIgnoreCase)) ? true : null,
                d.Str("SerialNumber")))
            .ToList()) ?? [];

        var bitlocker = Isolated.Part(errors, "BitLocker", SystemCollector.BitLocker)?.ToDictionary(b => b.Volume.TrimEnd('\\'), b => b.Protection,
            StringComparer.OrdinalIgnoreCase) ?? [];
        var volumes = Isolated.Part(errors, "volumes", () => DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new VolumeInfo(d.Name.TrimEnd('\\'), string.IsNullOrEmpty(d.VolumeLabel) ? null : d.VolumeLabel, d.DriveFormat, d.TotalSize, d.TotalFreeSpace,
                bitlocker.GetValueOrDefault(d.Name.TrimEnd('\\'))))
            .ToList()) ?? [];

        var gpus = Isolated.Part(errors, "Win32_VideoController", () => Wmi.Query(Wmi.Cimv2, "SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController")
            .Select(g => new GpuInfo(g.Str("Name") ?? "GPU", g.Str("DriverVersion"), g.Long("AdapterRAM") is > 0 and var ram ? ram : null))
            .ToList()) ?? [];
        var batteries = Isolated.Part(errors, "Win32_Battery", () => Wmi.Query(Wmi.Cimv2, "SELECT Name, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery")
            .Select(b => new BatteryInfo(b.Str("Name") ?? "Battery", b.Int("EstimatedChargeRemaining"),
                b.Int("BatteryStatus") switch { 1 => "Discharging", 2 => "On AC", 3 => "Fully charged", 4 => "Low", 5 => "Critical", 6 or 7 or 8 or 9 => "Charging", _ => null }))
            .ToList()) ?? [];

        return new HardwareSection(machine, tpm, processors, new MemoryInfo(total, modules), disks, volumes, gpus, batteries);
    }

    private static string? Media(int? media, int? bus) => bus == 17 ? "NVMe" : media switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };

    private static string? Bus(int? bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => null,
    };

    private static string OperationalStatus(string code) => code switch
    {
        "2" => "OK",
        "3" => "Degraded",
        "5" => "Predictive failure",
        "6" => "Error",
        "10" => "Stopped",
        "11" => "In service",
        "13" => "Lost communication",
        "53264" => "Online",
        "53265" => "Not ready",
        "53266" => "No media",
        "53267" => "Offline",
        "53268" => "Failed",
        _ => code,
    };
}
