using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Ai;
using PNMTD.Auth.Rights;
using PNMTD.Models.Db;

namespace PNMTD.Tools;

/// <summary>How people appear in a result: Cookie's placeholders, or as they are (MCP, docs/mcp-plan.md §6.6).</summary>
public interface IMasker
{
    /// <summary>The text with people replaced, or as it is; never null (an empty text for none).</summary>
    string Mask(string? text);
}

/// <summary>MCP's masking: none. An MCP user sees what their rights allow (docs/cookie-plan.md §3.5).</summary>
public sealed class NoMasking : IMasker
{
    public static readonly NoMasking Instance = new();

    public string Mask(string? text) => text ?? "";
}

/// <summary>Who a tool runs for.</summary>
public enum ToolCaller { Cookie, Mcp }

/// <summary>An MCP call's caller (docs/mcp-plan.md §6.2): the token's user and the token, and where PNMT's pages are.</summary>
public sealed record McpCallInfo(Guid UserId, string Username, Guid TokenId, McpTokenKind Kind, string? BaseUrl, string CorrelationId);

/// <summary>
/// What a tool runs with (docs/mcp-plan.md §6.2): the rights it may use (Cookie's group, the lower of that and an asker's, or
/// a token's capped rights), the masking, and the areas its results came from (a note or a reply carries them, cookie
/// plan §9.2). For MCP also the call and the objects it was about, for the MCP log.
/// </summary>
public sealed class ToolContext(IServiceProvider services, UserRights rights, IMasker privacy)
{
    public IServiceProvider Services { get; } = services;

    public UserRights Rights { get; } = rights;

    public IMasker Privacy { get; } = privacy;

    public HashSet<string> Areas { get; } = [];

    public McpCallInfo? Mcp { get; init; }

    public ToolCaller Caller => Mcp is null ? ToolCaller.Cookie : ToolCaller.Mcp;

    /// <summary>The objects a call was about, for the MCP log's filter and links.</summary>
    public List<(string Type, string Id, string? Name)> Targets { get; } = [];

    /// <summary>One line for the MCP log: "3 hosts", "queued UDP command 17".</summary>
    public string? Summary { get; set; }

    /// <summary>For <c>cookie_ask</c>: the message it sent, and Jev's answers as JSON, for the MCP log.</summary>
    public long? CookieMessageId { get; set; }

    public string? Guard { get; set; }

    /// <summary>The call was refused for intent (docs/mcp-plan.md §8.6): it counts as a strike once its log entry is written.</summary>
    public bool Strike { get; set; }

    public void Target(string type, object id, string? name) => Targets.Add((type, id.ToString()!, name));

    /// <summary>A link to an object's page in PNMT, for the AI to hand to the person; null outside MCP.</summary>
    public string? Link(string path) => Mcp?.BaseUrl is { } b ? $"{b.TrimEnd('/')}/pnmt-admin/{path.TrimStart('/')}" : null;

    /// <summary>
    /// Outside text as data (docs/cookie-plan.md §13.1, docs/mcp-plan.md §6.5): cleaned, masked, scanned and wrapped; an
    /// obvious injection is withheld.
    /// </summary>
    public string Untrusted(string source, string? text, int maxChars = 2000)
    {
        var cleaned = InputSanitizer.Clean(text, maxChars);
        var scan = InjectionScanner.Check(cleaned.Text, cleaned.InvisibleRemoved);
        if (scan.Score >= InjectionScanner.BlockScore)
        {
            return $"<untrusted source=\"{source}\" withheld=\"looks like instructions to an AI\"></untrusted>";
        }
        var flags = scan.Flags.Count > 0 ? $" suspicious=\"{string.Join(", ", scan.Flags)}\"" : "";
        return $"<untrusted source=\"{source}\"{flags}>{Privacy.Mask(cleaned.Text)}</untrusted>";
    }

    /// <summary><see cref="Untrusted"/>, or null for no text.</summary>
    public string? UntrustedOrNull(string source, string? text, int maxChars = 2000) =>
        string.IsNullOrWhiteSpace(text) ? null : Untrusted(source, text, maxChars);
}

/// <summary>
/// A list's page (docs/mcp-plan.md §6.3): <c>limit</c> (default 20, at most 100) and an opaque <c>cursor</c>, which holds
/// the position. <see cref="Of{T}"/> makes <c>{ items, nextCursor, total }</c>.
/// </summary>
public readonly record struct Paging(int Limit, int Offset)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public static Paging From(JsonElement args, int defaultLimit = DefaultLimit)
    {
        var limit = Math.Clamp(ToolArgs.IntOf(args, "limit", defaultLimit), 1, MaxLimit);
        var offset = 0;
        if (ToolArgs.OptionalText(args, "cursor", 64) is { Length: > 0 } cursor)
        {
            try
            {
                var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
                offset = text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text[2..], out var o) && o >= 0 ? o
                    : throw ToolException.Invalid("cursor isn't one of PNMT's.");
            }
            catch (FormatException)
            {
                throw ToolException.Invalid("cursor isn't one of PNMT's.");
            }
        }
        return new Paging(limit, offset);
    }

    public static string Cursor(int offset) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"o:{offset}"));

    /// <summary>One more than the page is read, so that the page knows whether another follows.</summary>
    public async Task<ToolPage<T>> ReadAsync<T>(IQueryable<T> query, CancellationToken ct, bool count = true)
    {
        var rows = await query.Skip(Offset).Take(Limit + 1).ToListAsync(ct);
        int? total = count ? await query.CountAsync(ct) : null;
        return Of(rows, total);
    }

    /// <summary>A page from rows read with <see cref="Limit"/> + 1 (or the whole list already in memory).</summary>
    public ToolPage<T> Of<T>(IReadOnlyList<T> rows, int? total)
    {
        var more = rows.Count > Limit;
        return new ToolPage<T>([.. rows.Take(Limit)], more ? Cursor(Offset + Limit) : null, total);
    }

    /// <summary>A page of a list that is all in memory.</summary>
    public ToolPage<T> Slice<T>(IReadOnlyList<T> all) => Of([.. all.Skip(Offset).Take(Limit + 1)], all.Count);
}

/// <summary>A page of a list.</summary>
public sealed record ToolPage<T>(IReadOnlyList<T> Items, string? NextCursor, int? Total)
{
    public ToolPage<TOut> Select<TOut>(Func<T, TOut> map) => new([.. Items.Select(map)], NextCursor, Total);
}
