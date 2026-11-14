using System.Globalization;
using System.Text.Json;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>A block device as <c>lsblk</c> lists it, with what it holds (partitions, LUKS mappings, LVM volumes).</summary>
internal sealed record BlockDevice(string Name, string? Type, long? Size, string? Model, string? Serial, bool? Rotational, string? Transport, string? FsType,
    string? Label, string? MountPoint, IReadOnlyList<BlockDevice> Children);

/// <summary>
/// <c>lsblk -J -b -p</c> (docs/linux-sensor-plan.md §5.3): the disks with their model, serial, size, media and transport, and the
/// tree under them, for volumes' labels and LUKS. Older util-linux writes numbers and flags as strings, newer as JSON values: both
/// are read.
/// </summary>
internal static class BlockDevices
{
    private const string Columns = "NAME,TYPE,SIZE,MODEL,SERIAL,ROTA,TRAN,FSTYPE,LABEL,MOUNTPOINT";

    public static List<BlockDevice> Read() => Parse(Tool.Text("lsblk", ["-J", "-b", "-p", "-o", Columns], TimeSpan.FromSeconds(15)));

    public static List<BlockDevice> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("blockdevices", out var devices) && devices.ValueKind == JsonValueKind.Array
            ? devices.EnumerateArray().Select(Device).ToList()
            : [];
    }

    private static BlockDevice Device(JsonElement e) => new(
        Text(e, "name") ?? "?",
        Text(e, "type"),
        Number(e, "size"),
        Text(e, "model")?.Trim(),
        Text(e, "serial")?.Trim(),
        Flag(e, "rota"),
        Text(e, "tran"),
        Text(e, "fstype"),
        Text(e, "label"),
        Text(e, "mountpoint"),
        e.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array ? children.EnumerateArray().Select(Device).ToList() : []);

    public static IEnumerable<BlockDevice> All(IEnumerable<BlockDevice> devices) => devices.SelectMany(d => All(d.Children).Prepend(d));

    /// <summary>LUKS volumes (§5.2): each crypto_LUKS device and where what it holds is mounted (through LVM too).</summary>
    public static List<EncryptedVolume> Encrypted(IEnumerable<BlockDevice> devices) => All(devices)
        .Where(d => d.FsType == "crypto_LUKS")
        .Select(d => new EncryptedVolume(d.Name, All(d.Children).Select(c => c.MountPoint).FirstOrDefault(m => !string.IsNullOrEmpty(m) && m != "[SWAP]"), "LUKS"))
        .DistinctBy(v => v.Device)
        .ToList();

    /// <summary>The mount points that lie on a LUKS device, for the volumes' <c>Encryption</c>.</summary>
    public static HashSet<string> EncryptedMounts(IEnumerable<BlockDevice> devices) => All(devices)
        .Where(d => d.FsType == "crypto_LUKS")
        .SelectMany(d => All(d.Children))
        .Select(c => c.MountPoint).Where(m => !string.IsNullOrEmpty(m)).Select(m => m!)
        .ToHashSet(StringComparer.Ordinal);

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    } : null;

    private static long? Number(JsonElement e, string name) =>
        Text(e, name) is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static bool? Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => v.GetString() switch { "1" => true, "0" => false, _ => null },
        JsonValueKind.Number => v.GetRawText() != "0",
        _ => null,
    } : null;
}
