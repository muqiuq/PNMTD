using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace PNMTD.Lib.Agent;

/// <summary>
/// Bash scripts' parameters (docs/linux-sensor-plan.md §8.2): a YAML block in comments that PNMTD and the agent read without
/// running anything. It starts with a line <c># pnmt:</c> within the first 50 lines and runs while lines start with <c>#</c>:
/// <c>synopsis</c>, <c>description</c> and <c>parameters</c> (a map from name to type, mandatory, default, set, range and help).
/// Each parameter reaches the script as <c>PNMT_&lt;NAME&gt;</c>. Header errors block runs; the checks of §8.3 are findings.
/// </summary>
public static partial class BashScriptHeader
{
    public const int MaxHeaderBytes = 64 * 1024;
    public const int HeaderWithinLines = 50;

    /// <summary>The types: text, whole numbers, numbers, a checkbox (true or false), and a password field (encrypted end to end).</summary>
    public static readonly IReadOnlyList<string> Types = ["string", "int", "number", "bool", "secret"];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex NamePattern();

    // $PNMT_X, ${PNMT_X}, ${PNMT_X:-…}, ${#PNMT_X} (its length) and ${!PNMT_X} (indirection) are all uses.
    [GeneratedRegex(@"\$(?:\{[#!]?)?PNMT_([A-Z0-9_]+)")]
    private static partial Regex EnvironmentUse();

    [GeneratedRegex(@"(^|[\s;|&(])eval\s")]
    private static partial Regex Eval();

    [GeneratedRegex(@"(^|[\s;|&(])sudo\s")]
    private static partial Regex Sudo();

    /// <summary>The environment variable a parameter becomes: <c>keep_days</c> → <c>PNMT_KEEP_DAYS</c>.</summary>
    public static string EnvironmentName(string parameter) => "PNMT_" + parameter.ToUpperInvariant();

    /// <summary>A new bash script in the library (§8.1): the header, strict mode, and the parameters used as variables.</summary>
    public const string Template = """
        #!/usr/bin/env bash
        # pnmt:
        #   synopsis: What this script does, in one line
        #   description: |
        #     What it changes, and what it needs. It runs as the account chosen in PNMT's run dialog.
        #   parameters:
        #     target:  { type: string, mandatory: true, help: "What to work on" }
        #     days:    { type: int, default: 7, range: [1, 365] }
        #     dry_run: { type: bool, help: "Only say what would change" }
        set -euo pipefail

        echo "Working on ${PNMT_TARGET}, ${PNMT_DAYS} days"
        if [[ "${PNMT_DRY_RUN:-false}" == true ]]; then
          echo "Dry run: nothing changed."
          exit 0
        fi
        """;

    /// <summary>The script's parameters and help from its header, the header's errors, and the findings of §8.3.</summary>
    public static ParsedScript Parse(string text)
    {
        var errors = new List<ScriptSyntaxError>();
        var findings = new List<ScriptFinding>();
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n');
        var parameters = new List<ScriptParameter>();
        ScriptHelp? help = null;

        var start = Array.FindIndex(lines, 0, Math.Min(lines.Length, HeaderWithinLines), l => l.Trim() is "# pnmt:" or "#pnmt:");
        if (start >= 0)
        {
            var block = new List<string>();
            for (var i = start + 1; i < lines.Length && lines[i].StartsWith('#'); i++)
            {
                var line = lines[i][1..];
                block.Add(line.StartsWith(' ') ? line[1..] : line);
            }
            var yaml = string.Join('\n', block);
            if (yaml.Length > MaxHeaderBytes)
            {
                errors.Add(new ScriptSyntaxError($"The # pnmt: header is larger than {MaxHeaderBytes / 1024} KB.", start + 1, 1));
            }
            else
            {
                (parameters, help) = ReadHeader(yaml, start + 2, errors);
            }
        }
        Check(lines, parameters, start, findings);
        return new ParsedScript(parameters, help, errors, findings);
    }

