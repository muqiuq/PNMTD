using System.Text;
using PNMTD.Lib.Mikrotik;
using PNMTD.Mikrotik;

namespace PNMTD.Tests.Mikrotik;

/// <summary>The pure parts (docs/mikrotik-plan.md §14), on the output M0 recorded on RouterOS 7.13 and 7.25.1.</summary>
[TestClass]
public sealed class RouterOsTests
{
    private static string Fixture(string version, string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mikrotik", version, file));

    // --- versions -------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("7.13", 7, 13, 0, RouterOsStage.Release, 0)]
    [DataRow("7.16.1", 7, 16, 1, RouterOsStage.Release, 0)]
    [DataRow("7.17beta2", 7, 17, 0, RouterOsStage.Beta, 2)]
    [DataRow("7.15rc3", 7, 15, 0, RouterOsStage.ReleaseCandidate, 3)]
    [DataRow("7.13 (stable)", 7, 13, 0, RouterOsStage.Release, 0)]
    public void Versions_parse(string text, int major, int minor, int patch, RouterOsStage stage, int number)
    {
        Assert.IsTrue(RouterOsVersion.TryParse(text, out var v));
        Assert.AreEqual(new RouterOsVersion(major, minor, patch, stage, number), v);
    }

    [TestMethod]
    public void Versions_order_as_releases_do()
    {
        string[] ordered = ["7.12.1", "7.13", "7.13.1", "7.17beta1", "7.17beta2", "7.17rc1", "7.17", "7.17.1", "7.20", "7.25.1"];
        var parsed = ordered.Select(RouterOsVersion.Parse).ToList();
        CollectionAssert.AreEqual(ordered, parsed.OrderBy(v => v).Select(v => v.ToString()).ToList());
        Assert.IsTrue(RouterOsVersion.Parse("7.12.1").IsBelowFloor);
        Assert.IsFalse(RouterOsVersion.Parse("7.13").IsBelowFloor);
        Assert.IsTrue(RouterOsVersion.TryParse("7.16.1 (long-term)", out _, out var channel));
        Assert.AreEqual("long-term", channel);
        Assert.IsFalse(RouterOsVersion.TryParse("seven", out _));
        Assert.IsFalse(RouterOsVersion.TryParse("7.130000x", out _));
    }

    // --- values ------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow("1970-01-01 00:00:43", 43)]
    [DataRow("1970-01-02 03:04:05", 97445)]
    [DataRow("00:02:38", 158)]
    [DataRow("1d02:03:04", 93784)]
    [DataRow("1w2d03:04:05", 788645)]
    [DataRow("3h4m5s", 11045)]
    [DataRow("25s", 25)]
    [DataRow("1d", 86400)]
    public void Durations_in_every_form_RouterOS_writes(string text, int seconds) =>
        Assert.AreEqual(TimeSpan.FromSeconds(seconds), RouterOsValues.Duration(text));

    [TestMethod]
    public void Sizes_and_numbers()
    {
        Assert.AreEqual(12_582_912L, RouterOsValues.Bytes("12MiB"));
        Assert.AreEqual(1536L, RouterOsValues.Bytes("1.5KiB"));
        Assert.AreEqual(75210752L, RouterOsValues.Bytes("75210752"));
        Assert.IsNull(RouterOsValues.Bytes("a lot"));
        // :serialize writes "22" as 22.000000 and "7.13" as 7.130000 (M0).
        Assert.AreEqual("22", RouterOsValues.Number("22.000000"));
        Assert.AreEqual("7.13", RouterOsValues.Number("7.130000"));
        Assert.AreEqual("999", RouterOsValues.Number("999"));
    }

    // --- the poll -----------------------------------------------------------------------------------

    [TestMethod]
    public void Poll_of_7_13()
    {
        var r = RouterPoll.Parse(Fixture("7.13", "poll.json"));
        Assert.AreEqual("r13", r.Identity);
        Assert.AreEqual("7.13", r.VersionText);
        Assert.AreEqual(RouterOsVersion.Parse("7.13"), r.Version);
        Assert.AreEqual("stable", r.Channel);
        Assert.AreEqual(TimeSpan.FromSeconds(171), r.Uptime);
        Assert.AreEqual("CHR", r.DisplayModel);
        Assert.IsFalse(r.IsRouterBoard);
        Assert.IsNull(r.FirmwareCurrent);
        CollectionAssert.AreEqual(new[] { "ssh", "read" }, r.GroupPolicies.ToArray());
        CollectionAssert.AreEqual(new[] { "pnmt@lab" }, r.SshKeyOwners.ToArray());
        Assert.AreEqual(22, r.SshPort);
        Assert.AreEqual(1420, r.WireGuard?.Mtu);
        Assert.AreEqual(TimeSpan.FromSeconds(25), r.WireGuard?.Keepalive);
        Assert.AreEqual("vpn.example.com", r.WireGuard?.EndpointAddress);
        CollectionAssert.AreEqual(new[] { "10.253.0.0/30" }, r.WireGuard!.AllowedAddresses.ToArray());
        Assert.AreEqual("22", r.FirewallRules[0].DestinationPorts);
        Assert.AreEqual(1006632960L, r.TotalMemory);
    }

    [TestMethod]
    public void Poll_of_7_25_1()
    {
        var r = RouterPoll.Parse(Fixture("7.25.1", "poll.json"));
        Assert.AreEqual("7.25.1", r.VersionText);
        Assert.AreEqual("stable", r.Channel);
        Assert.AreEqual(TimeSpan.FromSeconds(344), r.Uptime);
        // 7.25 names the key's comment "info", 7.13 "key-owner".
        CollectionAssert.AreEqual(new[] { "pnmt@lab" }, r.SshKeyOwners.ToArray());
        Assert.AreEqual(2, r.FirewallRules.Count);
    }

    [TestMethod]
    public void Poll_reads_no_secret()
    {
        foreach (var version in new[] { "7.13", "7.25.1" })
        {
            var raw = Fixture(version, "poll.json");
            Assert.IsFalse(raw.Contains("private-key", StringComparison.Ordinal), version);
        }
        // The script names the properties of the lists that hold keys.
        StringAssert.Contains(RouterPoll.Script("pnmt:7f3a"), "/interface/wireguard/print as-value proplist=name,listen-port,mtu,public-key");
    }

    [TestMethod]
    public void Unexpected_output_is_a_format_error()
    {
        Assert.ThrowsExactly<FormatException>(() => RouterPoll.Parse("bad command name x (line 1 column 3)"));
        Assert.ThrowsExactly<FormatException>(() => RouterPoll.Parse("{\"identity\":\"x\"}"));
        Assert.ThrowsExactly<FormatException>(() => RouterPoll.Parse(""));
    }

    [TestMethod]
    public void Poll_script_is_one_line_with_the_tag_quoted()
    {
        var script = RouterPoll.Script("pnmt:7f3a");
        Assert.IsFalse(script.Contains('\n'), "exec parses every line on its own (M0)");
        StringAssert.Contains(script, "where comment=\"pnmt:7f3a\"");
        StringAssert.Contains(script, "[[:parse \"/system/routerboard/print as-value\"]]");
    }

    // --- quoting -----------------------------------------------------------------------------------

    [TestMethod]
    public void Quoting_follows_RouterOS_rules()
    {
        // M0 printed each of these back unchanged on 7.13.
        Assert.AreEqual("\"with \\\"quotes\\\"\"", RouterOsScript.Quote("with \"quotes\""));
        Assert.AreEqual("\"back\\\\slash\"", RouterOsScript.Quote("back\\slash"));
        Assert.AreEqual("\"dollar \\$x\"", RouterOsScript.Quote("dollar $x"));
        Assert.AreEqual("\"question\\?\"", RouterOsScript.Quote("question?"));
        Assert.AreEqual("\"new\\nline\\ttab\"", RouterOsScript.Quote("new\nline\ttab"));
        Assert.AreEqual("\"gr\\C3\\BCezi\"", RouterOsScript.Quote("grüezi"));
        var quoted = RouterOsScript.Quote("\u0001;[x]{y}🦊");
        Assert.IsTrue(quoted.All(c => c is >= ' ' and < (char)0x7F), quoted);
    }

    [TestMethod]
    public void One_script_wraps_the_text_in_parse()
    {
        var wrapped = RouterOsScript.AsOneScript("{\r\n:local x 1\r\n:put $x\r\n}");
        Assert.AreEqual(":local pnmtScript [:parse \"{\\n:local x 1\\n:put \\$x\\n}\"]; $pnmtScript", wrapped);
    }

    // --- the join script ------------------------------------------------------------------------------

    private static JoinScriptSettings Settings(MikrotikMode mode) => new()
    {
        RouterName = "office \"gw\"",
        Instance = "pnmt.example.com",
        Tag = "pnmt:7f3a",
        Mode = mode,
        GeneratedAt = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc),
        SshPublicKeys = ["ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIJHfraviAflbzG+6DsQRh5WgMrkjjvKHPvsoPjc7cLw+ pnmt-abc123@pnmt.example.com"],
        UserAddress = mode == MikrotikMode.DirectSsh ? "192.0.2.10/32" : "10.253.0.1/32",
        Tunnel = mode == MikrotikMode.DirectSsh ? null : new JoinScriptTunnel("UGsL4e17bQnWs1rb8vOGl/gGTpPHz1aec5UTmTqv+Xw=",
            "gNbcvXPEr5wWdZ+bMYMkU5FdB+LYzuD7RFDhvQIW3w4=", "vpn.example.com", 51820, "10.253.0.17", "10.253.0.0/30", "10.253.0.1", "10.253.0.2", 25, 1420),
    };

