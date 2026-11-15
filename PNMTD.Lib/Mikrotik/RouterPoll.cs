using System.Text.Json;

namespace PNMTD.Lib.Mikrotik;

/// <summary>
/// One poll (docs/mikrotik-plan.md §5.1): a single exec of <see cref="Script"/>, which prints one JSON
/// document, parsed by <see cref="Parse"/>. Recorded in M0, on 7.13 and 7.25.1:
/// <list type="bullet">
/// <item>Over exec, every line is parsed on its own, so the script is one line.</item>
/// <item>A menu that does not exist (<c>/system routerboard</c> on CHR and x86) is a syntax error that
/// fails the whole script, so optional menus are parsed at run time, with <c>:parse</c> inside <c>:do … on-error</c>.</item>
/// <item>Versions are read with a prefix ("v7.20"), because <c>:serialize</c> turns a string that looks
/// like a number into one (7.20 → 7.200000).</item>
/// <item>The prints of lists that could hold a secret (WireGuard, peers) name their properties
/// (<c>proplist</c>), so no secret can reach the stored reading, whatever a RouterOS version shows to a
/// read-only user. A proplist may name only properties that every version has: an unknown one fails the
/// print ("input does not match any value of value-name"). Menus with one item (<c>/system resource</c>)
/// take no proplist at all ("bad parameter proplist") and hold no secrets.</item>
/// </list>
/// </summary>
public static class RouterPoll
{
    public const string UserName = "pnmt";

    public const string GroupName = "pnmt-read";

    /// <summary>The poll script for the installation's tag (the comment its join script writes, "pnmt:7f3a").</summary>
    public static string Script(string tag)
    {
        var t = RouterOsScript.Quote(tag);
        string[] parts =
        [
            ":local r ({})",
            ":set ($r->\"resource\") [/system/resource/print as-value]",
            ":set ($r->\"version\") (\"v\" . [/system/resource/get version])",
            ":set ($r->\"identity\") [/system/identity/get name]",
            ":set ($r->\"channel\") [/system/package/update/get channel]",
            // CHR and x86 have no RouterBOARD menu: its absence must not fail the poll.
            ":do { :set ($r->\"routerboard\") [[:parse \"/system/routerboard/print as-value\"]]; :set ($r->\"firmware\") [[:parse \":return (\\\"v\\\" . [/system/routerboard/get current-firmware] . \\\" v\\\" . [/system/routerboard/get upgrade-firmware])\"]] } on-error={}",
            ":do { :set ($r->\"health\") [[:parse \"/system/health/print as-value\"]] } on-error={}",
            $":set ($r->\"group\") [/user/group/print as-value proplist=name,policy where name=\"{GroupName}\"]",
            $":set ($r->\"keys\") [/user/ssh-keys/print as-value where user=\"{UserName}\"]",
            ":set ($r->\"ssh\") [/ip/service/print as-value proplist=name,port,address,disabled where name=\"ssh\"]",
            $":set ($r->\"wireguard\") [/interface/wireguard/print as-value proplist=name,listen-port,mtu,public-key,disabled,running where comment={t}]",
            $":set ($r->\"peers\") [/interface/wireguard/peers/print as-value proplist=public-key,endpoint-address,endpoint-port,allowed-address,persistent-keepalive,disabled where comment={t}]",
            $":set ($r->\"firewall\") [/ip/firewall/filter/print as-value proplist=chain,action,src-address,dst-port,disabled where comment={t}]",
            // The access watch (docs/cookie-access-plan.md §4.2): the log's login lines, the users and their groups, who is logged in
            // now, and the clock to read the lines' times with. Each on its own, so that none can fail the poll.
            ":do { :set ($r->\"log\") [/log/print as-value where topics~\"account\"] } on-error={}",
            ":do { :set ($r->\"users\") [/user/print as-value proplist=name,group,disabled] } on-error={}",
            ":do { :set ($r->\"groups\") [/user/group/print as-value proplist=name,policy] } on-error={}",
            ":do { :set ($r->\"active\") [/user/active/print as-value proplist=name,when,address,via] } on-error={}",
            ":do { :set ($r->\"clock\") ([/system/clock/get date] . \" \" . [/system/clock/get time]) } on-error={}",
            ":put [:serialize to=json $r]",
        ];
        return "{ " + string.Join("; ", parts) + " }";
    }

