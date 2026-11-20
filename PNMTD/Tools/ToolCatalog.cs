using PNMTD.Auth.Rights;
using PNMTD.Models.Db;
using PNMTD.Tools.Read;

namespace PNMTD.Tools;

/// <summary>
/// Every tool, for MCP and Cookie (docs/mcp-plan.md §6.1, §7). The rules about who gets what are here and nowhere else:
/// Cookie only reads, and only its own tools; the read-only token only reads and asks; nothing outside the token's
/// capped rights (<see cref="Mcp.McpRights"/>).
/// </summary>
public static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        .. SelfTools.All,
        .. MonitoringReadTools.All,
        .. KnowledgeReadTools.All,
        .. IntegrationReadTools.All,
        .. OperationsReadTools.All,
        .. AccessReadTools.All,
        .. NetworkReadTools.All,
        .. CookieCaseTools.All,
        .. CookieChatTools.All,
        .. Write.MonitoringWriteTools.All,
        .. Write.KnowledgeWriteTools.All,
        .. Write.NetworkWriteTools.All,
        .. Actions.CommandTools.All,
        .. Delete.DeleteTools.All,
    ];

    private static readonly Dictionary<string, ToolSpec> ByName = All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public static ToolSpec? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>Cookie's tools (docs/cookie-plan.md §3.5): reading, its own, within its rights.</summary>
    public static IEnumerable<ToolSpec> ForCookie(UserRights rights) =>
        All.Where(t => t.ForCookie && t.Kind == ToolKind.Read && t.Allows(rights, ToolCaller.Cookie));

    /// <summary>Whether a token of this kind is ever offered the tool, rights aside.</summary>
    public static bool KindAllows(ToolSpec tool, McpTokenKind kind) =>
        kind == McpTokenKind.Write || tool.Kind is ToolKind.Read or ToolKind.Ask;

    /// <summary>A token's tools: its kind's, within its capped rights.</summary>
    public static IEnumerable<ToolSpec> ForMcp(UserRights rights, McpTokenKind kind) =>
        All.Where(t => !t.CookieOnly && KindAllows(t, kind) && t.Allows(rights, ToolCaller.Mcp));
}