    private static (List<ScriptParameter>, ScriptHelp?) ReadHeader(string yaml, int firstLine, List<ScriptSyntaxError> errors)
    {
        var parameters = new List<ScriptParameter>();
        if (yaml.Trim().Length == 0)
        {
            return (parameters, null);
        }
        YamlMappingNode root;
        try
        {
            // Anchors and aliases are refused (§8.2): a header is a handful of plain values, and aliases are how YAML bombs work.
            var parser = new Parser(new StringReader(yaml));
            while (parser.MoveNext())
            {
                if (parser.Current is AnchorAlias || parser.Current is NodeEvent { Anchor.IsEmpty: false })
                {
                    errors.Add(new ScriptSyntaxError("The header uses YAML anchors or aliases, which are not allowed.", firstLine + (int)parser.Current.Start.Line - 1, 1));
                    return (parameters, null);
                }
            }
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                errors.Add(new ScriptSyntaxError("The # pnmt: header is not a map of synopsis, description and parameters.", firstLine, 1));
                return (parameters, null);
            }
            root = mapping;
        }
        catch (YamlException e)
        {
            errors.Add(new ScriptSyntaxError($"The header is not valid YAML: {e.Message}", firstLine + Math.Max(0, (int)e.Start.Line - 1), (int)Math.Max(1, e.Start.Column)));
            return (parameters, null);
        }

