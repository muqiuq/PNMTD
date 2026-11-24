using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Boards;

/// <summary>
/// Who sends a board message (docs/board-messages-plan.md §4.2, §8.7): a person, or Cookie with its decision, its case, and the person who
/// approved a proposal; and who the audit names.
/// </summary>
public sealed record BoardMessageSender(BoardMessageSource Source, Guid? UserId, string Name, AuditWho Who, Guid? ApprovedById = null,
    string? ApprovedByName = null, long? DecisionId = null, Guid? CaseId = null);

/// <summary>
/// Board messages (docs/board-messages-plan.md §4, §6, §10): sending one to boards that show messages (at most 10 open on each), withdrawing
/// it, the lists for the Debug page, and a board's open ones for its poll. Each send and withdrawal moves the boards' stamps, so their
/// screens fetch the change at their next poll; and is in the audit log. Ended messages are kept Boards:MessageHistoryDays, cleaned when
/// one is sent, at most once every 6 hours: no background service.
/// </summary>
public sealed class BoardMessageService(PnmtdDbContext db, BoardRuntime runtime, IOptionsMonitor<BoardOptions> options, ILogger<BoardMessageService> logger)
{
    public const int MaxTitle = 100;
    public const int MaxText = 1000;
    public const int MinSeconds = 10;
    public const int MaxOpenPerBoard = 10;

    /// <summary>Boards:MessageMaxMinutes in seconds: 1 minute to a day.</summary>
    public int MaxSeconds => Math.Clamp(options.CurrentValue.MessageMaxMinutes, 1, 1440) * 60;

    /// <summary>A board a message can go to: enabled, and showing messages (Look → Messages).</summary>
    public sealed record Target(Guid Id, string Name, bool Enabled, bool ShowsMessages, BoardPeople People, IReadOnlyList<string> Networks);

    public async Task<IReadOnlyList<Target>> TargetsAsync(CancellationToken ct)
    {
        var rows = await db.Boards.AsNoTracking().OrderBy(b => b.Name).Select(b => new { b.Id, b.Name, b.Enabled, b.Layout, b.Networks }).ToListAsync(ct);
        return rows.Select(b =>
        {
            var look = BoardLayouts.Read(b.Layout).Look;
            return new Target(b.Id, b.Name, b.Enabled, look.Messages, look.People, Connectors.ConnectorNetworks.Read(b.Networks).Select(n => n.Cidr).ToList());
        }).ToList();
    }

