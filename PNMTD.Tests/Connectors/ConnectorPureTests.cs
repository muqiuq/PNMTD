using System.Net;
using System.Text.Json;
using PNMTD.Ai;
using PNMTD.Connectors;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Connectors;

/// <summary>A caller's networks (docs/connectors-plan.md §6.3), pure.</summary>
[TestClass]
public sealed class ConnectorNetworksTests
{
    [TestMethod]
    public void A_single_address_becomes_a_32_or_a_128()
    {
        Assert.AreEqual(("10.20.0.17/32", (string?)null), ConnectorNetworks.Normalize("10.20.0.17"));
        Assert.AreEqual(("2001:db8:20::17/128", (string?)null), ConnectorNetworks.Normalize(" 2001:db8:20::17 "));
        Assert.AreEqual(("10.20.0.17/32", (string?)null), ConnectorNetworks.Normalize("::ffff:10.20.0.17"));
        Assert.AreEqual(("10.20.0.0/24", (string?)null), ConnectorNetworks.Normalize("10.20.0.0/24"));
        Assert.AreEqual(("0.0.0.0/0", (string?)null), ConnectorNetworks.Normalize("0.0.0.0/0"));
    }

    [TestMethod]
    public void Host_bits_are_refused_with_the_hint()
    {
        var (cidr, error) = ConnectorNetworks.Normalize("10.20.0.17/24");
        Assert.IsNull(cidr);
        Assert.AreEqual("A network starts at its first address: 10.20.0.0/24, or 10.20.0.17/32 for one address.", error);
        Assert.IsNotNull(ConnectorNetworks.Normalize("10.20.0.0/33").Error);
        Assert.IsNotNull(ConnectorNetworks.Normalize("web01").Error);
        Assert.IsNotNull(ConnectorNetworks.Normalize("").Error);
        Assert.IsNotNull(ConnectorNetworks.Normalize("fe80::1%eth0").Error);
    }

    [TestMethod]
    public void An_IPv4_mapped_caller_matches_an_IPv4_network_and_IPv6_needs_its_own()
    {
        ConnectorNetwork[] office = [new("10.20.0.0/24", "office"), new("2001:db8:20::/64", null)];
        Assert.IsTrue(ConnectorNetworks.Contains(office, IPAddress.Parse("::ffff:10.20.0.99")));
        Assert.IsTrue(ConnectorNetworks.Contains(office, IPAddress.Parse("10.20.0.1")));
        Assert.IsFalse(ConnectorNetworks.Contains(office, IPAddress.Parse("10.20.1.1")));
        Assert.IsTrue(ConnectorNetworks.Contains(office, IPAddress.Parse("2001:db8:20::abcd")));
        Assert.IsFalse(ConnectorNetworks.Contains(office, IPAddress.Parse("2001:db8:21::1")));
        Assert.IsFalse(ConnectorNetworks.Contains(office, null));

        // 0.0.0.0/0 alone doesn't let IPv6 in.
        ConnectorNetwork[] anyV4 = [new("0.0.0.0/0", null)];
        Assert.IsTrue(ConnectorNetworks.Contains(anyV4, IPAddress.Parse("203.0.113.9")));
        Assert.IsFalse(ConnectorNetworks.Contains(anyV4, IPAddress.Parse("2001:db8::1")));
        Assert.IsTrue(ConnectorNetworks.AnyAddress(anyV4));
        Assert.IsFalse(ConnectorNetworks.AnyAddress(office));
    }

    [TestMethod]
    public void Entries_inside_another_are_flagged_not_refused()
    {
        ConnectorNetwork[] networks = [new("10.0.0.0/8", null), new("10.20.0.0/24", null), new("10.20.0.0/24", null), new("192.168.1.0/24", null)];
        CollectionAssert.AreEqual(new[] { null, "10.0.0.0/8", "10.0.0.0/8", null }, ConnectorNetworks.Inside(networks).ToArray());
        ConnectorNetwork[] twins = [new("10.20.0.0/24", null), new("10.20.0.0/24", null)];
        CollectionAssert.AreEqual(new[] { null, "10.20.0.0/24" }, ConnectorNetworks.Inside(twins).ToArray());
    }
}