        string? synopsis = null, description = null;
        var parameterHelp = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (keyNode, value) in root.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? "";
            var line = firstLine + (int)keyNode.Start.Line - 1;
            switch (key)
            {
                case "synopsis":
                    synopsis = Scalar(value);
                    break;
                case "description":
                    description = Scalar(value)?.TrimEnd();
                    break;
                case "parameters":
                    if (value is YamlMappingNode map)
                    {
                        foreach (var (nameNode, spec) in map.Children)
                        {
                            if (ReadParameter(nameNode, spec, parameters.Count, firstLine, errors) is { } parameter)
                            {
                                if (parameters.Any(p => p.Name == parameter.Name))
                                {
                                    errors.Add(new ScriptSyntaxError($"The parameter {parameter.Name} is declared twice.", firstLine + (int)nameNode.Start.Line - 1, 1));
                                    continue;
                                }
                                parameters.Add(parameter);
                                if (parameter.HelpMessage is { } h)
                                {
                                    parameterHelp[parameter.Name] = h;
                                }
                            }
                        }
                    }
                    else if (value is not YamlScalarNode { Value: null or "" })
                    {
                        errors.Add(new ScriptSyntaxError("parameters: a map from each parameter's name to its type and rules.", line, 1));
                    }
                    break;
                default:
                    errors.Add(new ScriptSyntaxError($"The header has no key \"{key}\" (synopsis, description, parameters).", line, 1));
                    break;
            }
        }
        return (parameters, synopsis is null && description is null && parameterHelp.Count == 0 ? null : new ScriptHelp(synopsis, description, parameterHelp));
    }

    private static ScriptParameter? ReadParameter(YamlNode nameNode, YamlNode spec, int position, int firstLine, List<ScriptSyntaxError> errors)
    {
        var name = (nameNode as YamlScalarNode)?.Value ?? "";
        var line = firstLine + (int)nameNode.Start.Line - 1;
        if (!NamePattern().IsMatch(name))
        {
            errors.Add(new ScriptSyntaxError($"\"{name}\" is not a parameter name: lower-case letters, digits and _, starting with a letter, at most 40.", line, 1));
            return null;
        }
        var fields = spec switch
        {
            YamlMappingNode m => m.Children.ToDictionary(p => (p.Key as YamlScalarNode)?.Value ?? "", p => p.Value, StringComparer.Ordinal),
            YamlScalarNode { Value: { Length: > 0 } typeOnly } => new Dictionary<string, YamlNode>(StringComparer.Ordinal) { ["type"] = new YamlScalarNode(typeOnly) },
            _ => null,
        };
        if (fields is null)
        {
            errors.Add(new ScriptSyntaxError($"{name}: {{ type: …, mandatory: …, default: …, set: […], range: [min, max], help: … }}.", line, 1));
            return null;
        }
        foreach (var unknown in fields.Keys.Where(k => k is not ("type" or "mandatory" or "default" or "set" or "range" or "help")))
        {
            errors.Add(new ScriptSyntaxError($"{name}: no such rule \"{unknown}\" (type, mandatory, default, set, range, help).", line, 1));
        }
        var type = fields.TryGetValue("type", out var t) ? Scalar(t) ?? "string" : "string";
        if (!Types.Contains(type))
        {
            errors.Add(new ScriptSyntaxError($"{name}: the type {type} is not one of {string.Join(", ", Types)}.", line, 1));
            return null;
        }
        var mandatory = false;
        if (fields.TryGetValue("mandatory", out var m2))
        {
            if (Scalar(m2) is not ("true" or "false") || !bool.TryParse(Scalar(m2), out mandatory))
            {
                errors.Add(new ScriptSyntaxError($"{name}: mandatory is true or false.", line, 1));
            }
        }
        IReadOnlyList<string>? set = null;
        if (fields.TryGetValue("set", out var s))
        {
            if (s is YamlSequenceNode seq && seq.Children.All(c => c is YamlScalarNode) && seq.Children.Count > 0)
            {
                set = seq.Children.Select(c => ((YamlScalarNode)c).Value ?? "").ToList();
            }
            else
            {
                errors.Add(new ScriptSyntaxError($"{name}: set is a list of values, [a, b, c].", line, 1));
            }
        }
        double? min = null, max = null;
        if (fields.TryGetValue("range", out var r))
        {
            if (type is not ("int" or "number"))
            {
                errors.Add(new ScriptSyntaxError($"{name}: a range needs the type int or number.", line, 1));
            }
            else if (r is YamlSequenceNode { Children.Count: 2 } pair && Number(pair.Children[0]) is { } a && Number(pair.Children[1]) is { } b && a <= b)
            {
                (min, max) = (a, b);
            }
            else
            {
                errors.Add(new ScriptSyntaxError($"{name}: range is [min, max] with min ≤ max.", line, 1));
            }
        }
        string? @default = null;
        if (fields.TryGetValue("default", out var d))
        {
            @default = Scalar(d);
            if (type == "secret")
            {
                errors.Add(new ScriptSyntaxError($"{name}: a secret has no default.", line, 1));
                @default = null;
            }
            else if (@default is not null && DefaultProblem(type, @default, set, min, max) is { } problem)
            {
                errors.Add(new ScriptSyntaxError($"{name}: the default {problem}.", line, 1));
            }
        }
        var helpText = fields.TryGetValue("help", out var h) ? Scalar(h) : null;
        return new ScriptParameter(name, type, mandatory, @default, position, set, min, max, helpText, type == "bool", type == "secret");
    }

    private static string? DefaultProblem(string type, string value, IReadOnlyList<string>? set, double? min, double? max)
    {
        switch (type)
        {
            case "bool" when value is not ("true" or "false"):
                return "of a bool is true or false";
            case "int" when !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
                return "is not a whole number";
            case "number" when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                return "is not a number";
        }
        if (set is not null && !set.Contains(value))
        {
            return "is not in the set";
        }
        if ((min is not null || max is not null) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && (n < min || n > max))
        {
            return "is outside the range";
        }
        return null;
    }

    /// <summary>The checks of §8.3, as findings: bash's shebang, strict mode, eval, PNMT_ variables, sudo, secrets echoed.</summary>
    private static void Check(string[] lines, List<ScriptParameter> parameters, int headerStart, List<ScriptFinding> findings)
    {
        var first = lines.FirstOrDefault() ?? "";
        if (!first.StartsWith("#!", StringComparison.Ordinal) || !first.Contains("bash", StringComparison.Ordinal))
        {
            findings.Add(new ScriptFinding("Shebang", "The first line is not #!/usr/bin/env bash (or #!/bin/bash); the agent runs it with bash anyway.", 1));
        }
        var code = lines.Select((l, i) => (Text: l, Number: i + 1)).Where(l => !l.Text.TrimStart().StartsWith('#')).ToList();
        var strict = code.Any(l => StrictMode(l.Text));
        if (!strict)
        {
            findings.Add(new ScriptFinding("StrictMode", "No set -euo pipefail (or at least -e and -u): a failing command or an unset variable goes unnoticed."));
        }
        foreach (var line in code.Where(l => Eval().IsMatch(l.Text)).Take(5))
        {
            findings.Add(new ScriptFinding("Eval", "eval runs text as code: arguments can turn into commands.", line.Number));
        }
        foreach (var line in code.Where(l => Sudo().IsMatch(l.Text)).Take(5))
        {
            findings.Add(new ScriptFinding("Sudo", "sudo in a script: it already runs as the account chosen in the run dialog.", line.Number));
        }
        var declared = parameters.ToDictionary(p => EnvironmentName(p.Name)[5..], p => p, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in code)
        {
            foreach (Match match in EnvironmentUse().Matches(line.Text))
            {
                var name = match.Groups[1].Value;
                used.Add(name);
                if (!declared.ContainsKey(name))
                {
                    findings.Add(new ScriptFinding("Undeclared", $"PNMT_{name} is used but not declared in the # pnmt: header.", line.Number));
                }
                else if (declared[name].IsSecure && line.Text.TrimStart().StartsWith("echo", StringComparison.Ordinal))
                {
                    findings.Add(new ScriptFinding("SecretEchoed", $"The secret PNMT_{name} is echoed: it would end up in the run's output.", line.Number));
                }
            }
        }
        foreach (var unused in declared.Keys.Where(n => !used.Contains(n)))
        {
            findings.Add(new ScriptFinding("Unused", $"The parameter {declared[unused].Name} is declared but PNMT_{unused} is never used.",
                headerStart >= 0 ? headerStart + 1 : null));
        }
    }

    /// <summary>A <c>set</c> line that turns on errexit and nounset: <c>set -euo pipefail</c>, <c>set -e -u</c>, <c>set -o errexit -o nounset</c>.</summary>
    private static bool StrictMode(string line)
    {
        var words = line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words[0] != "set")
        {
            return false;
        }
        bool e = false, u = false;
        for (var i = 1; i < words.Length; i++)
        {
            var w = words[i];
            if (w == "-o" && i + 1 < words.Length)
            {
                e |= words[i + 1] == "errexit";
                u |= words[i + 1] == "nounset";
                i++;
            }
            else if (w.StartsWith('-') && !w.StartsWith("--", StringComparison.Ordinal))
            {
                e |= w.Contains('e');
                u |= w.Contains('u');
            }
        }
        return e && u;
    }

    private static string? Scalar(YamlNode node) => node is YamlScalarNode s ? s.Value : null;

    private static double? Number(YamlNode node) =>
        node is YamlScalarNode { Value: { } v } && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// The arguments as systemd's EnvironmentFile lines (§8.4): <c>PNMT_NAME="value"</c>, double-quoted with backslashes and quotes
    /// escaped, so nothing in a value can end it or become a second variable. systemd expands nothing in these files, and a line
    /// break inside the quotes stays part of the value. Booleans are <c>true</c> or <c>false</c>.
    /// </summary>
    public static string EnvironmentFile(IReadOnlyDictionary<string, string> values)
    {
        var text = new System.Text.StringBuilder();
        foreach (var (name, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            text.Append(EnvironmentName(name)).Append("=\"");
            foreach (var c in value)
            {
                text.Append(c switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    _ => c.ToString(),
                });
            }
            text.Append("\"\n");
        }
        return text.ToString();
    }
}
