using System.Text.Json.Serialization;

namespace PNMTD.Lib.Network;

/// <summary>What a connection is made of (docs/network-map-plan.md §3.2).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectionMedium>))]
public enum ConnectionMedium { BaseT, Dac, Aoc, FiberMmf, FiberSmf, Wifi, WireGuard, Tunnel, VirtualMachine, Other }

/// <summary>One type: its code (stored), label, medium, speed in Mbit/s (null for the virtual ones), and whether it is physical.</summary>
public sealed record ConnectionType(string Code, string Label, ConnectionMedium Medium, int? SpeedMbps, bool Physical);

/// <summary>
/// The fixed list of connection types (§3.2): each holds the medium and the speed, so a connection has exactly one. Stored by
/// its code; a new type is a code change, with no migration. A code that is no longer here still shows, as its code.
/// </summary>
public static class ConnectionTypes
{
    public const string Wifi = "wifi";
    public const string WireGuard = "wireguard";
    public const string Tunnel = "tunnel";
    public const string VirtualMachine = "vm";
    public const string OtherPhysical = "other-physical";
    public const string OtherVirtual = "other-virtual";

    private static readonly (string Code, string Label, int Mbps)[] Speeds =
    [
        ("10m", "10M", 10), ("100m", "100M", 100), ("1g", "1G", 1_000), ("2.5g", "2.5G", 2_500), ("5g", "5G", 5_000),
        ("10g", "10G", 10_000), ("25g", "25G", 25_000), ("40g", "40G", 40_000), ("50g", "50G", 50_000), ("100g", "100G", 100_000),
    ];

    public static readonly IReadOnlyList<ConnectionType> All = Build();

    private static readonly Dictionary<string, ConnectionType> ByCode = All.ToDictionary(t => t.Code, StringComparer.Ordinal);

    private static List<ConnectionType> Build()
    {
        var list = new List<ConnectionType>();
        void Medium(string prefix, string label, ConnectionMedium medium, params string[] speeds)
        {
            foreach (var (code, speedLabel, mbps) in Speeds.Where(s => speeds.Contains(s.Code)))
            {
                list.Add(new ConnectionType($"{prefix}-{code}", $"{label} {speedLabel}", medium, mbps, true));
            }
        }
        Medium("baset", "BASE-T", ConnectionMedium.BaseT, "10m", "100m", "1g", "2.5g", "5g", "10g");
        Medium("dac", "DAC", ConnectionMedium.Dac, "1g", "10g", "25g", "40g", "100g");
        Medium("aoc", "AOC", ConnectionMedium.Aoc, "10g", "25g", "40g", "100g");
        Medium("mmf", "Fiber MMF", ConnectionMedium.FiberMmf, "1g", "10g", "25g", "40g", "100g");
        Medium("smf", "Fiber SMF", ConnectionMedium.FiberSmf, "1g", "10g", "25g", "40g", "100g");
        list.Add(new ConnectionType(Wifi, "Wi-Fi", ConnectionMedium.Wifi, null, true));
        list.Add(new ConnectionType(WireGuard, "WireGuard", ConnectionMedium.WireGuard, null, false));
        list.Add(new ConnectionType(Tunnel, "Tunnel", ConnectionMedium.Tunnel, null, false));
        list.Add(new ConnectionType(VirtualMachine, "Virtual machine", ConnectionMedium.VirtualMachine, null, false));
        list.Add(new ConnectionType(OtherPhysical, "Other (physical)", ConnectionMedium.Other, null, true));
        list.Add(new ConnectionType(OtherVirtual, "Other (virtual)", ConnectionMedium.Other, null, false));
        return list;
    }

    public static ConnectionType? Find(string? code) => code is not null && ByCode.TryGetValue(code, out var type) ? type : null;

    public static bool IsKnown(string? code) => Find(code) is not null;

    /// <summary>The label, or the code itself for a code that is no longer in the list.</summary>
    public static string Label(string code) => Find(code)?.Label ?? code;

    /// <summary>Physical unless the type says otherwise; an unknown code counts as physical.</summary>
    public static bool IsPhysical(string code) => Find(code)?.Physical ?? true;

    /// <summary>The type of a medium at a speed, or null when the list has no such combination (DAC at 2.5G).</summary>
    public static string? Of(ConnectionMedium medium, int? speedMbps)
    {
        if (speedMbps is null)
        {
            return null;
        }
        return All.FirstOrDefault(t => t.Medium == medium && t.SpeedMbps == speedMbps)?.Code;
    }

    /// <summary>"10G", "100M": a speed as the labels write it.</summary>
    public static string SpeedLabel(int mbps) =>
        Speeds.FirstOrDefault(s => s.Mbps == mbps) is { Label: { } label } ? label : mbps >= 1000 ? $"{mbps / 1000d:0.#}G" : $"{mbps}M";
}