/// <summary>A call's arguments (§5), pure: the connector's choices, the JSON types, the characters, the patterns, the script's own checks.</summary>
[TestClass]
public sealed class ConnectorArgumentsTests
{
    private static ScriptParameter P(string name, string type = "string", bool mandatory = false, IReadOnlyList<string>? set = null, double? min = null,
        double? max = null, bool isSwitch = false, bool secure = false) => new(name, type, mandatory, null, null, set, min, max, null, isSwitch, secure);

    private static ConnectorArgumentRule Exposed(ScriptParameter p, string? pattern = null, bool multiline = false, bool required = false) =>
        new(p.Name, ConnectorArgumentMode.Exposed, required, pattern, null, multiline, p, null, 0);

    private static readonly IReadOnlyList<ConnectorArgumentRule> Shop =
    [
        Exposed(P("Site", mandatory: true), "shop-[a-z0-9]{2,20}"),
        Exposed(P("Scope", set: ["Pages", "Images", "All"])),
        Exposed(P("Days", "int", min: 1, max: 365)),
        Exposed(P("Note"), multiline: true),
        new("DryRun", ConnectorArgumentMode.Fixed, false, null, JsonSerializer.SerializeToElement(false), false, P("DryRun", "switch", isSwitch: true), null, 4),
        new("Force", ConnectorArgumentMode.Omitted, false, null, null, false, P("Force", "switch", isSwitch: true), null, 5),
        Exposed(P("Token", "securestring", secure: true), "[A-Za-z0-9]{8,}"),
    ];

    private static ConnectorArgumentCheck Check(string json) =>
        ConnectorArguments.Check(Shop, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!, 4000, 100);

    [TestMethod]
    public void A_good_call_passes_with_the_fixed_value_and_the_secret_apart()
    {
        var check = Check("""{"Site":"shop-de","Scope":"Pages","Days":7,"Token":"abcd1234EF"}""");
        Assert.IsTrue(check.Ok, string.Join("; ", check.Fields));
        CollectionAssert.AreEquivalent(new[] { "Site", "Scope", "Days", "DryRun" }, check.Plain.Keys.ToList());
        Assert.IsFalse(check.Plain["DryRun"].GetBoolean());
        CollectionAssert.AreEquivalent(new[] { "Site", "Scope", "Days" }, check.CallerPlain.Keys.ToList());
        Assert.AreEqual("abcd1234EF", check.Secure["Token"]);
        Assert.IsFalse(JsonSerializer.Serialize(check.CallerPlain).Contains("abcd1234EF"));
    }

    [TestMethod]
    public void Unknown_fixed_omitted_and_missing_arguments_are_each_listed()
    {
        var check = Check("""{"Sitee":"shop-de","DryRun":true,"Force":true}""");
        Assert.IsFalse(check.Ok);
        Assert.AreEqual("not an argument of this connector", check.Fields["Sitee"]);
        Assert.AreEqual("set by PNMT: it can't be passed", check.Fields["DryRun"]);
        Assert.AreEqual("not an argument of this connector", check.Fields["Force"]);
        Assert.AreEqual("required", check.Fields["Site"]);
        Assert.AreEqual("4 arguments are not allowed.", check.Message);
    }

