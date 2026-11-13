using System.Globalization;
using System.Text;

namespace PNMTD.Lib.Mikrotik;

/// <summary>How PNMTD reaches a router (docs/mikrotik-plan.md §3.1).</summary>
public enum MikrotikMode : short
{
    /// <summary>The router's tunnel, and SSH through it as <c>pnmt</c>: every feature.</summary>
    WireGuardSsh = 1,

    /// <summary>The tunnel alone: its state is the sensor's state.</summary>
    WireGuardOnly = 2,

    /// <summary>SSH to an address PNMTD can reach, no tunnel.</summary>
    DirectSsh = 3,
}

public static class MikrotikModes
{
    public static bool UsesSsh(this MikrotikMode mode) => mode is MikrotikMode.WireGuardSsh or MikrotikMode.DirectSsh;

    public static bool UsesWireGuard(this MikrotikMode mode) => mode is MikrotikMode.WireGuardSsh or MikrotikMode.WireGuardOnly;

    public static string Label(this MikrotikMode mode) => mode switch
    {
        MikrotikMode.WireGuardSsh => "WireGuard + SSH",
        MikrotikMode.WireGuardOnly => "WireGuard only",
        _ => "Direct SSH",
    };
}

/// <summary>The router's side of the tunnel, for the WireGuard modes (§3.2).</summary>
public sealed record JoinScriptTunnel(
    string PrivateKey,
    string GatewayPublicKey,
    string EndpointHost,
    int EndpointPort,
    string TunnelAddress,
    string ServerBlock,
    string ServerAddress,
    string AccessAddress,
    int KeepaliveSeconds,
    int Mtu);

/// <summary>Everything a join script is made of (§4.3).</summary>
public sealed record JoinScriptSettings
{
    public required string RouterName { get; init; }

    /// <summary>The PNMT installation, as people know it: PublicBaseUrl's host or the identity.</summary>
    public required string Instance { get; init; }

    /// <summary>The comment on everything the script creates: "pnmt:7f3a".</summary>
    public required string Tag { get; init; }

    public required MikrotikMode Mode { get; init; }

    public required DateTime GeneratedAt { get; init; }

    public JoinScriptTunnel? Tunnel { get; init; }

    /// <summary>PNMTD's public keys, OpenSSH lines; two while a rotation runs (§4.2).</summary>
    public IReadOnlyList<string> SshPublicKeys { get; init; } = [];

    /// <summary>Where <c>pnmt</c> may log in from: the server address, PNMTD's source address (Direct SSH), or null for anywhere.</summary>
    public string? UserAddress { get; init; }

    /// <summary>Direct SSH: the address and port PNMTD connects to, for the commented firewall example.</summary>
    public int SshPort { get; init; } = 22;

    public bool IncludeSecrets { get; init; }

    public bool Diagnostics { get; init; }

    public bool ManagementAccess { get; init; }

    public IReadOnlyList<int> ManagementPorts { get; init; } = [22, 8291, 80, 443];
}

/// <summary>
/// The join and removal scripts (§4.3, §4.6). Recorded in M0 on 7.13 and 7.25.1:
/// <list type="bullet">
/// <item>A pasted or imported script runs line by line, so a <c>:local</c> lives inside one <c>{ … }</c> line.</item>
/// <item><c>place-before=0</c> fails on an empty filter list ("no such item"): PNMT's rules are added, then
/// moved before the first rule that is not dynamic, unless they are first already.</item>
/// <item>7.25 removes the key file itself after <c>ssh-keys import</c>, so the file is removed with <c>find</c>.</item>
/// <item>On 7.13 a new file or interface appears only a moment after its <c>add</c>: see <see cref="WaitFor"/>.
/// A firewall rule on a new interface can stay invalid; setting its interface again repairs it.</item>
/// <item>7.13 keeps a removed user's SSH keys for the next user of that name: they are removed first.</item>
/// </list>
/// Every string is quoted by <see cref="RouterOsScript.Quote"/>.
/// </summary>
public static class JoinScript
{
    public const string KeyFilePrefix = "pnmt";