    /// <summary>Parses the script's output; throws <see cref="FormatException"/> when it is not the expected JSON ("unexpected output").</summary>
    public static RouterReading Parse(string output)
    {
        var json = output.Trim();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Unexpected output: {Excerpt(json)}", ex);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.Item("resource") is not { } resource)
            {
                throw new FormatException($"Unexpected output: {Excerpt(json)}");
            }
            var versionText = root.Text("version") is { Length: > 1 } v && v[0] == 'v' ? v[1..] : resource.Text("version");
            RouterOsVersion.TryParse(versionText, out var version, out var suffixChannel);
            var parsedVersion = RouterOsVersion.TryParse(versionText, out _) ? version : null;
            var rb = root.Item("routerboard");
            var firmware = (root.Text("firmware") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.StartsWith('v') ? f[1..] : f).ToArray();
            var group = root.Item("group");
            var ssh = root.Item("ssh");
            var wg = root.Item("wireguard");
            var peer = root.Item("peers");
            return new RouterReading
            {
                Identity = root.Text("identity"),
                VersionText = versionText is null ? null : StripChannel(versionText),
                Version = parsedVersion,
                Channel = root.Text("channel") ?? suffixChannel,
                Uptime = RouterOsValues.Duration(resource.Text("uptime")),
                Cpu = resource.Text("cpu"),
                CpuCount = resource.Int("cpu-count"),
                CpuFrequencyMhz = resource.Int("cpu-frequency"),
                CpuLoad = resource.Int("cpu-load"),
                FreeMemory = resource.Long("free-memory"),
                TotalMemory = resource.Long("total-memory"),
                FreeStorage = resource.Long("free-hdd-space"),
                TotalStorage = resource.Long("total-hdd-space"),
                Architecture = resource.Text("architecture-name"),
                BoardName = resource.Text("board-name"),
                WriteSectorsTotal = resource.Long("write-sect-total"),
                BadBlocks = resource.Text("bad-blocks"),
                IsRouterBoard = rb?.Bool("routerboard") ?? false,
                Model = rb?.Text("model"),
                SerialNumber = rb?.Text("serial-number"),
                FirmwareType = rb?.Text("firmware-type"),
                FirmwareCurrent = firmware.Length > 0 ? firmware[0] : null,
                FirmwareUpgrade = firmware.Length > 1 ? firmware[1] : null,
                Health = root.TryGetProperty("health", out var health) ? health.GetRawText() : null,
                GroupFound = group is not null,
                GroupPolicies = group?.List("policy").Where(p => !p.StartsWith('!')).ToList() ?? [],
                SshKeyOwners = root.Items("keys").Select(k => k.Text("key-owner") ?? k.Text("info")).OfType<string>().ToList(),
                SshPort = ssh?.Int("port"),
                SshAddresses = ssh?.List("address") ?? [],
                SshDisabled = ssh?.Bool("disabled") ?? false,
                WireGuard = wg is { } w ? new RouterWireGuard(w.Text("public-key"), w.Int("listen-port"), w.Int("mtu"), w.Bool("disabled") ?? false,
                    peer?.Text("endpoint-address"), peer?.Int("endpoint-port"), RouterOsValues.Duration(peer?.Text("persistent-keepalive")),
                    peer?.List("allowed-address") ?? [], peer?.Text("public-key")) : null,
                FirewallRules = root.Items("firewall").Select(f => new RouterFirewallRule(f.Text("chain"), f.Text("action"), f.Text("src-address"),
                    f.Text("dst-port"), f.Bool("disabled") ?? false)).ToList(),
                LogRead = root.TryGetProperty("log", out _),
                LogLines = root.Items("log").Select(l => new RouterLogLine(l.Text("time"), l.Text("message") ?? "")).Where(l => l.Message.Length > 0).ToList(),
                Users = root.Items("users").Select(u => new RouterUser(u.Text("name") ?? "", u.Text("group"), u.Bool("disabled") ?? false))
                    .Where(u => u.Name.Length > 0).ToList(),
                Groups = root.Items("groups").Select(g => new RouterGroup(g.Text("name") ?? "", g.List("policy").Where(p => !p.StartsWith('!')).ToList()))
                    .Where(g => g.Name.Length > 0).ToList(),
                Active = root.Items("active").Select(a => new RouterActiveUser(a.Text("name") ?? "", a.Text("when"), a.Text("address"), a.Text("via")))
                    .Where(a => a.Name.Length > 0).ToList(),
                Clock = root.Text("clock"),
            };
        }
    }

    private static string StripChannel(string text) => text.IndexOf(' ') is var i and > 0 ? text[..i] : text;

    private static string Excerpt(string text) => text.Length <= 200 ? text : text[..200] + "…";
}

