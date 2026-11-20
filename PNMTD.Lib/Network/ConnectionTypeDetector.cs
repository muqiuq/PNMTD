using System.Globalization;
using System.Text.RegularExpressions;

namespace PNMTD.Lib.Network;

/// <summary>
/// What the scan read about one interface (docs/network-map-plan.md §3.3): its RouterOS type and name, and from
/// <c>/interface ethernet monitor</c> the link, its rate and the SFP module. Values a port doesn't have are null.
/// </summary>
public sealed record PortFacts
{
    /// <summary>RouterOS' interface type: <c>ether</c>, <c>wg</c>, <c>vlan</c>, <c>bridge</c>, <c>wifi</c>, <c>eoip</c>, …</summary>
    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? DefaultName { get; init; }

    /// <summary>The monitor's <c>rate</c>, as "1Gbps", "100Mbps" or "2.5Gbps".</summary>
    public string? Rate { get; init; }

    public bool? LinkUp { get; init; }

    public bool? FullDuplex { get; init; }

    public bool? SfpModulePresent { get; init; }

    public string? SfpType { get; init; }

    /// <summary><c>sfp-connector-type</c>: LC, SC, MPO, RJ45, copper-pigtail, no-separable-connector, …</summary>
    public string? SfpConnector { get; init; }

    /// <summary><c>sfp-wavelength</c>, as "850nm".</summary>
    public string? SfpWavelength { get; init; }

    public string? SfpVendor { get; init; }

    public string? SfpPartNumber { get; init; }

    /// <summary>The <c>sfp-link-length-*</c> values that are there: "sm", "om1" … "om4", "copper".</summary>
    public IReadOnlyDictionary<string, string> SfpLinkLengths { get; init; } = new Dictionary<string, string>();

    /// <summary>Ethernet compliance codes, where the version shows them ("10GBASE-SR").</summary>
    public string? SfpCompliance { get; init; }
}

/// <summary>
/// Proposes a connection type for a router's interface (§3.3). Only a proposal: a person can always change it, and an unclear
/// port gives none. DAC and AOC are told apart from optics by the connector and the wavelength (N0 records real modules).
/// </summary>
public static partial class ConnectionTypeDetector
{
    private static readonly string[] WifiTypes = ["wifi", "wlan", "wireless", "wifiwave2", "wifi-qcom", "wifi-qcom-ac", "cap"];

    private static readonly string[] TunnelTypes =
        ["eoip", "eoip-tunnel", "eoipv6", "gre", "gre-tunnel", "gre6-tunnel", "ipip", "ipip-tunnel", "ipipv6-tunnel", "vxlan", "6to4-tunnel",
         "l2tp-out", "l2tp-in", "l2tp-ether", "sstp-out", "sstp-in", "pptp-out", "pptp-in", "ovpn-out", "ovpn-in", "pppoe-out", "pppoe-in", "vpls"];

    private static readonly string[] SeparableOptical = ["lc", "sc", "mpo", "mtrj", "mt-rj", "st", "fc", "mu", "cs", "sn"];

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*([KMG])bps\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RateText();

    [GeneratedRegex(@"(\d{3,4}(?:\.\d+)?)\s*nm", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Wavelength();

    /// <summary>The proposed type's code, or null.</summary>
    public static string? Detect(PortFacts port)
    {
        var type = (port.Type ?? "").Trim().ToLowerInvariant();
        if (WifiTypes.Contains(type))
        {
            return ConnectionTypes.Wifi;
        }
        if (type == "wg")
        {
            return ConnectionTypes.WireGuard;
        }
        if (TunnelTypes.Contains(type))
        {
            return ConnectionTypes.Tunnel;
        }
        if (type is not ("ether" or ""))
        {
            // A bridge, VLAN or bond is no connection of its own: the connection is on the physical port (§3.3).
            return null;
        }
        return Medium(port) is { } medium ? ConnectionTypes.Of(medium, RateMbps(port.Rate)) : null;
    }

    /// <summary>The medium of an Ethernet port, from its module; null when it can't be told.</summary>
    public static ConnectionMedium? Medium(PortFacts port)
    {
        if (port.SfpModulePresent == true)
        {
            var connector = Normalize(port.SfpConnector);
            var nm = WavelengthNm(port.SfpWavelength);
            var lengths = port.SfpLinkLengths.Where(l => !IsZero(l.Value)).Select(l => l.Key.ToLowerInvariant()).ToHashSet();
            var compliance = (port.SfpCompliance ?? "").ToUpperInvariant();
            if (connector.Contains("rj45") || Regex.IsMatch(compliance, @"BASE-?T\b"))
            {
                return ConnectionMedium.BaseT;
            }
            if (connector.Contains("copper") || connector.Contains("pigtail") || (compliance.Contains("CR") && !compliance.Contains("SR"))
                || compliance.Contains("DAC") || (lengths.Contains("copper") && nm is null))
            {
                return ConnectionMedium.Dac;
            }
            var separable = SeparableOptical.Any(c => connector == c || connector.StartsWith(c + "-", StringComparison.Ordinal));
            var multiMode = nm is >= 780 and <= 910 || lengths.Any(l => l.StartsWith("om", StringComparison.Ordinal)) || compliance.Contains("SR") || compliance.Contains("SX");
            var singleMode = nm is >= 1260 and <= 1650 || lengths.Contains("sm") || compliance.Contains("LR") || compliance.Contains("ER")
                || compliance.Contains("ZR") || compliance.Contains("LX") || compliance.Contains("BX");
            if (!separable && connector.Contains("no-separable") && (nm is not null || multiMode))
            {
                return ConnectionMedium.Aoc;
            }
            if (singleMode && !multiMode)
            {
                return ConnectionMedium.FiberSmf;
            }
            if (multiMode)
            {
                return ConnectionMedium.FiberMmf;
            }
            return null;
        }
        var name = (port.DefaultName ?? port.Name ?? "").ToLowerInvariant();
        if (name.StartsWith("sfp", StringComparison.Ordinal) || name.StartsWith("qsfp", StringComparison.Ordinal))
        {
            // An empty cage, or a version that doesn't say: unclear.
            return null;
        }
        return ConnectionMedium.BaseT;
    }

    /// <summary>"1Gbps" → 1000, "2.5Gbps" → 2500, "100Mbps" → 100.</summary>
    public static int? RateMbps(string? rate)
    {
        if (rate is null || RateText().Match(rate) is not { Success: true } m)
        {
            return null;
        }
        var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var factor = char.ToUpperInvariant(m.Groups[2].Value[0]) switch { 'G' => 1000d, 'M' => 1d, _ => 0.001d };
        var mbps = (int)Math.Round(value * factor);
        return mbps > 0 ? mbps : null;
    }

    public static int? WavelengthNm(string? text) =>
        text is not null && Wavelength().Match(text) is { Success: true } m ? (int)Math.Round(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) : null;

    private static string Normalize(string? text) => (text ?? "").Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

    private static bool IsZero(string value) => string.IsNullOrWhiteSpace(value) || value.Trim() is "0" or "0m" or "0km";
}