    public static string Build(JoinScriptSettings s)
    {
        var tag = RouterOsScript.Quote(s.Tag);
        var sb = new StringBuilder();
        Header(sb, s, "join script");
        if (s.Mode.UsesWireGuard())
        {
            sb.AppendLine("# It contains this router's WireGuard private key: keep it like a password.");
        }
        sb.AppendLine($"# Everything it creates carries the comment {tag} (this installation): pasting it again replaces it,");
        sb.AppendLine("# and so does what an earlier PNMT installation created (a comment pnmt:…).");
        RemoveAll(sb);
        if (s.Mode.UsesWireGuard())
        {
            WaitGone(sb, "/interface", "\"pnmt\"");
            var t = s.Tunnel ?? throw new ArgumentException("A WireGuard mode needs the tunnel's settings.", nameof(s));
            sb.AppendLine($"{{ :local port 13231; :while ([:len [/interface wireguard find listen-port=$port]] > 0) do={{ :set port ($port + 1) }}; " +
                $"/interface wireguard add name=pnmt listen-port=$port mtu={t.Mtu} private-key={RouterOsScript.Quote(t.PrivateKey)} comment={tag} }}");
            WaitFor(sb, "/interface", "\"pnmt\"");
            sb.AppendLine($"/interface wireguard peers add interface=pnmt public-key={RouterOsScript.Quote(t.GatewayPublicKey)} " +
                $"endpoint-address={RouterOsScript.Quote(t.EndpointHost)} endpoint-port={t.EndpointPort} allowed-address={t.ServerBlock} " +
                $"persistent-keepalive={t.KeepaliveSeconds}s comment={tag}");
            sb.AppendLine($"/ip address add address={t.TunnelAddress}/32 interface=pnmt comment={tag}");
            sb.AppendLine($"/ip route add dst-address={t.ServerBlock} gateway=pnmt comment={tag}");
            var rules = false;
            if (s.Mode == MikrotikMode.WireGuardSsh)
            {
                sb.AppendLine($"/ip firewall filter add chain=input in-interface=pnmt src-address={t.ServerAddress} protocol=tcp dst-port={s.SshPort} action=accept comment={tag}");
                rules = true;
            }
            if (s.ManagementAccess && s.ManagementPorts.Count > 0)
            {
                sb.AppendLine($"/ip firewall filter add chain=input in-interface=pnmt src-address={t.AccessAddress} protocol=tcp " +
                    $"dst-port={string.Join(',', s.ManagementPorts.Select(p => p.ToString(CultureInfo.InvariantCulture)))} action=accept comment={tag}");
                rules = true;
            }
            if (rules)
            {
                // 7.13 (M0): a rule added right after its interface sometimes stays invalid ("I"), although the
                // interface exists. Setting the interface again resolves it.
                sb.AppendLine($"{{ :local n 0; :while ([:len [/ip firewall filter find where comment={tag} and invalid]] > 0 and $n < 50) do={{ :delay 100ms; " +
                    $"/ip firewall filter set [find where comment={tag} and invalid] in-interface=pnmt; :set n ($n + 1) }} }}");
                // First in the filter list, so a drop rule further down cannot catch PNMT's traffic.
                sb.AppendLine($"{{ :local all [/ip firewall filter find where dynamic=no]; :if ([/ip firewall filter get [:pick $all 0] comment] != {tag}) do={{ " +
                    $"/ip firewall filter move [find where comment={tag}] destination=[:pick $all 0] }} }}");
            }
        }
        if (s.Mode.UsesSsh())
        {
            var policies = new List<string> { "ssh", "read" };
            if (s.IncludeSecrets) policies.Add("sensitive");
            if (s.Diagnostics) policies.Add("test");
            sb.AppendLine($"/user group add name={RouterPoll.GroupName} policy={string.Join(',', policies)} comment={tag}");
            var address = s.UserAddress is null ? "" : $" address={s.UserAddress}";
            sb.AppendLine($"/user add name={RouterPoll.UserName} group={RouterPoll.GroupName}{address} password=[:rndstr length=32] comment={tag}");
            for (var i = 0; i < s.SshPublicKeys.Count; i++)
            {
                var file = RouterOsScript.Quote($"{KeyFilePrefix}-{i + 1}.pub");
                sb.AppendLine($"/file remove [find name={file}]");
                WaitGone(sb, "/file", file);
                sb.AppendLine($"/file add name={file} contents={RouterOsScript.Quote(s.SshPublicKeys[i])}");
                WaitFor(sb, "/file", file);
                sb.AppendLine($"/user ssh-keys import user={RouterPoll.UserName} public-key-file={file}");
                sb.AppendLine($"/file remove [find name={file}]");
            }
            sb.AppendLine($":foreach s in=[/ip service find where name=\"ssh\" and disabled=yes] do={{ :put \"PNMT: warning: the SSH service is disabled. Enable it with: /ip service enable ssh\" }}");
            if (s.Mode == MikrotikMode.DirectSsh)
            {
                sb.AppendLine($"# PNMT connects to this router's SSH port ({s.SshPort}). If the firewall blocks it, allow PNMT's address, for example:");
                sb.AppendLine($"# /ip firewall filter add chain=input src-address={(s.UserAddress is { } a ? a.Split('/')[0] : "<PNMT's address>")} " +
                    $"protocol=tcp dst-port={s.SshPort} action=accept comment={tag}");
                sb.AppendLine("# Then move it above any rule that drops input (WinBox: drag it up; or /ip firewall filter move).");
            }
        }
        sb.AppendLine(s.Mode switch
        {
            MikrotikMode.WireGuardSsh => $":put {RouterOsScript.Quote($"PNMT: joined as {s.Tunnel!.TunnelAddress}, dialling {s.Tunnel.EndpointHost}:{s.Tunnel.EndpointPort}. PNMT logs in within a minute.")}",
            MikrotikMode.WireGuardOnly => $":put {RouterOsScript.Quote($"PNMT: joined as {s.Tunnel!.TunnelAddress}, dialling {s.Tunnel.EndpointHost}:{s.Tunnel.EndpointPort}.")}",
            _ => $":put {RouterOsScript.Quote("PNMT: the user pnmt is ready. PNMT logs in at its next poll.")}",
        });
        return sb.ToString();
    }

