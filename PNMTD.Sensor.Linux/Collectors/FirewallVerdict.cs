using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PNMTD.Sensor.Core.Collectors;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Collectors;

/// <summary>What the firewall does with a new connection to a port: let it in from anywhere, filter it, or can't be told.</summary>
internal sealed record PortVerdict(string Verdict, IReadOnlyList<string> OnlyFrom)
{
    public const string Open = "open";
    public const string Filtered = "filtered";
    public const string Unknown = "unknown";

    public static readonly PortVerdict IsOpen = new(Open, []);
    public static readonly PortVerdict IsFiltered = new(Filtered, []);
    public static readonly PortVerdict IsUnknown = new(Unknown, []);
}

/// <summary>
/// The firewall's verdict per port (docs/linux-sensor-plan.md §5.5): for ufw and for simple nftables and iptables input chains, the default
/// policy plus the accept and drop rules on the port; <b>anything more complex says unknown rather than guess</b>: firewalld, Proxmox VE's
/// own firewall, named sets, marks, iptables-nft's <c>xt</c> matches. A rule that admits only some sources makes the port <i>filtered</i>,
/// with those sources. The evaluation is pure: <see cref="Read"/> reads the rules once per collection.
/// </summary>
internal sealed class FirewallVerdict
{
    private readonly Func<string, int, PortVerdict> verdict;

    private FirewallVerdict(string kind, Func<string, int, PortVerdict> verdict, string? note = null)
    {
        Kind = kind;
        this.verdict = verdict;
        Note = note;
    }

    /// <summary>ufw, nftables, iptables, firewalld, proxmox, none, or unknown (the rules can't be read here).</summary>
    public string Kind { get; }

    public string? Note { get; }

    /// <summary>The verdict for a new connection to the port, <c>tcp</c> or <c>udp</c>.</summary>
    public PortVerdict For(string protocol, int port) => verdict(protocol.ToLowerInvariant(), port);

    public static FirewallVerdict Fixed(string kind, PortVerdict always, string? note = null) => new(kind, (_, _) => always, note);

    public static FirewallVerdict Read()
    {
        if (LinuxOs.IsProxmox() && ProxmoxFirewallOn())
        {
            return Fixed("proxmox", PortVerdict.IsUnknown, "Proxmox VE's firewall is on: its rules are not evaluated");
        }
        if (Tool.Exists("ufw"))
        {
            var status = Tool.Run("ufw", ["status", "verbose"], TimeSpan.FromSeconds(15));
            if (status.Ok && status.Output.Contains("Status: active", StringComparison.Ordinal))
            {
                return Ufw(status.Output);
            }
        }
        if (Tool.Exists("firewall-cmd") && IsActive("firewalld.service"))
        {
            return Fixed("firewalld", PortVerdict.IsUnknown, "firewalld's zones are not evaluated");
        }
        if (Tool.Exists("nft"))
        {
            var ruleset = Tool.Run("nft", ["-j", "list", "ruleset"], TimeSpan.FromSeconds(15));
            if (!ruleset.Ok)
            {
                return Fixed("unknown", PortVerdict.IsUnknown, $"nft: {ruleset.Reason}");
            }
            var nft = Nft(ruleset.Output);
            if (nft is not null)
            {
                return nft;
            }
        }
        if (Tool.Exists("iptables-legacy") || !Tool.Exists("nft") && Tool.Exists("iptables"))
        {
            var rules = Tool.Run(Tool.Exists("iptables-legacy") ? "iptables-legacy" : "iptables", ["-S", "INPUT"], TimeSpan.FromSeconds(15));
            if (rules.Ok && Iptables(rules.Output) is { } iptables)
            {
                return iptables;
            }
        }
        return Fixed("none", PortVerdict.IsOpen);
    }

    private static bool IsActive(string unit)
    {
        try
        {
            return Tool.Run("systemctl", ["is-active", unit], TimeSpan.FromSeconds(10)).Output.Trim() == "active";
        }
        catch (NotAvailableException)
        {
            return false;
        }
    }

    /// <summary>pve-firewall runs and the cluster's firewall is enabled (<c>/etc/pve/firewall/cluster.fw</c>: <c>enable: 1</c>).</summary>
    private static bool ProxmoxFirewallOn() =>
        (IsActive("pve-firewall.service") || IsActive("proxmox-firewall.service"))
        && Regex.IsMatch(Tool.ReadFile("/etc/pve/firewall/cluster.fw") ?? "", @"^\s*enable:\s*1", RegexOptions.Multiline);