    /// <summary>
    /// Sends a message (§4): 400 for a field out of range, 404 for a board that doesn't exist, 409 for one switched off, with messages off,
    /// or with 10 open. "All boards" is resolved now: a board added later doesn't get it.
    /// </summary>
    public async Task<BoardMessageItem> SendAsync(BoardMessageRequest request, BoardMessageSender sender, CancellationToken ct)
    {
        var v = new ApiValidator();
        var title = string.IsNullOrWhiteSpace(request.Title) ? "" : request.Title.Replace("\0", "").Trim();
        v.Check(title.Length is >= 1 and <= MaxTitle && !title.Contains('\n') && !title.Contains('\r'), "title", $"1 to {MaxTitle} characters, one line.");
        var text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Replace("\0", "").Trim();
        v.Check(text is null || text.Length <= MaxText, "text", $"At most {MaxText} characters.");
        v.Check(Enum.IsDefined(request.Kind), "kind", "Alert, warning, maintenance, information or success.");
        v.Check(request.Seconds >= MinSeconds && request.Seconds <= MaxSeconds, "seconds", $"{MinSeconds} to {MaxSeconds} seconds.");
        var ids = (request.BoardIds ?? []).Distinct().ToList();
        v.Check(request.AllBoards || ids.Count > 0, "boardIds", "One or more boards, or all boards that show messages.");
        v.ThrowIfInvalid();

        var boards = (await TargetsAsync(ct)).ToDictionary(b => b.Id);
        List<Target> targets;
        if (request.AllBoards)
        {
            targets = boards.Values.Where(b => b.Enabled && b.ShowsMessages).ToList();
            if (targets.Count == 0)
            {
                throw ApiException.Conflict("no_boards", "No board shows messages: switch Look → Messages on for one first.");
            }
        }
        else
        {
            targets = [];
            foreach (var id in ids)
            {
                var board = boards.GetValueOrDefault(id) ?? throw ApiException.NotFound("Board");
                if (!board.Enabled)
                {
                    throw ApiException.Conflict("board_disabled", $"{board.Name} is switched off.");
                }
                if (!board.ShowsMessages)
                {
                    throw ApiException.Conflict("board_no_messages", $"{board.Name} doesn't show messages (its Look → Messages is off).");
                }
                targets.Add(board);
            }
        }
        var now = Clock.UtcNow;
        var open = await db.BoardMessages.AsNoTracking().Where(m => m.WithdrawnAt == null && m.EndsAt > now).Select(m => m.BoardIds).ToListAsync(ct);
        foreach (var target in targets)
        {
            if (open.Count(o => o.Contains(target.Id)) >= MaxOpenPerBoard)
            {
                throw ApiException.Conflict("too_many_open", $"{target.Name} has {MaxOpenPerBoard} open messages: withdraw one first.");
            }
        }

        var message = new BoardMessageEntity
        {
            Id = Guid.CreateVersion7(now), BoardIds = [.. targets.Select(t => t.Id)], Kind = request.Kind, Title = title, Text = text, Seconds = request.Seconds,
            SentAt = now, EndsAt = now.AddSeconds(request.Seconds), Source = sender.Source, SentById = sender.UserId, SentByName = Cut(sender.Name, 200),
            ApprovedById = sender.ApprovedById, ApprovedByName = sender.ApprovedByName is null ? null : Cut(sender.ApprovedByName, 200),
            CookieDecisionId = sender.DecisionId, CookieCaseId = sender.CaseId,
        };
        db.BoardMessages.Add(message);
        var names = string.Join(", ", targets.Select(t => t.Name));
        var by = sender.Source == BoardMessageSource.Cookie
            ? $"by {message.SentByName}{(sender.ApprovedByName is { } approver ? $", approved by {approver}" : "")}"
            : $"by {message.SentByName}";
        AuditLog.Record(db, AuditEvents.BoardMessageSent,
            $"{message.Kind} \"{title}\" to {names} for {Duration(message.Seconds)}, {by}{(text is null ? "" : $": {Cut(text, 600)}")}", sender.Who,
            AuditTarget.Of(AuditTargets.BoardMessage, message.Id, title));
        await db.SaveChangesAsync(ct);
        foreach (var target in targets)
        {
            runtime.Bump(target.Id);
        }
        logger.LogInformation("Board message {Id} ({Kind}) sent to {Boards} by {Sender}", message.Id, message.Kind, names, message.SentByName);
        if (runtime.CleanupDue())
        {
            await CleanupAsync(ct);
        }
        return Item(message, boards, now);
    }

    /// <summary>Ends an open message on every board it went to (§4.4): 404 for none, 409 for one that ended already.</summary>
    public async Task<BoardMessageItem> WithdrawAsync(Guid id, Guid? userId, string name, AuditWho who, CancellationToken ct)
    {
        var message = await db.BoardMessages.SingleOrDefaultAsync(m => m.Id == id, ct) ?? throw ApiException.NotFound("Board message");
        var now = Clock.UtcNow;
        if (message.WithdrawnAt is not null || message.EndsAt <= now)
        {
            throw ApiException.Conflict("not_open", "This message isn't shown any more.");
        }
        var left = (int)Math.Ceiling((message.EndsAt - now).TotalSeconds);
        message.WithdrawnAt = now;
        message.WithdrawnById = userId;
        message.WithdrawnByName = Cut(name, 200);
        var boards = (await TargetsAsync(ct)).ToDictionary(b => b.Id);
        var names = string.Join(", ", message.BoardIds.Select(b => boards.GetValueOrDefault(b)?.Name ?? "a deleted board"));
        AuditLog.Record(db, AuditEvents.BoardMessageWithdrawn, $"\"{message.Title}\" on {names}, by {name}, {Duration(left)} before its end", who,
            AuditTarget.Of(AuditTargets.BoardMessage, message.Id, message.Title));
        await db.SaveChangesAsync(ct);
        foreach (var board in message.BoardIds)
        {
            runtime.Bump(board);
        }
        return Item(message, boards, now);
    }

