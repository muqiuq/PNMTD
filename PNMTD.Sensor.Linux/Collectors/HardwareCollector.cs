using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Core.Resilience;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>
/// <c>hardware</c> (docs/linux-sensor-plan.md §5.3 → the Windows record, <see cref="HardwareSection"/>): the machine and its firmware
/// (<c>/sys/class/dmi/id</c>, or the device tree), the TPM, the processors (<c>lscpu -J</c>), memory (<c>/proc/meminfo</c>, the modules
/// from <c>dmidecode -t 17</c> when installed), the disks (<c>lsblk</c>, SMART from <c>smartctl -j -H</c> when installed), the real file
/// systems (with LUKS underneath), GPUs from <c>/sys/bus/pci</c> and batteries. At boot and every 6 hours.
/// </summary>
internal sealed partial class HardwareCollector : Collector
{
    public override string Name => SectionNames.Hardware;

    public override TimeSpan Interval => TimeSpan.FromHours(6);

    /// <summary>smartctl asks every disk, a few seconds each.</summary>
    public override TimeSpan Timeout => TimeSpan.FromMinutes(2);

    private static readonly IReadOnlySet<string> VolatileNames = new HashSet<string> { "freeBytes", "chargePercent", "status" };

    public override IReadOnlySet<string> Volatile => VolatileNames;

    /// <summary>What vendors leave in DMI fields they didn't fill.</summary>
    private static readonly string[] Unfilled = ["To Be Filled By O.E.M.", "To be filled by O.E.M.", "Not Specified", "Default string", "System Product Name",
        "System manufacturer", "None", "0", "Not Applicable", "N/A"];

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(B|kB|KB|KiB|MB|MiB|GB|GiB|TB|TiB)$")]
    private static partial Regex Size();

    public override object Collect(List<string> errors, CancellationToken ct)
    {
        var blocks = Isolated.Part(errors, "lsblk", BlockDevices.Read) ?? [];
        var meminfo = Isolated.Part(errors, "/proc/meminfo", () => LinuxFacts.ParseMeminfo(File.ReadAllText("/proc/meminfo")));
        return new HardwareSection(
            Isolated.Part(errors, "machine", Machine) ?? new MachineInfo(null, null, null, null, null, null),
            Isolated.Part(errors, "TPM", Tpm),
            Isolated.Part(errors, "processors", Processors) ?? [],
            new MemoryInfo(meminfo?.GetValueOrDefault("MemTotal") ?? 0, Isolated.Part(errors, "memory modules", MemoryModules) ?? []),
            Isolated.Part(errors, "disks", () => Disks(blocks, errors, ct)) ?? [],
            Isolated.Part(errors, "volumes", () => Volumes(blocks)) ?? [],
            Isolated.Part(errors, "GPUs", Gpus) ?? [],
            Isolated.Part(errors, "batteries", Batteries) ?? []);
    }