    [TestMethod]
    public void Direct_SSH_script_creates_only_the_user()
    {
        var script = JoinScript.Build(Settings(MikrotikMode.DirectSsh));
        StringAssert.Contains(script, "/user group add name=pnmt-read policy=ssh,read comment=\"pnmt:7f3a\"");
        StringAssert.Contains(script, "/user add name=pnmt group=pnmt-read address=192.0.2.10/32 password=[:rndstr length=32] comment=\"pnmt:7f3a\"");
        StringAssert.Contains(script, "/user ssh-keys import user=pnmt public-key-file=\"pnmt-1.pub\"");
        Assert.IsFalse(script.Contains("/interface wireguard add", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("private-key", StringComparison.Ordinal));
        AssertSafeToRepeat(script);
    }

    [TestMethod]
    public void WireGuard_script_holds_the_tunnel_and_its_key()
    {
        var script = JoinScript.Build(Settings(MikrotikMode.WireGuardSsh) with { ManagementAccess = true, Diagnostics = true, IncludeSecrets = true });
        StringAssert.Contains(script, "private-key=\"UGsL4e17bQnWs1rb8vOGl/gGTpPHz1aec5UTmTqv+Xw=\"");
        StringAssert.Contains(script, "allowed-address=10.253.0.0/30 persistent-keepalive=25s");
        StringAssert.Contains(script, "/ip route add dst-address=10.253.0.0/30 gateway=pnmt");
        StringAssert.Contains(script, "src-address=10.253.0.2 protocol=tcp dst-port=22,8291,80,443");
        StringAssert.Contains(script, "policy=ssh,read,sensitive,test");
        // M0: place-before=0 fails on an empty list, so the rules are moved instead.
        Assert.IsFalse(script.Contains("place-before", StringComparison.Ordinal));
        StringAssert.Contains(script, "/ip firewall filter move [find where comment=\"pnmt:7f3a\"]");
        StringAssert.Contains(script, "# It contains this router's WireGuard private key");
        AssertSafeToRepeat(script);
    }

    [TestMethod]
    public void WireGuard_only_has_no_user_and_no_SSH_rule()
    {
        var script = JoinScript.Build(Settings(MikrotikMode.WireGuardOnly));
        Assert.IsFalse(script.Contains("/user add", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("dst-port=22 ", StringComparison.Ordinal));
        StringAssert.Contains(script, "/interface wireguard add name=pnmt");
    }

    [TestMethod]
    public void Removal_script_removes_every_tagged_object()
    {
        var script = JoinScript.Removal("office", "pnmt.example.com", "pnmt:7f3a", DateTime.UtcNow);
        foreach (var menu in new[] { "/ip firewall filter", "/ip route", "/ip address", "/interface wireguard peers", "/interface wireguard", "/user", "/user group" })
        {
            StringAssert.Contains(script, $"{menu} remove [find where comment~\"^pnmt:\"]");
        }
        StringAssert.Contains(script, "/user ssh-keys remove [find where user=\"pnmt\"]");
    }

    /// <summary>What M0 found pasting the script again on 7.13 and 7.25.1.</summary>
    private static void AssertSafeToRepeat(string script)
    {
        var lines = script.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Every statement on one line: a :local ends with its line.
        foreach (var line in lines.Where(l => !l.StartsWith('#')))
        {
            Assert.AreEqual(line.Count(c => c == '{'), line.Count(c => c == '}'), line);
        }
        var keysRemoved = Array.FindIndex(lines, l => l.StartsWith("/user ssh-keys remove", StringComparison.Ordinal));
        var userRemoved = Array.FindIndex(lines, l => l.StartsWith("/user remove", StringComparison.Ordinal));
        Assert.IsTrue(keysRemoved >= 0 && keysRemoved < userRemoved, "7.13 keeps a removed user's keys");
        var add = Array.FindIndex(lines, l => l.StartsWith("/file add", StringComparison.Ordinal));
        if (add >= 0)
        {
            StringAssert.Contains(lines[add + 1], ":while ([:len [/file find where name=", "7.13 lists a new file only a moment later");
        }
        Assert.IsFalse(script.Contains("/file remove pnmt", StringComparison.Ordinal), "7.25 removes the key file itself");
    }

    // --- PNMTD's keys ------------------------------------------------------------------------------

    [TestMethod]
    public void Ed25519_keys_in_OpenSSH_formats()
    {
        var pair = SshKeys.GenerateEd25519("pnmt-abc123@pnmt.example.com");
        var parts = pair.PublicLine.Split(' ');
        Assert.AreEqual("ssh-ed25519", parts[0]);
        Assert.AreEqual("pnmt-abc123@pnmt.example.com", parts[2]);
        Assert.AreEqual(51, Convert.FromBase64String(parts[1]).Length);
        StringAssert.StartsWith(pair.PrivatePem, "-----BEGIN OPENSSH PRIVATE KEY-----\n");
        StringAssert.StartsWith(SshKeys.Fingerprint(pair.PublicLine), "SHA256:");
        // SSH.NET reads it, and derives the same public key.
        using var key = new Renci.SshNet.PrivateKeyFile(new MemoryStream(Encoding.ASCII.GetBytes(pair.PrivatePem)));
        Assert.AreEqual("ssh-ed25519", key.Key.ToString());
        Assert.AreNotEqual(pair.PublicLine, SshKeys.GenerateEd25519("x").PublicLine);
    }

    // --- the version catalog ------------------------------------------------------------------------

    [TestMethod]
    public void Newest_version_answers()
    {
        Assert.IsTrue(VersionCatalog.TryParse("7.25.1 1793623193\n", out var v));
        Assert.AreEqual("7.25.1", v.Version);
        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(1793623193), v.ReleasedAt);
        Assert.IsTrue(VersionCatalog.TryParse("7.24.6", out var noTime));
        Assert.IsNull(noTime.ReleasedAt);
        Assert.IsFalse(VersionCatalog.TryParse("<html>", out _));
        Assert.IsFalse(VersionCatalog.TryParse("", out _));
    }

    // --- remote command output (§8.3) ----------------------------------------------------------------------

    [TestMethod]
    public void Command_output_is_cleaned_of_terminal_sequences()
    {
        Assert.AreEqual("Flags: X\nred\n", CommandOutput.Clean("\u001b[mFlags: X\r\n\u001b[31mred\u001b[0m\r\n"));
        Assert.AreEqual("title gone\n", CommandOutput.Clean("\u001b]0;window\u0007title gone\n"), "OSC titles");
        Assert.AreEqual("a\tb\nc", CommandOutput.Clean("a\tb\rc\u0000\u0008"), "a lone CR is a line end; other controls go");
        Assert.AreEqual("grüezi 🦊\n", CommandOutput.Clean("grüezi 🦊\r\n"));
    }

    [TestMethod]
    public void RouterOS_errors_are_the_last_line()
    {
        // As M0 and M6 recorded them on 7.13 and 7.25.1.
        Assert.AreEqual("not enough permissions (9)", CommandOutput.Error("not enough permissions (9)\n"));
        Assert.AreEqual("bad command name nosuch (line 1 column 2)", CommandOutput.Error("bad command name nosuch (line 1 column 2)\n"));
        Assert.AreEqual("expected end of command (line 2 column 19)", CommandOutput.Error("expected end of command (line 2 column 19)\n"));
        Assert.AreEqual("input does not match any value of interface (/ip/address/add (interface); line 1)",
            CommandOutput.Error("before\ninput does not match any value of interface (/ip/address/add (interface); line 1)\n"));
        Assert.AreEqual("boom (:error; line 1)", CommandOutput.Error("boom (:error; line 1)\n"), ":error's own text");
        Assert.IsNull(CommandOutput.Error("Flags: D - DYNAMIC\n0 D 10.0.2.15/24\n"));
        Assert.IsNull(CommandOutput.Error("not enough permissions (9)\nbut it went on\n"), "only the last line: an error stops the script");
        Assert.IsNull(CommandOutput.Error("13:00:01 system,error login failure for user x\n"), "a log line is not an error");
        Assert.AreEqual("last", CommandOutput.LastLine("first\n  last  \n\n"));
        Assert.IsNull(CommandOutput.LastLine("\n \n"));
    }

    [TestMethod]
    public void Command_output_is_cut_at_a_character_boundary()
    {
        Assert.AreEqual(("abc", false), CommandOutput.Limit("abc", 3));
        Assert.AreEqual(("ab", true), CommandOutput.Limit("abc", 2));
        Assert.AreEqual(("a", true), CommandOutput.Limit("aü", 2), "ü takes two bytes");
        Assert.AreEqual(("a", true), CommandOutput.Limit("a🦊", 4), "the fox takes four bytes and two chars");
        Assert.AreEqual(("a🦊", false), CommandOutput.Limit("a🦊", 5));
    }

    [TestMethod]
    public void A_batch_is_grouped_by_output_the_largest_group_first()
    {
        var groups = CommandOutput.Group(new[] { ("r1", "x"), ("r2", "y"), ("r3", "y"), ("r4", "x"), ("r5", "y") }, r => r.Item2);
        Assert.AreEqual(2, groups.Count);
        CollectionAssert.AreEqual(new[] { "r2", "r3", "r5" }, groups[0].Select(r => r.Item1).ToArray());
        CollectionAssert.AreEqual(new[] { "r1", "r4" }, groups[1].Select(r => r.Item1).ToArray());
    }
}
