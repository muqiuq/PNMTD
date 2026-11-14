using System.Text.Json;
using PNMTD.Lib.Agent;

namespace PNMTD.Tests.Linux;

/// <summary>
/// Bash scripts' headers (docs/linux-sensor-plan.md §8.2, §8.3, §14): the parameters with their types and rules, the arguments
/// checked against them with the same code as PowerShell's, the header's errors (which block runs), the findings, and the
/// environment file.
/// </summary>
[TestClass]
public sealed class BashScriptHeaderTests
{
    private const string Script = """
        #!/usr/bin/env bash
        # pnmt:
        #   synopsis: Rotate a site's logs and reload nginx
        #   description: Compresses logs older than a day and deletes those older than keep_days.
        #   parameters:
        #     site:      { type: string, mandatory: true, set: [www, api, shop], help: "The site" }
        #     keep_days: { type: int, default: 14, range: [1, 365] }
        #     dry_run:   { type: bool }
        #     api_token: { type: secret, help: "The CDN's token, to purge its cache" }
        set -euo pipefail

        echo "Rotating ${PNMT_SITE}, keeping ${PNMT_KEEP_DAYS} days"
        [[ "${PNMT_DRY_RUN:-false}" == true ]] && exit 0
        curl -s -H "Authorization: Bearer ${PNMT_API_TOKEN:-}" https://cdn.example.com/purge
        """;

    [TestMethod]
    public void The_header_becomes_typed_parameters_and_help()
    {
        var parsed = BashScriptHeader.Parse(Script);

        Assert.AreEqual(0, parsed.Errors.Count, string.Join("; ", parsed.Errors.Select(e => e.Message)));
        Assert.AreEqual(0, parsed.Findings.Count, string.Join("; ", parsed.Findings.Select(f => f.Message)));
        Assert.AreEqual("Rotate a site's logs and reload nginx", parsed.Help!.Synopsis);
        Assert.AreEqual(4, parsed.Parameters.Count);
        var site = parsed.Parameters[0];
        Assert.AreEqual(("site", "string", true), (site.Name, site.Type, site.Mandatory));
        CollectionAssert.AreEqual(new[] { "www", "api", "shop" }, site.ValidateSet!.ToArray());
        Assert.AreEqual("The site", site.HelpMessage);
        var days = parsed.Parameters[1];
        Assert.AreEqual(("14", 1.0, 365.0), (days.Default, days.RangeMin, days.RangeMax));
        Assert.IsTrue(parsed.Parameters[2].IsSwitch);
        Assert.IsTrue(parsed.Parameters[3].IsSecure);
    }

    [TestMethod]
    public void Arguments_are_checked_against_the_header_as_for_PowerShell()
    {
        var parsed = BashScriptHeader.Parse(Script);
        var bad = new Dictionary<string, JsonElement>
        {
            ["site"] = JsonSerializer.SerializeToElement("blog"),
            ["keep_days"] = JsonSerializer.SerializeToElement(400),
            ["dry_run"] = JsonSerializer.SerializeToElement("yes"),
            ["nope"] = JsonSerializer.SerializeToElement(1),
        };
        var good = new Dictionary<string, JsonElement> { ["site"] = JsonSerializer.SerializeToElement("www") };

        var problems = ScriptArguments.Check(parsed, bad, []).Select(p => p.Field).ToList();

        CollectionAssert.AreEquivalent(new[] { "arguments.nope", "arguments.site", "arguments.keep_days", "arguments.dry_run" }, problems);
        Assert.AreEqual(0, ScriptArguments.Check(parsed, good, ["api_token"]).Count());
        Assert.AreEqual(1, ScriptArguments.Check(parsed, new Dictionary<string, JsonElement>(), []).Count(), "site is mandatory");
    }

    [TestMethod]
    [DataRow("#     bad-name: { type: string }", "is not a parameter name")]
    [DataRow("#     n: { type: date }", "is not one of")]
    [DataRow("#     n: { type: int, default: abc }", "is not a whole number")]
    [DataRow("#     n: { type: string, range: [1, 2] }", "a range needs the type int or number")]
    [DataRow("#     n: { type: secret, default: x }", "a secret has no default")]
    [DataRow("#     n: { type: string, colour: red }", "no such rule")]
    [DataRow("#     n: &a { type: string }", "anchors or aliases")]
    [DataRow("#     n: { type: string", "not valid YAML")]
    public void Header_errors_are_reported_with_their_line(string parameterLine, string expected)
    {
        var text = $"#!/bin/bash\n# pnmt:\n#   parameters:\n{parameterLine}\nset -eu\necho ${{PNMT_N:-}}\n";

        var parsed = BashScriptHeader.Parse(text);

        Assert.IsTrue(parsed.Errors.Any(e => e.Message.Contains(expected, StringComparison.Ordinal)),
            string.Join("; ", parsed.Errors.Select(e => e.Message)));
    }

    [TestMethod]
    public void A_script_without_a_header_has_no_parameters_and_the_findings_of_section_8_3()
    {
        var parsed = BashScriptHeader.Parse("#!/bin/sh\neval \"$1\"\nsudo systemctl restart nginx\necho $PNMT_HOST\n");

        Assert.AreEqual(0, parsed.Parameters.Count);
        Assert.AreEqual(0, parsed.Errors.Count);
        CollectionAssert.IsSubsetOf(new[] { "Shebang", "StrictMode", "Eval", "Sudo", "Undeclared" }, parsed.Findings.Select(f => f.Rule).ToArray());
    }

    [TestMethod]
    public void An_echoed_secret_and_an_unused_parameter_are_findings()
    {
        var parsed = BashScriptHeader.Parse("#!/usr/bin/env bash\n# pnmt:\n#   parameters:\n#     token: { type: secret }\n#     unused: { type: string }\nset -euo pipefail\necho \"${PNMT_TOKEN}\"\n");

        CollectionAssert.IsSubsetOf(new[] { "SecretEchoed", "Unused" }, parsed.Findings.Select(f => f.Rule).ToArray());
    }

    [TestMethod]
    public void A_length_or_an_indirection_counts_as_a_use()
    {
        var parsed = BashScriptHeader.Parse("#!/usr/bin/env bash\n# pnmt:\n#   parameters:\n#     name: { type: string }\n#     ref: { type: string }\nset -euo pipefail\n(( ${#PNMT_NAME} > 3 )) && echo \"${!PNMT_REF}\"\n");

        Assert.AreEqual(0, parsed.Findings.Count, string.Join("; ", parsed.Findings.Select(f => f.Message)));
    }

    [TestMethod]
    public void The_environment_file_quotes_every_value()
    {
        var text = BashScriptHeader.EnvironmentFile(new Dictionary<string, string>
        {
            ["site"] = "www",
            ["note"] = "say \"hi\" \\ $HOME\nPNMT_X=1",
        });

        Assert.AreEqual("PNMT_NOTE=\"say \\\"hi\\\" \\\\ $HOME\nPNMT_X=1\"\nPNMT_SITE=\"www\"\n", text);
    }

    [TestMethod]
    public void The_template_parses_cleanly() =>
        Assert.AreEqual(0, BashScriptHeader.Parse(BashScriptHeader.Template).Errors.Count + BashScriptHeader.Parse(BashScriptHeader.Template).Findings.Count);
}