    /// <summary>DMI's vendor, product, serial and BIOS (the serial is root's to read); on boards without DMI the device tree's model and serial.</summary>
    private static MachineInfo Machine()
    {
        const string dmi = "/sys/class/dmi/id";
        var firmware = Directory.Exists("/sys/firmware/efi") ? "UEFI" : Directory.Exists(dmi) ? "BIOS" : null;
        if (!Directory.Exists(dmi))
        {
            var model = Tool.ReadValue("/proc/device-tree/model")?.TrimEnd('\0');
            var serial = Tool.ReadValue("/sys/firmware/devicetree/base/serial-number")?.TrimEnd('\0');
            return new MachineInfo(null, model, serial, null, null, firmware);
        }
        string? Read(string name) => Filled(Tool.ReadValue(Path.Combine(dmi, name)));
        return new MachineInfo(Read("sys_vendor"), Read("product_name"), Read("product_serial") ?? Read("board_serial"), Read("bios_version"),
            DateTimeOffset.TryParseExact(Read("bios_date"), "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null,
            firmware);
    }

    public static string? Filled(string? value) => value is null || Unfilled.Contains(value.Trim(), StringComparer.Ordinal) ? null : value.Trim();

    /// <summary><c>/sys/class/tpm/tpm0</c> and its major version.</summary>
    private static TpmInfo Tpm()
    {
        if (!Directory.Exists("/sys/class/tpm/tpm0"))
        {
            return new TpmInfo(false, null, null);
        }
        var major = Tool.ReadValue("/sys/class/tpm/tpm0/tpm_version_major");
        return new TpmInfo(true, major is null ? null : $"{major}.0", null);
    }

    // --- processors ---------------------------------------------------------------------------------------------------

    private static List<ProcessorInfo> Processors()
    {
        if (Tool.Exists("lscpu"))
        {
            return ParseLscpu(Tool.Text("lscpu", ["-J"], TimeSpan.FromSeconds(10)), Tool.ReadFile("/proc/cpuinfo"));
        }
        var cpuinfo = Tool.ReadFile("/proc/cpuinfo") ?? throw new NotAvailableException("neither lscpu nor /proc/cpuinfo");
        var threads = cpuinfo.Split('\n').Count(l => l.StartsWith("processor", StringComparison.Ordinal));
        return [new ProcessorInfo(CpuinfoName(cpuinfo) ?? "?", null, threads, null)];
    }

    /// <summary>
    /// <c>lscpu -J</c>'s fields (flat in older util-linux, nested from 2.37): one entry per socket with its model, cores and threads, and
    /// the maximum clock. ARM reports cores per cluster, and "-" where it doesn't know; the name then comes from /proc/cpuinfo.
    /// </summary>
    public static List<ProcessorInfo> ParseLscpu(string json, string? cpuinfo)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var document = JsonDocument.Parse(json))
        {
            if (document.RootElement.TryGetProperty("lscpu", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                Flatten(list, fields);
            }
        }
        string? Field(params string[] names) => names.Select(n => fields.GetValueOrDefault(n)).FirstOrDefault(v => v is not null && v != "-");
        int? Int(params string[] names) => int.TryParse(Field(names), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        var sockets = Int("Socket(s):") ?? Int("Cluster(s):") ?? 1;
        var cores = Int("Core(s) per socket:") ?? Int("Core(s) per cluster:");
        var perCore = Int("Thread(s) per core:") ?? 1;
        var total = Int("CPU(s):");
        cores ??= total is { } t ? t / Math.Max(1, sockets * perCore) : null;
        // ARM's "Model:" is a revision number, not a name: the vendor alone then (Apple, ARM, Ampere).
        var name = Field("Model name:") ?? (cpuinfo is null ? null : CpuinfoName(cpuinfo)) ?? Field("Vendor ID:") ?? "?";
        var maxMhz = double.TryParse(Field("CPU max MHz:"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz) ? (int?)Math.Round(mhz) : null;
        return Enumerable.Range(0, Math.Clamp(sockets, 1, 16)).Select(_ => new ProcessorInfo(name, cores, cores * perCore, maxMhz)).ToList();
    }

    private static void Flatten(JsonElement list, Dictionary<string, string> fields)
    {
        foreach (var item in list.EnumerateArray())
        {
            if (item.TryGetProperty("field", out var field) && item.TryGetProperty("data", out var data) && field.GetString() is { } name)
            {
                fields.TryAdd(name, data.ValueKind == JsonValueKind.String ? data.GetString() ?? "" : data.GetRawText());
            }
            if (item.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                Flatten(children, fields);
            }
        }
    }

    private static string? CpuinfoName(string cpuinfo) => cpuinfo.Split('\n')
        .Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
        .FirstOrDefault(p => p[0].Trim() is "model name" or "Model" or "Hardware" or "cpu model")?[1].Trim();

    // --- memory -------------------------------------------------------------------------------------------------------

    /// <summary>The modules from <c>dmidecode -t 17</c>, when it is installed and the firmware has a DMI table.</summary>
    private static List<MemoryModule> MemoryModules()
    {
        if (!Tool.Exists("dmidecode"))
        {
            throw new NotAvailableException("dmidecode is not installed");
        }
        var result = Tool.Run("dmidecode", ["-t", "17"], TimeSpan.FromSeconds(15));
        if (!result.Ok || result.Output.Contains("No SMBIOS nor DMI entry point found", StringComparison.Ordinal))
        {
            throw new NotAvailableException($"dmidecode: {result.Reason}");
        }
        return ParseDmidecodeMemory(result.Output);
    }

    /// <summary><c>dmidecode -t 17</c>'s "Memory Device" blocks: the slot, size, speed, maker and part number of each installed module.</summary>
    public static List<MemoryModule> ParseDmidecodeMemory(string text)
    {
        var modules = new List<MemoryModule>();
        foreach (var block in text.Replace("\r", "").Split("\n\n"))
        {
            if (!block.Contains("Memory Device", StringComparison.Ordinal))
            {
                continue;
            }
            var values = block.Split('\n').Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.Ordinal);
            if (Bytes(values.GetValueOrDefault("Size")) is not { } size)
            {
                continue;
            }
            var speed = values.GetValueOrDefault("Configured Memory Speed") is { } configured && Mhz(configured) is { } c ? c : Mhz(values.GetValueOrDefault("Speed"));
            modules.Add(new MemoryModule(values.GetValueOrDefault("Locator"), size, speed, Filled(values.GetValueOrDefault("Manufacturer")),
                Filled(values.GetValueOrDefault("Part Number"))));
        }
        return modules;
    }

    private static long? Bytes(string? text)
    {
        if (text is null || Size().Match(text.Trim()) is not { Success: true } m)
        {
            return null;
        }
        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var factor = m.Groups[2].Value switch
        {
            "kB" or "KB" or "KiB" => 1024L,
            "MB" or "MiB" => 1024L * 1024,
            "GB" or "GiB" => 1024L * 1024 * 1024,
            "TB" or "TiB" => 1024L * 1024 * 1024 * 1024,
            _ => 1L,
        };
        return (long)(value * factor);
    }

    private static int? Mhz(string? text) =>
        text?.Split(' ')[0] is { } number && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    // --- disks and volumes --------------------------------------------------------------------------------------------

    /// <summary>The disks (not loop, RAM or zram devices): model, size, media (NVMe, HDD by rotation, else SSD), bus, and SMART's verdict.</summary>
    private static List<DiskInfo> Disks(IReadOnlyList<BlockDevice> blocks, List<string> errors, CancellationToken ct)
    {
        var smart = Tool.Exists("smartctl");
        return blocks.Where(d => d.Type == "disk" && !Path.GetFileName(d.Name).StartsWith("zram", StringComparison.Ordinal)
                && !Path.GetFileName(d.Name).StartsWith("ram", StringComparison.Ordinal) && !Path.GetFileName(d.Name).StartsWith("loop", StringComparison.Ordinal))
            .Take(32)
            .Select(d =>
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(d.Name);
                var (health, failing) = smart ? Isolated.Part(errors, $"SMART {name}", () => Smart(d.Name)) : (null, null);
                var media = name.StartsWith("nvme", StringComparison.Ordinal) ? "NVMe" : d.Rotational switch { true => "HDD", false => "SSD", _ => null };
                var bus = d.Transport ?? (name.StartsWith("vd", StringComparison.Ordinal) ? "virtio" : name.StartsWith("xvd", StringComparison.Ordinal) ? "xen" : null);
                var model = d.Model ?? bus switch { "virtio" => "VirtIO disk", "xen" => "Xen disk", _ => name };
                return new DiskInfo(name, model, d.Size, media, bus, health, null, failing, d.Serial);
            })
            .ToList();
    }