    // --- ufw ----------------------------------------------------------------------------------------------------

    /// <summary>ufw's application profiles that name a port the agent knows without asking ufw.</summary>
    private static readonly Dictionary<string, (string Protocol, int Port)[]> UfwApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenSSH"] = [("tcp", 22)],
        ["Nginx HTTP"] = [("tcp", 80)],
        ["Nginx HTTPS"] = [("tcp", 443)],
        ["Nginx Full"] = [("tcp", 80), ("tcp", 443)],
        ["Apache"] = [("tcp", 80)],
        ["Apache Secure"] = [("tcp", 443)],
        ["Apache Full"] = [("tcp", 80), ("tcp", 443)],
    };

    /// <summary>
    /// <c>ufw status verbose</c>: the default for incoming, then the rules in order, the first that matches wins (ufw's order). An unknown
    /// application profile before a match makes it unknown.
    /// </summary>
    public static FirewallVerdict Ufw(string status)
    {
        var policy = Regex.Match(status, @"Default:\s*(\w+)\s*\(incoming\)") is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : "deny";
        var rules = new List<(string To, string Action, string From)>();
        var inTable = false;
        foreach (var raw in status.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }
            if (!inTable || line.Trim().Length == 0)
            {
                continue;
            }
            var match = Regex.Match(line, @"^(?<to>.+?)\s{2,}(?<action>(ALLOW|DENY|REJECT|LIMIT)( IN| OUT| FWD)?)\s{2,}(?<from>.+?)\s*(#.*)?$");
            if (match.Success)
            {
                rules.Add((match.Groups["to"].Value.Trim(), match.Groups["action"].Value.Trim(), match.Groups["from"].Value.Trim()));
            }
        }
        return new FirewallVerdict("ufw", (protocol, port) =>
        {
            var onlyFrom = new List<string>();
            foreach (var (to, action, from) in rules)
            {
                if (action.EndsWith(" OUT", StringComparison.Ordinal) || action.EndsWith(" FWD", StringComparison.Ordinal))
                {
                    continue;
                }
                var target = to.Replace(" (v6)", "", StringComparison.Ordinal);
                bool? matches;
                if (target == "Anywhere")
                {
                    matches = true;
                }
                else if (UfwApps.TryGetValue(target, out var app))
                {
                    matches = app.Any(a => a.Protocol == protocol && a.Port == port);
                }
                else if (Regex.Match(target, @"^(?:(?<addr>\S+) )?(?<ports>[0-9,:]+)(?:/(?<proto>tcp|udp))?$") is { Success: true } spec)
                {
                    matches = (!spec.Groups["proto"].Success || spec.Groups["proto"].Value == protocol) && InPorts(spec.Groups["ports"].Value, port);
                }
                else if (Regex.IsMatch(target, @"^[0-9a-fA-F.:/]+$"))
                {
                    // A destination address only: every port to it.
                    matches = true;
                }
                else
                {
                    matches = null;
                }
                if (matches is null)
                {
                    return PortVerdict.IsUnknown;
                }
                if (matches == false)
                {
                    continue;
                }
                var anywhere = from.StartsWith("Anywhere", StringComparison.Ordinal);
                var allow = action.StartsWith("ALLOW", StringComparison.Ordinal) || action.StartsWith("LIMIT", StringComparison.Ordinal);
                if (anywhere)
                {
                    return allow ? PortVerdict.IsOpen : onlyFrom.Count > 0 ? new PortVerdict(PortVerdict.Filtered, onlyFrom) : PortVerdict.IsFiltered;
                }
                if (allow)
                {
                    onlyFrom.Add(from.Replace(" (v6)", "", StringComparison.Ordinal));
                }
            }
            return policy == "allow" ? PortVerdict.IsOpen : new PortVerdict(PortVerdict.Filtered, onlyFrom.Distinct().ToList());
        });
    }

    /// <summary><c>22</c>, <c>22,80</c>, <c>60000:61000</c>.</summary>
    private static bool InPorts(string list, int port) => list.Split(',').Any(part =>
    {
        var range = part.Split(':');
        return range.Length == 2
            ? int.TryParse(range[0], out var low) && int.TryParse(range[1], out var high) && port >= low && port <= high
            : int.TryParse(part, out var single) && single == port;
    });

    // --- nftables -----------------------------------------------------------------------------------------------

    private sealed record NftChain(string Family, string Table, string Name, string? Hook, string? Policy, List<JsonElement> Rules);

    /// <summary>
    /// <c>nft -j list ruleset</c>: every base chain that hooks input, evaluated for IPv4 (inet and ip tables) and IPv6 (inet and ip6). A
    /// connection must pass all of a family's chains; the port is open when it is open for either family. Null when nothing hooks input.
    /// </summary>
    public static FirewallVerdict? Nft(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Fixed("unknown", PortVerdict.IsUnknown, "nft's ruleset could not be read");
        }
        var chains = new List<NftChain>();
        if (!document.RootElement.TryGetProperty("nftables", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("chain", out var c))
            {
                chains.Add(new NftChain(Str(c, "family") ?? "", Str(c, "table") ?? "", Str(c, "name") ?? "", Str(c, "hook"), Str(c, "policy"), []));
            }
        }
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("rule", out var r)
                && chains.FirstOrDefault(c => c.Family == Str(r, "family") && c.Table == Str(r, "table") && c.Name == Str(r, "chain")) is { } chain)
            {
                chain.Rules.Add(r.Clone());
            }
        }
        var inputs = chains.Where(c => c.Hook == "input" && c.Family is "inet" or "ip" or "ip6").ToList();
        if (inputs.Count == 0)
        {
            return null;
        }
        return new FirewallVerdict("nftables", (protocol, port) =>
        {
            PortVerdict Family(string family)
            {
                var relevant = inputs.Where(c => c.Family == "inet" || c.Family == family).ToList();
                var combined = PortVerdict.IsOpen;
                var onlyFrom = new List<string>();
                foreach (var chain in relevant)
                {
                    var v = EvaluateChain(chain, chains, protocol, port, family, 0, onlyFrom) ?? (chain.Policy == "drop" ? PortVerdict.IsFiltered : PortVerdict.IsOpen);
                    if (v.Verdict == PortVerdict.Unknown)
                    {
                        return PortVerdict.IsUnknown;
                    }
                    if (v.Verdict == PortVerdict.Filtered)
                    {
                        combined = PortVerdict.IsFiltered;
                    }
                }
                return combined.Verdict == PortVerdict.Filtered && onlyFrom.Count > 0 ? new PortVerdict(PortVerdict.Filtered, onlyFrom.Distinct().ToList()) : combined;
            }
            var v4 = Family("ip");
            var v6 = Family("ip6");
            return v4.Verdict == PortVerdict.Open || v6.Verdict == PortVerdict.Open ? PortVerdict.IsOpen
                : v4.Verdict == PortVerdict.Unknown || v6.Verdict == PortVerdict.Unknown ? PortVerdict.IsUnknown
                : new PortVerdict(PortVerdict.Filtered, v4.OnlyFrom.Concat(v6.OnlyFrom).Distinct().ToList());
        });
    }

    /// <summary>A chain's rules in order: the verdict of the first that decides, null when it ends without one (the policy, or back to the caller).</summary>
    private static PortVerdict? EvaluateChain(NftChain chain, List<NftChain> all, string protocol, int port, string family, int depth, List<string> onlyFrom)
    {
        if (depth > 4)
        {
            return PortVerdict.IsUnknown;
        }
        foreach (var rule in chain.Rules)
        {
            if (!rule.TryGetProperty("expr", out var expressions) || expressions.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            bool? matches = true;
            string? restricted = null;
            string? verdict = null;
            string? target = null;
            foreach (var e in expressions.EnumerateArray())
            {
                if (e.TryGetProperty("match", out var match))
                {
                    var m = Match(match, protocol, port, family, out var source);
                    restricted ??= source;
                    if (m is null)
                    {
                        matches = null;
                    }
                    else if (m == false && matches is not null)
                    {
                        matches = false;
                    }
                }
                else if (e.TryGetProperty("accept", out _))
                {
                    verdict = "accept";
                }
                else if (e.TryGetProperty("drop", out _))
                {
                    verdict = "drop";
                }
                else if (e.TryGetProperty("reject", out _))
                {
                    verdict = "drop";
                }
                else if (e.TryGetProperty("return", out _))
                {
                    verdict = "return";
                }
                else if (e.TryGetProperty("jump", out var jump) || e.TryGetProperty("goto", out jump))
                {
                    verdict = e.TryGetProperty("jump", out _) ? "jump" : "goto";
                    target = Str(jump, "target");
                }
                else if (e.TryGetProperty("counter", out _) || e.TryGetProperty("log", out _) || e.TryGetProperty("limit", out _) || e.TryGetProperty("comment", out _))
                {
                    // Statements that don't decide.
                }
                else
                {
                    // xt (iptables-nft), vmap, fib, socket, meta mark …: more than this evaluation knows.
                    matches = matches == false ? false : null;
                }
            }
            if (matches == false || verdict is null)
            {
                continue;
            }
            if (matches is null)
            {
                return PortVerdict.IsUnknown;
            }
            if (restricted is not null)
            {
                // Only some sources: they are let in (or kept out), the rest goes on.
                if (verdict == "accept")
                {
                    onlyFrom.Add(restricted);
                }
                if (verdict is "accept" or "drop")
                {
                    continue;
                }
                return PortVerdict.IsUnknown;
            }
            switch (verdict)
            {
                case "accept":
                    return PortVerdict.IsOpen;
                case "drop":
                    return PortVerdict.IsFiltered;
                case "return":
                    return null;
                case "jump" or "goto":
                    if (all.FirstOrDefault(c => c.Family == chain.Family && c.Table == chain.Table && c.Name == target && c.Hook is null) is not { } next)
                    {
                        return PortVerdict.IsUnknown;
                    }
                    var result = EvaluateChain(next, all, protocol, port, family, depth + 1, onlyFrom);
                    if (result is not null || verdict == "goto")
                    {
                        return result;
                    }
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// One match against a new connection from anywhere to the port: true, false, or null (can't be told). A source address match sets
    /// <paramref name="source"/> and counts as matching.
    /// </summary>
    private static bool? Match(JsonElement match, string protocol, int port, string family, out string? source)
    {
        source = null;
        var op = Str(match, "op") ?? "==";
        if (!match.TryGetProperty("left", out var left) || !match.TryGetProperty("right", out var right))
        {
            return null;
        }
        if (left.TryGetProperty("meta", out var meta) && Str(meta, "key") is { } key)
        {
            switch (key)
            {
                case "iifname" or "iif":
                    // The loopback interface's rules don't concern connections from outside; others are per interface: unknown.
                    return Values(right).All(v => v == "lo") ? op == "!=" : null;
                case "l4proto":
                    return Compare(op, Values(right).Contains(protocol));
                case "nfproto":
                    return Compare(op, Values(right).Contains(family == "ip" ? "ipv4" : "ipv6"));
                default:
                    return null;
            }
        }
        if (left.TryGetProperty("ct", out var ct) && Str(ct, "key") == "state")
        {
            var states = Values(right);
            // A new connection's state is new: established, related and invalid don't match it.
            return op is "==" or "in" ? states.Contains("new") : op == "!=" ? !states.Contains("new") : null;
        }
        if (left.TryGetProperty("payload", out var payload))
        {
            var proto = Str(payload, "protocol");
            var field = Str(payload, "field");
            switch (proto, field)
            {
                case ("tcp" or "udp", "dport"):
                    if (proto != protocol)
                    {
                        return false;
                    }
                    return PortIn(right, port) is { } inside ? Compare(op, inside) : null;
                case ("tcp" or "udp" or "th", _):
                    return proto == protocol || proto == "th" ? null : false;
                case ("ip", "protocol") or ("ip6", "nexthdr"):
                    if ((proto == "ip") != (family == "ip"))
                    {
                        return false;
                    }
                    return Compare(op, Values(right).Contains(protocol));
                case ("ip" or "ip6", "saddr"):
                    if ((proto == "ip") != (family == "ip"))
                    {
                        return false;
                    }
                    source = string.Join(", ", Values(right));
                    return op == "==" || op == "in" ? true : null;
                case ("icmp" or "icmpv6", _):
                    return false;
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool? Compare(string op, bool inside) => op switch
    {
        "==" or "in" => inside,
        "!=" => !inside,
        _ => null,
    };

    /// <summary>A port against 22, a set of ports and ranges, or a range; null for a named set (<c>@ports</c>).</summary>
    private static bool? PortIn(JsonElement right, int port)
    {
        switch (right.ValueKind)
        {
            case JsonValueKind.Number:
                return right.GetInt32() == port;
            case JsonValueKind.String:
                return right.GetString() is { } s && s.StartsWith('@') ? null : int.TryParse(right.GetString(), out var n) && n == port;
            case JsonValueKind.Object when right.TryGetProperty("set", out var set):
                var any = false;
                foreach (var element in set.EnumerateArray())
                {
                    if (PortIn(element, port) is not { } inside)
                    {
                        return null;
                    }
                    any |= inside;
                }
                return any;
            case JsonValueKind.Object when right.TryGetProperty("range", out var range) && range.GetArrayLength() == 2:
                return range[0].ValueKind == JsonValueKind.Number && range[1].ValueKind == JsonValueKind.Number
                    ? port >= range[0].GetInt32() && port <= range[1].GetInt32()
                    : null;
            case JsonValueKind.Array:
                return right.EnumerateArray().Any(e => PortIn(e, port) == true);
            default:
                return null;
        }
    }

    /// <summary>A match's right side as strings: one value, a set, or a list.</summary>
    private static List<string> Values(JsonElement right) => right.ValueKind switch
    {
        JsonValueKind.String => [right.GetString() ?? ""],
        JsonValueKind.Number => [right.GetRawText()],
        JsonValueKind.Array => right.EnumerateArray().SelectMany(Values).ToList(),
        JsonValueKind.Object when right.TryGetProperty("set", out var set) => Values(set),
        JsonValueKind.Object when right.TryGetProperty("prefix", out var prefix) => [$"{Str(prefix, "addr")}/{(prefix.TryGetProperty("len", out var l) ? l.GetRawText() : "")}"],
        _ => [right.GetRawText()],
    };

    // --- iptables (legacy) --------------------------------------------------------------------------------------

    /// <summary>
    /// <c>iptables-legacy -S INPUT</c>: the policy and the rules in order, for IPv4. Interfaces other than lo, custom chains and matches it
    /// doesn't know make it unknown. Null when there are no rules and the policy accepts (no firewall).
    /// </summary>
    public static FirewallVerdict? Iptables(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var policy = lines.FirstOrDefault(l => l.StartsWith("-P INPUT ", StringComparison.Ordinal))?["-P INPUT ".Length..] ?? "ACCEPT";
        var rules = lines.Where(l => l.StartsWith("-A INPUT ", StringComparison.Ordinal)).Select(l => l["-A INPUT ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        if (rules.Count == 0 && policy == "ACCEPT")
        {
            return null;
        }
        return new FirewallVerdict("iptables", (protocol, port) =>
        {
            var onlyFrom = new List<string>();
            foreach (var words in rules)
            {
                bool? matches = true;
                string? source = null;
                string? jump = null;
                for (var i = 0; i < words.Length; i++)
                {
                    var word = words[i];
                    var value = i + 1 < words.Length ? words[i + 1] : "";
                    switch (word)
                    {
                        case "-i" or "--in-interface":
                            matches = value == "lo" ? false : matches == false ? false : null;
                            i++;
                            break;
                        case "-p" or "--protocol":
                            if (value != protocol && value != "all")
                            {
                                matches = false;
                            }
                            i++;
                            break;
                        case "--dport" or "--destination-port":
                            if (!InPorts(value, port))
                            {
                                matches = false;
                            }
                            i++;
                            break;
                        case "--dports":
                            if (!InPorts(value, port))
                            {
                                matches = false;
                            }
                            i++;
                            break;
                        case "--ctstate" or "--state":
                            if (!value.Split(',').Contains("NEW"))
                            {
                                matches = false;
                            }
                            i++;
                            break;
                        case "-s" or "--source":
                            source = value;
                            i++;
                            break;
                        case "-m":
                            // tcp, udp, conntrack, state, multiport, comment: their options follow.
                            if (value is not ("tcp" or "udp" or "conntrack" or "state" or "multiport" or "comment"))
                            {
                                matches = matches == false ? false : null;
                            }
                            i++;
                            break;
                        case "--comment":
                            i++;
                            break;
                        case "-j":
                            jump = value;
                            i = words.Length;
                            break;
                        default:
                            matches = matches == false ? false : null;
                            break;
                    }
                }
                if (matches == false || jump is null or "LOG")
                {
                    continue;
                }
                if (matches is null || jump is not ("ACCEPT" or "DROP" or "REJECT"))
                {
                    return PortVerdict.IsUnknown;
                }
                if (source is not null)
                {
                    if (jump == "ACCEPT")
                    {
                        onlyFrom.Add(source);
                    }
                    continue;
                }
                return jump == "ACCEPT" ? PortVerdict.IsOpen : PortVerdict.IsFiltered;
            }
            return policy == "ACCEPT" ? PortVerdict.IsOpen : new PortVerdict(PortVerdict.Filtered, onlyFrom);
        });
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
