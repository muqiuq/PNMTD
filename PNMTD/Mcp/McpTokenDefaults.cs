namespace PNMTD.Mcp;

/// <summary>The names of the MCP token's authentication (docs/mcp-plan.md §5.2).</summary>
public static class McpTokenDefaults
{
    /// <summary>The scheme, and the principal's authentication type. Never a default scheme: only <c>/mcp</c> names it.</summary>
    public const string Scheme = "McpToken";
}
