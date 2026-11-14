using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Decisions;

/// <summary>What a kind raises: the text, the payload, and where it comes from.</summary>
public sealed record DecisionDraft(
    string Kind,
    string Title,
    string Summary,
    object Payload,
    string? DedupeKey,
    Guid? Subject = null,
    string? SourceType = null,
    Guid? SourceId = null,
    bool AdminOnly = false,
    bool Disputed = false,
    int Priority = 0,
    Guid? AiWorkId = null);

/// <summary>
/// Raises decisions (docs/ai-operations-plan.md §5.1): one open decision per dedupe key, so that more of the same raise
/// its occurrences and attach their subjects instead of asking again. Open decisions expire after
/// <c>Decisions:ExpireDays</c> (30).
/// </summary>
public sealed class DecisionBook(IDbContextFactory<PnmtdDbContext> dbs, IConfiguration configuration)
{
    public static readonly JsonSerializerOptions Json = Ai.AiJson.Options;

    public int ExpireDays => Math.Max(1, configuration.GetValue("Decisions:ExpireDays", 30));

    /// <summary>The open decision of <paramref name="key"/>, with the subject attached; null when there is none.</summary>
    public async Task<Guid?> AttachAsync(string key, Guid? subject, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var open = await db.Decisions.SingleOrDefaultAsync(d => d.DedupeKey == key && d.State == DecisionState.Open, ct);
        if (open is null)
        {
            return null;
        }
        open.Occurrences++;
        open.LastSeenAt = Clock.UtcNow;
        if (subject is { } s && !open.Subjects.Contains(s))
        {
            open.Subjects = [.. open.Subjects, s];
        }
        await db.SaveChangesAsync(ct);
        return open.Id;
    }

    /// <summary>A new open decision, or the open one of the same key with the subject attached.</summary>
    public async Task<Guid> RaiseAsync(DecisionDraft draft, CancellationToken ct)
    {
        if (draft.DedupeKey is { } key && await AttachAsync(key, draft.Subject, ct) is { } existing)
        {
            return existing;
        }
        var now = Clock.UtcNow;
        var decision = new DecisionEntity
        {
            Id = Guid.NewGuid(), Kind = draft.Kind, State = DecisionState.Open, AdminOnly = draft.AdminOnly,
            Title = Cut(draft.Title, 300), Summary = Cut(draft.Summary, 2000), Payload = JsonSerializer.Serialize(draft.Payload, Json),
            SourceType = draft.SourceType, SourceId = draft.SourceId, DedupeKey = draft.DedupeKey, Subjects = draft.Subject is { } s ? [s] : [],
            Disputed = draft.Disputed, Priority = draft.Priority, CreatedAt = now, LastSeenAt = now, ExpiresAt = now.AddDays(ExpireDays),
            AiWorkId = draft.AiWorkId,
        };
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.Decisions.Add(decision);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && draft.DedupeKey is { } raced)
        {
            if (await AttachAsync(raced, draft.Subject, ct) is { } attached)
            {
                return attached;
            }
            throw;
        }
        return decision.Id;
    }

    public static T Payload<T>(DecisionEntity decision) => JsonSerializer.Deserialize<T>(decision.Payload, Json)!;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