/// <summary>What a poll read (§5.1). Values a router does not have (RouterBOARD on CHR) are null.</summary>
public sealed record RouterReading
{
    public string? Identity { get; init; }

    /// <summary>As the router wrote it, without the channel ("7.16.1").</summary>
    public string? VersionText { get; init; }

    public RouterOsVersion? Version { get; init; }

    public string? Channel { get; init; }

    public TimeSpan? Uptime { get; init; }

    public string? Cpu { get; init; }

    public int? CpuCount { get; init; }

    public int? CpuFrequencyMhz { get; init; }

    public int? CpuLoad { get; init; }

    public long? FreeMemory { get; init; }

    public long? TotalMemory { get; init; }

    public long? FreeStorage { get; init; }

    public long? TotalStorage { get; init; }

    public string? Architecture { get; init; }

    public string? BoardName { get; init; }

    public long? WriteSectorsTotal { get; init; }

    public string? BadBlocks { get; init; }

    public bool IsRouterBoard { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    public string? FirmwareType { get; init; }

    public string? FirmwareCurrent { get; init; }

    public string? FirmwareUpgrade { get; init; }

    /// <summary>The raw JSON of <c>/system health</c>: its shape differs between boards.</summary>
    public string? Health { get; init; }

    public bool GroupFound { get; init; }

    /// <summary>The policies the <c>pnmt-read</c> group has (the "!" ones left out).</summary>
    public IReadOnlyList<string> GroupPolicies { get; init; } = [];

    /// <summary>The comments of the <c>pnmt</c> user's SSH keys: which of PNMTD's keys the router has (§4.2).</summary>
    public IReadOnlyList<string> SshKeyOwners { get; init; } = [];

    public int? SshPort { get; init; }

    public IReadOnlyList<string> SshAddresses { get; init; } = [];

    public bool SshDisabled { get; init; }

    public RouterWireGuard? WireGuard { get; init; }

    public IReadOnlyList<RouterFirewallRule> FirewallRules { get; init; } = [];

    // The access watch's reads (docs/cookie-access-plan.md §4.2): stored elsewhere, never in the router's stored reading.

    /// <summary>Whether the log was read at all (an older poll script, or a refusal, reads nothing).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool LogRead { get; init; }

    /// <summary>The log's lines of the topic <c>account</c>: logins, logouts, failures.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<RouterLogLine> LogLines { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<RouterUser> Users { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<RouterGroup> Groups { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<RouterActiveUser> Active { get; init; } = [];

    /// <summary>The router's clock when the poll ran: <c>/system clock</c>'s date and time.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Clock { get; init; }

    /// <summary>The model where a RouterBOARD names it, else the board ("CHR").</summary>
    public string? DisplayModel => Model ?? BoardName;
}

/// <summary>The router's own side of the PNMT tunnel, for "script outdated" (§3.3).</summary>
public sealed record RouterWireGuard(string? PublicKey, int? ListenPort, int? Mtu, bool Disabled, string? EndpointAddress, int? EndpointPort,
    TimeSpan? Keepalive, IReadOnlyList<string> AllowedAddresses, string? PeerPublicKey);

public sealed record RouterFirewallRule(string? Chain, string? Action, string? SourceAddress, string? DestinationPorts, bool Disabled);
