using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>
/// The Windows Firewall through its COM API, <c>HNetCfg.FwPolicy2</c> (docs/windows-sensor-plan.md §3.1, §4.4): each
/// profile's state and default for inbound connections, and the enabled inbound allow rules.
/// </summary>
internal static class Firewall
{
    private static readonly (int Bit, string Name)[] ProfileBits = [(1, "Domain"), (2, "Private"), (4, "Public")];

    public sealed record Rule(string Name, string? Group, int Protocol, string Ports, int Profiles, string? Program, string? RemoteAddresses);

    private static dynamic Policy() =>
        Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new NotAvailableException("The firewall's COM API is not available."))!;

    public static List<FirewallProfileState> Profiles()
    {
        var policy = Policy();
        int active = policy.CurrentProfileTypes;
        return ProfileBits.Select(p => new FirewallProfileState(p.Name, (bool)policy.FirewallEnabled[p.Bit],
            (int)policy.DefaultInboundAction[p.Bit] == 0 ? "Block" : "Allow", (active & p.Bit) != 0)).ToList();
    }

    /// <summary>Enabled inbound allow rules (direction 1, action 1).</summary>
    public static List<Rule> InboundAllowRules()
    {
        var policy = Policy();
        var rules = new List<Rule>();
        foreach (dynamic rule in policy.Rules)
        {
            try
            {
                if (!(bool)rule.Enabled || (int)rule.Direction != 1 || (int)rule.Action != 1)
                {
                    continue;
                }
                rules.Add(new Rule((string)rule.Name, (string?)rule.Grouping, (int)rule.Protocol, (string?)rule.LocalPorts ?? "*", (int)rule.Profiles,
                    (string?)rule.ApplicationName, (string?)rule.RemoteAddresses));
            }
            catch (Exception e) when (e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException or InvalidCastException)
            {
                // A rule with an unusual shape: skipped.
            }
        }
        return rules;
    }

    public static string ProfileNames(int bits) => bits == 0x7FFFFFFF || bits == 7
        ? "All"
        : string.Join(", ", ProfileBits.Where(p => (bits & p.Bit) != 0).Select(p => p.Name));

    public static string ProtocolName(int protocol) => protocol switch
    {
        6 => "TCP",
        17 => "UDP",
        1 => "ICMPv4",
        58 => "ICMPv6",
        256 => "Any",
        _ => protocol.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>Whether a rule's ports (<c>"*"</c>, <c>"80,443"</c>, <c>"5000-5010"</c>, <c>"RPC"</c>) cover a port.</summary>
    public static bool Covers(Rule rule, string protocol, int port)
    {
        var ruleProtocol = ProtocolName(rule.Protocol);
        if (ruleProtocol != "Any" && ruleProtocol != protocol)
        {
            return false;
        }
        foreach (var part in rule.Ports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "*")
            {
                return true;
            }
            if (part.Split('-') is [var from, var to] && int.TryParse(from, out var f) && int.TryParse(to, out var t) && port >= f && port <= t)
            {
                return true;
            }
            if (int.TryParse(part, out var single) && single == port)
            {
                return true;
            }
            if (part.Equals("RPC-EPMap", StringComparison.OrdinalIgnoreCase) && port == 135)
            {
                return true;
            }
        }
        return false;
    }
}
