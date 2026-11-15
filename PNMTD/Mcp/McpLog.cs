using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Mcp;

/// <summary>
/// The MCP log (docs/mcp-plan.md §9): one entry per request, in a context of its own so that a tool's failed save can't
/// take its entry with it. The newest Mcp:LogEntries are kept, trimmed every 25 writes as the AI's request log is.
/// Failed sign-ins are summed up per address and minute, so a flood can't push the real entries out.
/// </summary>
public sealed class McpLog(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<McpOptions> options, ILogger<McpLog> logger)
{
    public const int MaxArgumentsChars = 16 * 1024;
    public const int MaxResultChars = 256 * 1024;

    private int writes;
    private readonly ConcurrentDictionary<string, (long Minute, long EntryId, int Count)> failures = new();

    public async Task<long?> WriteAsync(McpLogEntity entry, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbs.CreateDbContextAsync(CancellationToken.None);
            db.McpLog.Add(entry);
            await db.SaveChangesAsync(CancellationToken.None);
            if (Interlocked.Increment(ref writes) % 25 == 0)
            {
                await TrimAsync(db, CancellationToken.None);
            }
            return entry.Id;
        }
        catch (Exception e)
        {
            logger.LogError(e, "The MCP log entry for {Method} {Tool} could not be written", entry.Method, entry.Tool);
            return null;
        }
    }

    /// <summary>Keeps the newest <see cref="McpOptions.LogEntries"/> (at least 100).</summary>
    public async Task<int> TrimAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var keep = Math.Max(100, options.CurrentValue.LogEntries);
        return await db.Database.ExecuteSqlAsync($"""
            DELETE FROM mcp_log WHERE "Id" <= (SELECT "Id" FROM mcp_log ORDER BY "Id" DESC OFFSET {keep} LIMIT 1)
            """, ct);
    }

    /// <summary>
    /// A request whose token wasn't accepted (§9.2): the first of a minute from an address is an entry, the rest of that
    /// minute raise its count. The value presented is never stored, only the hint of the token it matched.
    /// </summary>
    public async Task AuthFailedAsync(string? ip, string? userAgent, string why, McpTokenEntity? token, string? username)
    {
        var key = ip ?? "unknown";
        var minute = Clock.UtcNow.Ticks / TimeSpan.TicksPerMinute;
        if (failures.TryGetValue(key, out var seen) && seen.Minute == minute)
        {
            var count = seen.Count + 1;
            failures[key] = (minute, seen.EntryId, count);
            try
            {
                await using var db = await dbs.CreateDbContextAsync();
                var summary = $"{count} refused sign-ins from {key} in this minute; the last: {why}";
                await db.McpLog.Where(l => l.Id == seen.EntryId).ExecuteUpdateAsync(u => u.SetProperty(l => l.Summary, summary));
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "The MCP log's count of refused sign-ins could not be updated");
            }
            return;
        }
        var id = await WriteAsync(new McpLogEntity
        {
            At = Clock.UtcNow,
            Method = "auth",
            Outcome = McpOutcome.Unauthenticated,
            ErrorCode = why,
            Error = McpTokenHandler.Describe(why),
            Summary = $"Refused: {why}" + (token?.Hint is { } hint ? $" (token …{hint})" : ""),
            Ip = ip,
            UserAgent = Cut(userAgent, 300),
            TokenId = token?.Id,
            Kind = token?.Kind,
            UserId = token?.UserId,
            Username = username ?? "",
        });
        if (id is { } entryId)
        {
            failures[key] = (minute, entryId, 1);
        }
        if (failures.Count > 10_000)
        {
            foreach (var old in failures.Where(f => f.Value.Minute < minute).Select(f => f.Key).ToList())
            {
                failures.TryRemove(old, out _);
            }
        }
    }

    /// <summary>
    /// Arguments as the log keeps them (§9.5): every text value that looks like a credential, and every PNMT token,
    /// replaced; at most <see cref="MaxArgumentsChars"/>.
    /// </summary>
    public static string? Redact(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }
        var copy = node.DeepClone();
        foreach (var value in Values(copy).ToList())
        {
            if (value.TryGetValue<string>(out var text) && ToolSafetyRedaction(text) is { } redacted)
            {
                Replace(value, JsonValue.Create(redacted));
            }
        }
        var json = copy.ToJsonString();
        return json.Length <= MaxArgumentsChars ? json : new JsonObject { ["cut"] = true, ["start"] = json[..2000] }.ToJsonString();
    }

    private static string? ToolSafetyRedaction(string text)
    {
        var findings = SecretScanner.Scan(text);
        if (findings.Count == 0 && !McpTokenValues.Anywhere().IsMatch(text))
        {
            return null;
        }
        return findings.Count > 0 ? $"[redacted: {findings[0].Kind}]" : McpTokenValues.Anywhere().Replace(text, "[redacted: a PNMT token]");
    }

    /// <summary>A result as the log keeps it: gzip, the text at most <see cref="MaxResultChars"/>.</summary>
    public static (byte[] Bytes, bool Truncated) Pack(string text)
    {
        var truncated = text.Length > MaxResultChars;
        var kept = truncated ? text[..MaxResultChars] : text;
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(kept));
        }
        return (output.ToArray(), truncated);
    }

    public static string Unpack(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return "";
        }
        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];

    private static IEnumerable<JsonValue> Values(JsonNode node) => node switch
    {
        JsonValue v => [v],
        JsonObject o => o.Select(p => p.Value).Where(c => c is not null).SelectMany(c => Values(c!)),
        JsonArray a => a.Where(c => c is not null).SelectMany(c => Values(c!)),
        _ => [],
    };

    private static void Replace(JsonNode old, JsonNode replacement)
    {
        switch (old.Parent)
        {
            case JsonObject o:
                o[old.GetPropertyName()] = replacement;
                break;
            case JsonArray a:
                a[old.GetElementIndex()] = replacement;
                break;
        }
    }
}