    /// <summary><c>smartctl -j -H</c>: passed or failing; nothing when the disk has no SMART (a VM's, most USB bridges).</summary>
    private static (string? Health, bool? Failing) Smart(string device)
    {
        var result = Tool.Run("smartctl", ["-j", "-H", device], TimeSpan.FromSeconds(20));
        return ParseSmart(result.Output);
    }

    public static (string? Health, bool? Failing) ParseSmart(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("smart_status", out var status) && status.TryGetProperty("passed", out var passed)
                && passed.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return passed.GetBoolean() ? ("Healthy", false) : ("Failing", true);
            }
        }
        catch (JsonException)
        {
        }
        return (null, null);
    }

    /// <summary>The real file systems (§5.3): mount point, label, type, size, free space, and LUKS underneath.</summary>
    private static List<VolumeInfo> Volumes(IReadOnlyList<BlockDevice> blocks)
    {
        var encrypted = BlockDevices.EncryptedMounts(blocks);
        var labels = BlockDevices.All(blocks).Where(b => b.MountPoint is not null && b.Label is not null)
            .GroupBy(b => b.MountPoint!).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);
        return LinuxFacts.RealMounts().Select(m =>
        {
            var usage = LinuxFacts.Usage(m.MountPoint);
            var label = labels.GetValueOrDefault(m.MountPoint) ?? (m.Type == "zfs" ? m.Device.Split('/')[0] : null);
            return new VolumeInfo(m.MountPoint, label, m.Type, usage?.Size, usage?.Available, encrypted.Contains(m.MountPoint) ? "LUKS" : null);
        }).ToList();
    }

    // --- GPUs and batteries -------------------------------------------------------------------------------------------

    /// <summary>PCI display controllers (class 0x03): the name from pci.ids when it is there, the driver and its version.</summary>
    private static List<GpuInfo> Gpus()
    {
        const string pci = "/sys/bus/pci/devices";
        if (!Directory.Exists(pci))
        {
            return [];
        }
        var gpus = new List<GpuInfo>();
        foreach (var device in Directory.GetDirectories(pci).Order(StringComparer.Ordinal))
        {
            if (Tool.ReadValue(Path.Combine(device, "class"))?.StartsWith("0x03", StringComparison.Ordinal) != true)
            {
                continue;
            }
            var vendor = Tool.ReadValue(Path.Combine(device, "vendor"))?.Replace("0x", "", StringComparison.Ordinal) ?? "?";
            var model = Tool.ReadValue(Path.Combine(device, "device"))?.Replace("0x", "", StringComparison.Ordinal) ?? "?";
            var driver = new DirectoryInfo(Path.Combine(device, "driver")).LinkTarget is { } target ? Path.GetFileName(target) : null;
            gpus.Add(new GpuInfo(PciName(vendor, model) ?? $"PCI display controller {vendor}:{model}",
                driver is null ? null : Tool.ReadValue($"/sys/module/{driver}/version") ?? driver, null));
        }
        return gpus;
    }

    /// <summary>"Vendor Device" from pci.ids (pciutils or hwdata), or null.</summary>
    private static string? PciName(string vendor, string device)
    {
        var file = new[] { "/usr/share/misc/pci.ids", "/usr/share/hwdata/pci.ids" }.FirstOrDefault(File.Exists);
        if (file is null)
        {
            return null;
        }
        string? vendorName = null;
        foreach (var line in File.ReadLines(file))
        {
            if (vendorName is null)
            {
                if (line.StartsWith(vendor + "  ", StringComparison.OrdinalIgnoreCase))
                {
                    vendorName = line[(vendor.Length + 2)..].Trim();
                }
                continue;
            }
            if (line.Length > 0 && line[0] != '\t' && line[0] != '#')
            {
                return vendorName;
            }
            if (line.StartsWith("\t" + device + "  ", StringComparison.OrdinalIgnoreCase))
            {
                return $"{vendorName} {line[(device.Length + 3)..].Trim()}";
            }
        }
        return vendorName;
    }

    /// <summary>/sys/class/power_supply's batteries: charge and status.</summary>
    private static List<BatteryInfo> Batteries()
    {
        const string supplies = "/sys/class/power_supply";
        if (!Directory.Exists(supplies))
        {
            return [];
        }
        return Directory.GetDirectories(supplies).Order(StringComparer.Ordinal)
            .Where(d => Tool.ReadValue(Path.Combine(d, "type")) == "Battery")
            .Select(d => new BatteryInfo(
                string.Join(' ', new[] { Path.GetFileName(d), Tool.ReadValue(Path.Combine(d, "model_name")) }.Where(v => v is not null)),
                int.TryParse(Tool.ReadValue(Path.Combine(d, "capacity")), NumberStyles.None, CultureInfo.InvariantCulture, out var charge) ? charge : null,
                Tool.ReadValue(Path.Combine(d, "status"))))
            .ToList();
    }
}
