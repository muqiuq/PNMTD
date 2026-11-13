using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Ai;

/// <summary>
/// Every request to a model (docs/ai-operations-plan.md §3.6): the raw bodies (never the Authorization header),
/// gzip-compressed and capped at 1 MiB each, the duration, the tokens and the cost. The newest
/// <c>Ai:LogEntries</c> (10,000) are kept; the spending is counted separately (<see cref="AiBudget"/>).
/// </summary>
public sealed class AiRequestLog(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<AiOptions> options, AiBudget budget)
{
    public const int MaxBodyBytes = 1024 * 1024;
    private int sinceTrim;

    public async Task<long> WriteAsync(AiRequestEntity entry, string request, string? response, CancellationToken ct)
    {
        var (requestBytes, cutRequest) = Gzip(request);
        var (responseBytes, cutResponse) = response is null ? (null, false) : Gzip(response);
        entry.Request = requestBytes!;
        entry.Response = responseBytes;
        entry.Truncated = cutRequest || cutResponse;
        entry.At = entry.At == default ? Clock.UtcNow : entry.At;
        entry.Error = entry.Error is { Length: > 1000 } e ? e[..1000] : entry.Error;
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.AiRequests.Add(entry);
        await db.SaveChangesAsync(ct);
        if (Interlocked.Increment(ref sinceTrim) >= 25)
        {
            Interlocked.Exchange(ref sinceTrim, 0);
            await TrimAsync(db, ct);
        }
        await budget.RecordAsync(entry.Purpose, entry.ModelServed ?? entry.ModelAsked, entry.Cost ?? 0m, ct);
        return entry.Id;
    }

    /// <summary>A request whose answer the task's checks refused (§3.4, layer 6).</summary>
    public async Task MarkInvalidAsync(long id, string error, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await db.AiRequests.Where(r => r.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Outcome, AiOutcome.InvalidOutput)
            .SetProperty(r => r.Error, error.Length > 1000 ? error[..1000] : error), ct);
    }

    /// <summary>Keeps the newest <c>Ai:LogEntries</c>.</summary>
    public async Task TrimAsync(PnmtdDbContext db, CancellationToken ct)
    {
        var keep = Math.Max(1, options.CurrentValue.LogEntries);
        await db.Database.ExecuteSqlRawAsync(
            """DELETE FROM ai_requests WHERE "Id" < (SELECT "Id" FROM ai_requests ORDER BY "Id" DESC OFFSET {0} LIMIT 1)""", [keep - 1], ct);
    }

    public static (byte[]? Bytes, bool Truncated) Gzip(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var truncated = bytes.Length > MaxBodyBytes;
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes, 0, truncated ? MaxBodyBytes : bytes.Length);
        }
        return (buffer.ToArray(), truncated);
    }

    public static string? Gunzip(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }
        using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
