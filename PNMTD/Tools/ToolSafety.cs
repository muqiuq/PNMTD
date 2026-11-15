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
}
