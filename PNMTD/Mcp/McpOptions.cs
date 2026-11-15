namespace PNMTD.Mcp;

/// <summary>The MCP server's settings (docs/mcp-plan.md §11.3), from <c>Mcp__…</c>.</summary>
public sealed class McpOptions
{
    public const string Section = "Mcp";

    /// <summary>The server at <c>/mcp</c> and the MCP panel on Account → Security. Off until set.</summary>
    public bool Enabled { get; set; }

    /// <summary>How long the read-write token stays on once switched on.</summary>
    public int WriteHours { get; set; } = 12;

    /// <summary>The read-only token stays on until this many days after its user was last online in the web UI.</summary>
    public int ReadDaysAfterOnline { get; set; } = 7;

    /// <summary>The read-write token needs 2FA on the account.</summary>
    public bool WriteRequiresTotp { get; set; } = true;

    /// <summary>The MCP log keeps the newest this many entries.</summary>
    public int LogEntries { get; set; } = 10_000;

    public int MaxResultChars { get; set; } = 40_000;

    public int CallsPerMinute { get; set; } = 120;

    public int WritesPerMinute { get; set; } = 20;

    /// <summary>How long "Allow deleting" lasts (§7.5).</summary>
    public int DeleteMinutes { get; set; } = 10;

    /// <summary>The most objects deleted in one window.</summary>
    public int DeletesPerWindow { get; set; } = 20;

    public int CookieMessagesPerHour { get; set; } = 10;

    public int CookieWaitSeconds { get; set; } = 60;

    /// <summary>This many strikes within 24 hours block a token and alert the admins (§8.6).</summary>
    public int StrikesToBlock { get; set; } = 3;
}