    [TestMethod]
    public void The_JSON_type_must_match_the_parameter()
    {
        Assert.AreEqual("must be a number", Check("""{"Site":"shop-de","Days":"7"}""").Fields["Days"]);
        Assert.AreEqual("must be a string", Check("""{"Site":7}""").Fields["Site"]);
        Assert.AreEqual("arrays and objects aren't allowed", Check("""{"Site":["shop-de"]}""").Fields["Site"]);
        Assert.AreEqual("null isn't a value: leave the argument out", Check("""{"Site":null}""").Fields["Site"]);
        var flag = new[] { Exposed(P("Verbose", "switch", isSwitch: true)) };
        Assert.AreEqual("must be true or false",
            ConnectorArguments.Check(flag, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"Verbose":"true"}""")!, 4000, 100).Fields["Verbose"]);
    }

    [TestMethod]
    public void A_pattern_matches_the_whole_value_only_and_the_error_names_the_pattern_never_the_value()
    {
        var check = Check("""{"Site":"shop-de; Remove-Item C:\\"}""");
        Assert.AreEqual("doesn't match the pattern shop-[a-z0-9]{2,20}", check.Fields["Site"]);
        Assert.IsFalse(check.Message.Contains("Remove-Item"));
        Assert.IsFalse(check.Fields.Values.Any(v => v.Contains("Remove-Item")));
        var anchored = new[] { Exposed(P("Name"), "[a-z]+") };
        Assert.IsFalse(ConnectorArguments.Check(anchored, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"Name":"abc; rm"}""")!, 4000, 100).Ok);
        Assert.IsTrue(ConnectorArguments.Check(anchored, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"Name":"abc"}""")!, 4000, 100).Ok);
        // ^…$ as the admin may write it loses nothing.
        var caret = new[] { Exposed(P("Name"), "^[a-z]+$") };
        Assert.IsTrue(ConnectorArguments.Check(caret, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"Name":"abc"}""")!, 4000, 100).Ok);
    }

    [TestMethod]
    public void A_secure_value_is_matched_and_never_echoed()
    {
        var check = Check("""{"Site":"shop-de","Token":"short"}""");
        Assert.AreEqual("doesn't match the pattern [A-Za-z0-9]{8,}", check.Fields["Token"]);
        Assert.AreEqual(0, check.Secure.Count);
        Assert.IsFalse(check.CallerPlain.ContainsKey("Token"));
    }

    [TestMethod]
    public void A_pattern_that_needs_backtracking_gets_a_timeout_and_a_timeout_refuses()
    {
        var (regex, error) = ConnectorPatterns.Compile(@"(a+)+\1b", 1);
        Assert.IsNull(error);
        Assert.IsFalse(regex!.Options.HasFlag(System.Text.RegularExpressions.RegexOptions.NonBacktracking));
        var rules = new[] { Exposed(P("Name"), @"(a+)+\1b") };
        var check = ConnectorArguments.Check(rules, new Dictionary<string, JsonElement> { ["Name"] = JsonSerializer.SerializeToElement(new string('a', 4000)) }, 4000, 1);
        StringAssert.Contains(check.Fields["Name"], "pattern_timeout");
        Assert.IsTrue(ConnectorPatterns.Compile("shop-[a-z]+", 100).Regex!.Options.HasFlag(System.Text.RegularExpressions.RegexOptions.NonBacktracking));
        Assert.IsNotNull(ConnectorPatterns.Compile("shop-[", 100).Error);
    }

    [TestMethod]
    public void Control_and_bidi_characters_are_refused_and_line_breaks_only_where_allowed()
    {
        Assert.AreEqual("holds a control character", Check("{\"Site\":\"shop-de\",\"Scope\":\"Pages\\u0000\"}").Fields["Scope"]);
        Assert.AreEqual("holds an invisible or a direction character", Check("{\"Site\":\"shop-de\",\"Scope\":\"Pa\\u202Eges\"}").Fields["Scope"]);
        Assert.AreEqual("holds a line break or a tab, which this argument doesn't allow", Check("{\"Site\":\"shop-de\",\"Scope\":\"Pages\\n\"}").Fields["Scope"]);
        var multi = Check("{\"Site\":\"shop-de\",\"Note\":\"line one\\r\\nline two\\n\\tthree\"}");
        Assert.IsTrue(multi.Ok, string.Join("; ", multi.Fields));
        Assert.AreEqual("line one\r\nline two\n\tthree", multi.Plain["Note"].GetString());
        Assert.AreEqual("holds a control character", Check("{\"Site\":\"shop-de\",\"Note\":\"bell\\u0007\"}").Fields["Note"]);
        Assert.AreEqual("holds a line break or a tab, which this argument doesn't allow", Check("{\"Site\":\"shop-de\",\"Note\":\"a lone\\rreturn\"}").Fields["Note"]);
        Assert.AreEqual("longer than 4000 characters",
            ConnectorArguments.Check(Shop, new Dictionary<string, JsonElement> { ["Site"] = JsonSerializer.SerializeToElement(new string('x', 4001)) }, 4000, 100).Fields["Site"]);
    }

    [TestMethod]
    public void The_scripts_own_set_and_range_still_apply()
    {
        Assert.AreEqual("scope must be one of Pages, Images, All", Check("""{"Site":"shop-de","Scope":"Everything"}""").Fields["Scope"]);
        Assert.AreEqual("days must be between 1 and 365", Check("""{"Site":"shop-de","Days":400}""").Fields["Days"]);
    }

    [TestMethod]
    public void A_changed_parse_reconciles_the_choices_by_name()
    {
        var id = Guid.NewGuid();
        var rows = new List<ConnectorArgumentEntity>
        {
            ConnectorArguments.New(id, P("Site", mandatory: true), 0),
            ConnectorArguments.New(id, P("Scope", set: ["Pages", "All"]), 1),
            ConnectorArguments.New(id, P("Old"), 2),
        };
        rows[1].Mode = ConnectorArgumentMode.Fixed;
        rows[1].FixedValue = "\"All\"";
        rows[0].Pattern = "shop-[a-z]+";
        // Scope's set loses All; Old goes; Region comes, mandatory; Verbose comes, optional.
        var reconciled = ConnectorArguments.Reconcile(id, rows,
            [P("Site", mandatory: true), P("Scope", set: ["Pages", "Images"]), P("Region", mandatory: true), P("Verbose", "switch", isSwitch: true)]);
        CollectionAssert.AreEqual(new[] { "Site", "Scope", "Region", "Verbose" }, rows.OrderBy(r => r.Order).Select(r => r.Name).ToArray());
        Assert.AreEqual(ConnectorArgumentMode.Exposed, rows.Single(r => r.Name == "Region").Mode);
        Assert.IsTrue(rows.Single(r => r.Name == "Region").Required);
        Assert.AreEqual(ConnectorArgumentMode.Omitted, rows.Single(r => r.Name == "Verbose").Mode);
        Assert.AreEqual("shop-[a-z]+", rows.Single(r => r.Name == "Site").Pattern);
        Assert.AreEqual("Old", reconciled.Removed.Single().Name);
        Assert.IsTrue(reconciled.NeedsReview);
        StringAssert.Contains(reconciled.Misfits.Single(), "Scope's fixed value");
    }
}

/// <summary>The caller's key (§6.2), pure: its shape, its hash, and the secret scanner knowing it.</summary>
[TestClass]
public sealed class ConnectorKeyTests
{
    [TestMethod]
    public void The_key_has_its_shape_and_the_scanner_catches_it()
    {
        var key = ConnectorKeys.New();
        Assert.AreEqual(51, key.Length);
        StringAssert.StartsWith(key, "pnmt_cx_");
        Assert.IsTrue(ConnectorKeys.LooksLikeOne(key));
        Assert.IsFalse(ConnectorKeys.LooksLikeOne(key[..^1]));
        Assert.IsFalse(ConnectorKeys.LooksLikeOne("pnmt_ro_" + key[8..]));
        Assert.AreEqual(64, ConnectorKeys.Hash(key).Length);
        Assert.AreNotEqual(ConnectorKeys.Hash(key), ConnectorKeys.Hash(ConnectorKeys.New()));
        Assert.AreEqual(key[^4..], ConnectorKeys.Hint(key));
        Assert.AreEqual(1, SecretScanner.Scan($"the back office's key is {key}, keep it safe").Count);
    }
}
