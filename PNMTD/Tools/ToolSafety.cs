using System.Text.Json;
using System.Text.Json.Nodes;
using PNMTD.Ai;
using PNMTD.Mcp;

namespace PNMTD.Tools;

/// <summary>What a result must not carry, in code (docs/mcp-plan.md §6.4).</summary>
public static class ToolSafety
{
    /// <summary>
    /// A sensor's parameters and the like, with every line that looks like a credential (<see cref="SecretScanner"/>) and
    /// every PNMT token hidden. People with Monitoring View see parameters in the UI; a machine gets them without these.
    /// </summary>
    public static string? WithoutCredentials(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        var findings = SecretScanner.Scan(text);
        if (findings.Count == 0 && !McpTokenValues.Anywhere().IsMatch(text))
        {
            return text;
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var finding in findings.Where(f => f.Line >= 1 && f.Line <= lines.Length))
        {
            lines[finding.Line - 1] = $"[hidden: looks like a {finding.Kind}]";
        }
        return McpTokenValues.Anywhere().Replace(string.Join('\n', lines), "[hidden: a PNMT token]");
    }

    /// <summary>
    /// A result whose shape is a service's own (an agent's sections, a Zabbix host, a router command's batch), made safe as a
    /// whole: every text that looks like instructions to an AI is withheld, every credential and PNMT token hidden
    /// (docs/mcp-plan.md §6.5). The rest stays as it is.
    /// </summary>
    public static JsonNode? Scanned(object? value)
    {
        var node = JsonSerializer.SerializeToNode(value, ToolResults.Json);
        if (node is null)
        {
            return null;
        }
        foreach (var leaf in Leaves(node).ToList())
        {
            if (!leaf.TryGetValue<string>(out var text) || text.Length < 12)
            {
                continue;
            }
            var cleaned = InputSanitizer.Clean(text, Math.Max(text.Length, 1));
            var replacement = InjectionScanner.Check(cleaned.Text, cleaned.InvisibleRemoved).Score >= InjectionScanner.BlockScore
                ? "[withheld: looks like instructions to an AI]"
                : WithoutCredentials(text);
            if (replacement != text)
            {
                switch (leaf.Parent)
                {
                    case JsonObject o:
                        o[leaf.GetPropertyName()] = replacement;
                        break;
                    case JsonArray a:
                        a[leaf.GetElementIndex()] = replacement;
                        break;
                }
            }
        }
        return node;
    }

    private static IEnumerable<JsonValue> Leaves(JsonNode node) => node switch
    {
        JsonValue v => [v],
        JsonObject o => o.Select(p => p.Value).Where(c => c is not null).SelectMany(c => Leaves(c!)),
        JsonArray a => a.Where(c => c is not null).SelectMany(c => Leaves(c!)),
        _ => [],
    };
}