    /// <summary>Removes everything a join script of this installation created (§4.6). PNMTD cannot: <c>pnmt</c> may not write.</summary>
    public static string Removal(string routerName, string instance, string tag, DateTime generatedAt)
    {
        var quoted = RouterOsScript.Quote(tag);
        var sb = new StringBuilder();
        sb.AppendLine($"# PNMT removal script for {Comment(routerName)} ({Comment(instance)}, generated {generatedAt:yyyy-MM-dd}).");
        sb.AppendLine($"# It removes everything a PNMT join script created (comment {quoted}, or pnmt:… of an earlier installation):");
        sb.AppendLine("# the tunnel, its firewall rules, and the user pnmt with its keys.");
        RemoveAll(sb);
        sb.AppendLine($":put {RouterOsScript.Quote("PNMT: removed.")}");
        return sb.ToString();
    }

    /// <summary>
    /// Waits up to 5 seconds until <paramref name="menu"/> lists <paramref name="quotedName"/>. On 7.13 a new
    /// file or interface appears a moment after its <c>add</c> returns (M0): an <c>import</c> of the file, or a
    /// firewall rule on the interface, right after it fails ("input does not match any value", an invalid rule).
    /// </summary>
    private static void WaitFor(StringBuilder sb, string menu, string quotedName) =>
        sb.AppendLine($"{{ :local n 0; :while ([:len [{menu} find where name={quotedName}]] = 0 and $n < 50) do={{ :delay 100ms; :set n ($n + 1) }} }}");

    /// <summary>
    /// The other way round: waits until a removed object is gone. Otherwise the <see cref="WaitFor"/> after
    /// the new <c>add</c> can still see the old one, and the rule lands on the interface that is going away.
    /// </summary>
    private static void WaitGone(StringBuilder sb, string menu, string quotedName) =>
        sb.AppendLine($"{{ :local n 0; :while ([:len [{menu} find where name={quotedName}]] > 0 and $n < 50) do={{ :delay 100ms; :set n ($n + 1) }} }}");

    /// <summary>A <c>find</c> condition for everything any PNMT join script created: its comment starts with "pnmt:".</summary>
    public const string AnyPnmtComment = "comment~\"^pnmt:\"";

    /// <summary>
    /// Everything a PNMT join script created, this installation's or an earlier one's, in an order that never
    /// removes something still in use. One PNMT per router: the names (the user pnmt, the group, the
    /// interface) are the same for every installation, so a router that moves to another installation, or
    /// a reinstalled PNMT with a new tag, would otherwise fail with "already exists" (the lab, M1).
    /// </summary>
    private static void RemoveAll(StringBuilder sb)
    {
        sb.AppendLine($"/ip firewall filter remove [find where {AnyPnmtComment}]");
        sb.AppendLine($"/ip route remove [find where {AnyPnmtComment}]");
        sb.AppendLine($"/ip address remove [find where {AnyPnmtComment}]");
        sb.AppendLine($"/interface wireguard peers remove [find where {AnyPnmtComment}]");
        sb.AppendLine($"/interface wireguard remove [find where {AnyPnmtComment}]");
        // 7.13 (M0) keeps a removed user's SSH keys and gives them to the next user of that name, so an
        // old key would still log in after a new paste: the keys go explicitly first.
        sb.AppendLine($"/user ssh-keys remove [find where user=\"{RouterPoll.UserName}\"]");
        sb.AppendLine($"/user remove [find where {AnyPnmtComment}]");
        sb.AppendLine($"/user group remove [find where {AnyPnmtComment}]");
    }

    private static void Header(StringBuilder sb, JoinScriptSettings s, string what) =>
        sb.AppendLine($"# PNMT {what} for {Comment(s.RouterName)} ({Comment(s.Instance)}, generated {s.GeneratedAt:yyyy-MM-dd}). Mode: {s.Mode.Label()}.");

    /// <summary>A name inside a comment line: one line, quoted.</summary>
    private static string Comment(string text) => "\"" + new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()) + "\"";
}
