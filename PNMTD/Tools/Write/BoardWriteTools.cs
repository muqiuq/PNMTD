using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Boards;
using PNMTD.Data;
using PNMTD.Tools.Read;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Write;

/// <summary>
/// Messages on the wall boards over MCP (docs/mcp-extensions-plan.md §9.2): sent as the token's person, marked MCP, with the limits of
/// Admin → Debug's panel (10 open on a board, the longest duration). No approval: a token acts for its person, who holds
/// <i>Boards: Message</i>. A message is withdrawn only by whoever sent it.
/// </summary>
public static class BoardWriteTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("board_message_send", "Send a message to boards",
            "Shows a message on wall boards for a while: an alert, a warning, maintenance, information or a success, with a title and an optional " +
            "text, on the boards named or on every board that shows messages. It says it came from you. At most 10 open messages on a board.",
            Schema(Ids("boardIds", "The boards, from boards_list", required: false), Bool("allBoards", "Every board that shows messages instead"),
                Choice("kind", "Information when left out", Enum.GetNames<BoardMessageKind>()), Text("title", "One line", true), Text("text", "More, under the title"),
                Int("seconds", "How long it shows; 60 when left out")),
            Area.Boards, Level.Message, ToolKind.Write, SendAsync) { AuditEvents = [AuditEvents.BoardMessageSent] },
        new("board_message_withdraw", "Withdraw a board message", "Takes a message you sent off the boards before its end.",
            Schema(Id("id", "The message, as board_message_send or boards_list name it")),
            Area.Boards, Level.Message, ToolKind.Write, WithdrawAsync) { AuditEvents = [AuditEvents.BoardMessageWithdrawn], Idempotent = true },
    ];

    private static async Task<(Guid Id, string Name, AuditWho Who)> SenderAsync(ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var user = ctx.Mcp?.UserId ?? throw new ToolException("mcp_only", "Only over MCP.");
        var name = await db.Users.AsNoTracking().Where(u => u.Id == user).Select(u => u.DisplayName).SingleOrDefaultAsync(ct) ?? ctx.Mcp!.Username;
        return (user, name, AuditWho.From(ctx.Services.GetRequiredService<IHttpContextAccessor>()));
    }

    private static async Task<object> SendAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var (user, name, who) = await SenderAsync(ctx, db, ct);
        var ids = OptionalIds(args, "boardIds", 100);
        var sent = await ctx.Services.GetRequiredService<BoardMessageService>().SendAsync(new BoardMessageRequest
        {
            BoardIds = ids,
            AllBoards = OptionalBool(args, "allBoards") ?? false,
            Kind = OptionalEnum<BoardMessageKind>(args, "kind") ?? BoardMessageKind.Information,
            Title = TextOf(args, "title", 1, 200),
            Text = OptionalText(args, "text", 2000),
            Seconds = IntOf(args, "seconds", 60),
        }, new BoardMessageSender(BoardMessageSource.Mcp, user, name, who), ct);
        ctx.Target("BoardMessage", sent.Id, sent.Title);
        ctx.Summary = $"{sent.Kind} \"{sent.Title}\" to {sent.Boards.Count} boards";
        return BoardReadTools.BoardMessage(ctx, sent);
    }

    private static async Task<object> WithdrawAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var id = IdOf(args);
        var (user, name, who) = await SenderAsync(ctx, db, ct);
        var sender = await db.BoardMessages.AsNoTracking().Where(m => m.Id == id).Select(m => new { m.SentById }).SingleOrDefaultAsync(ct)
            ?? throw ToolException.NotFound("board message");
        if (sender.SentById != user)
        {
            throw new ToolException("not_yours", "Only a message you sent can be withdrawn over MCP; the others on Admin → Debug in PNMT.");
        }
        var withdrawn = await ctx.Services.GetRequiredService<BoardMessageService>().WithdrawAsync(id, user, name, who, ct);
        ctx.Target("BoardMessage", id, withdrawn.Title);
        ctx.Summary = $"withdrew \"{withdrawn.Title}\"";
        return BoardReadTools.BoardMessage(ctx, withdrawn);
    }
}