    /// <summary>The open messages, newest first; or the last <paramref name="limit"/> sent in the days kept.</summary>
    public async Task<IReadOnlyList<BoardMessageItem>> ListAsync(bool open, int limit, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var query = db.BoardMessages.AsNoTracking();
        if (open)
        {
            query = query.Where(m => m.WithdrawnAt == null && m.EndsAt > now);
        }
        var rows = await query.OrderByDescending(m => m.SentAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
        var boards = (await TargetsAsync(ct)).ToDictionary(b => b.Id);
        return rows.Select(m => Item(m, boards, now)).ToList();
    }

    /// <summary>
    /// A board's open messages for its poll (§6.1): the most important kind first, then the newest; who sent it as the board names people,
    /// Cookie always by its name ("Name (Cookie)" when renamed).
    /// </summary>
    public async Task<IReadOnlyList<BoardMessageView>> ForBoardAsync(Guid board, BoardPeople people, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var rows = await db.BoardMessages.AsNoTracking().Where(m => m.WithdrawnAt == null && m.EndsAt > now && m.BoardIds.Contains(board))
            .ToListAsync(ct);
        return rows.OrderBy(m => m.Kind).ThenByDescending(m => m.SentAt).Select(m => new BoardMessageView(m.Id, m.Kind, m.Title, m.Text,
            From(m, people), Clock.AsUtc(m.SentAt), m.Seconds, Remaining(m, now))).ToList();
    }

    private static string? From(BoardMessageEntity m, BoardPeople people) => m.Source == BoardMessageSource.Cookie
        ? (string.Equals(m.SentByName, "Cookie", StringComparison.OrdinalIgnoreCase) ? m.SentByName : $"{m.SentByName} (Cookie)")
        : people switch
        {
            BoardPeople.Hidden => null,
            BoardPeople.Initials => BoardData.Initials(m.SentByName),
            _ => m.SentByName,
        };

    private static int Remaining(BoardMessageEntity m, DateTime now) =>
        m.WithdrawnAt is not null ? 0 : Math.Max(0, (int)Math.Ceiling((Clock.AsUtc(m.EndsAt) - now).TotalSeconds));

    private static BoardMessageItem Item(BoardMessageEntity m, IReadOnlyDictionary<Guid, Target> boards, DateTime now) =>
        new(m.Id, m.Kind, m.Title, m.Text, m.Seconds, Clock.AsUtc(m.SentAt), Clock.AsUtc(m.EndsAt), Remaining(m, now),
            m.WithdrawnAt is null && Clock.AsUtc(m.EndsAt) > now, m.Source, m.SentByName, m.ApprovedByName,
            m.BoardIds.Select(b => new BoardMessageBoard(b, boards.GetValueOrDefault(b)?.Name ?? "A deleted board")).ToList(),
            m.WithdrawnAt is { } w ? Clock.AsUtc(w) : null, m.WithdrawnByName, m.CookieCaseId);

    /// <summary>Messages that ended more than Boards:MessageHistoryDays ago.</summary>
    public async Task<int> CleanupAsync(CancellationToken ct)
    {
        var before = Clock.UtcNow.AddDays(-Math.Max(1, options.CurrentValue.MessageHistoryDays));
        var removed = await db.BoardMessages.Where(m => m.EndsAt < before).ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} board messages that ended before {Before:u}", removed, before);
        }
        return removed;
    }

    /// <summary>"45 s", "1 min", "1 h 30 min".</summary>
    public static string Duration(int seconds) => seconds < 60 ? $"{seconds} s"
        : seconds < 3600 ? (seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds / 60} min {seconds % 60} s")
        : seconds % 3600 == 0 ? $"{seconds / 3600} h" : $"{seconds / 3600} h {seconds % 3600 / 60} min";

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
