using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;
using PNMTD.Data;
using PNMTD.Mcp;
using PNMTD.Models.Db;
using static PNMTD.Tools.ToolArgs;

namespace PNMTD.Tools.Read;

/// <summary>
/// Talking to Cookie over MCP (docs/mcp-plan.md §8), and what Cookie shows a person. MCP only, so Cookie can't message
/// itself. Jev screens each message first; without Jev configured, <c>cookie_ask</c> isn't offered at all.
/// </summary>
public static class CookieChatTools
{
    public static readonly IReadOnlyList<ToolSpec> All =
    [
        new("cookie_ask", "Ask Cookie",
            "A message to Cookie, PNMT's assistant, or to Seraph, its reviewer, in your name: it shows in Cookie's window like your own. Cookie answers " +
            "with what you may see. Over a read-only token, only questions. Waits up to a minute for the reply; otherwise ask cookie_reply later.",
            Schema(Text("text", "The message, 1 to 4000 characters", true), Choice("to", "cookie (when left out) or seraph", ["cookie", "seraph"])),
            Area.Cookie, Level.Use, ToolKind.Ask, AskAsync) { Available = GuardConfigured },
        new("cookie_reply", "Cookie's reply", "The reply to a message sent with cookie_ask, or that it is still pending.",
            Schema(Int("messageId", "The message's id, from cookie_ask", true)), Area.Cookie, Level.Use, ToolKind.Read, ReplyAsync),
        new("cookie_status", "Cookie's status", "Cookie's overall state (All fine, Info, Warning, Alert) with why, and your personal summary.",
            Schema(), Area.Cookie, Level.Use, ToolKind.Read, StatusAsync),
    ];

    private static bool GuardConfigured(IServiceProvider services) =>
        services.GetRequiredService<IOptionsMonitor<AiOptions>>().CurrentValue is { Enabled: true, Guard.Configured: true };

    private static System.Security.Claims.ClaimsPrincipal User(ToolContext ctx) =>
        ctx.Services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User ?? throw new ToolException("mcp_only", "Only over MCP.");

    private static async Task<object> AskAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var call = ctx.Mcp ?? throw new ToolException("mcp_only", "Only over MCP.");
        var text = TextOf(args, "text", 1, CookieService.MaxMessageChars);
        var to = OptionalText(args, "to", 8) ?? "cookie";
        if (to is not ("cookie" or "seraph"))
        {
            throw ToolException.Invalid("to is cookie or seraph.");
        }
        var cookie = ctx.Services.GetRequiredService<CookieService>();
        if (!await cookie.RunningAsync(ct))
        {
            throw new ToolException("cookie_stopped", "Cookie is stopped: an admin starts it on Admin → Cookie.");
        }
        var readOnly = call.Kind == McpTokenKind.Read;
        var ai = ctx.Services.GetRequiredService<IOptionsMonitor<AiOptions>>().CurrentValue;
        var questions = McpGuard.Questions(readOnly);
        var decided = await ctx.Services.GetRequiredService<DecisionClient>().DecideAsync(new AiCall(McpGuard.Purpose),
            [$"This message comes over MCP through a {McpTokenService.KindLabel(call.Kind)} token of a person of PNMT, to its assistant.", text], questions, ct);
        var answers = questions.Keys.ToDictionary(k => k, k => decided.Answers.TryGetValue(k, out var a) ? a.Probability ?? 0 : 0);
        ctx.Guard = JsonSerializer.Serialize(answers);
        var user = User(ctx);
        var refused = answers.Where(a => a.Value >= ai.Guard.Threshold).OrderByDescending(a => a.Value).Select(a => a.Key).FirstOrDefault();
        if (refused is not null)
        {
            ctx.CookieMessageId = await cookie.RefusedMessageAsync(user, text, to, call.TokenId, readOnly, McpGuard.Message(refused), ct);
            ctx.Strike = true;
            throw new ToolException("refused_by_guard", McpGuard.Message(refused), new Dictionary<string, object?> { ["question"] = refused });
        }
        var options = ctx.Services.GetRequiredService<IOptionsMonitor<McpOptions>>().CurrentValue;
        var posted = await cookie.PostMessageAsync(new CookieMessageRequest(text, to), user,
            new McpMessageOrigin(call.TokenId, readOnly, Math.Max(1, options.CookieMessagesPerHour)), ct);
        ctx.CookieMessageId = posted.Id;
        ctx.Summary = $"asked {to}";
        var reply = await WaitAsync(db, call.UserId, posted.Id, TimeSpan.FromSeconds(Math.Clamp(options.CookieWaitSeconds, 0, 110)), ct);
        return reply ?? (object)new { Pending = true, MessageId = posted.Id, Next = "Ask cookie_reply with messageId in a while." };
    }

    /// <summary>Waits for Cookie's next round to answer, checking every second (as the chat's page polls), until the time is up.</summary>
    private static async Task<object?> WaitAsync(PnmtdDbContext db, Guid userId, long messageId, TimeSpan wait, CancellationToken ct)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if (await ReplyOfAsync(db, userId, messageId, ct) is { } reply)
            {
                return reply;
            }
            if (DateTime.UtcNow >= until)
            {
                return null;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private static async Task<object?> ReplyOfAsync(PnmtdDbContext db, Guid userId, long messageId, CancellationToken ct)
    {
        var request = await db.CookieMessages.AsNoTracking().Where(m => m.Id == messageId && m.UserId == userId)
            .Select(m => new { m.Answered, m.CaseId, m.RefusedWhy }).SingleOrDefaultAsync(ct) ?? throw ToolException.NotFound("message");
        if (request.RefusedWhy is { } why)
        {
            return new { MessageId = messageId, Refused = why };
        }
        if (!request.Answered)
        {
            return null;
        }
        var reply = await db.CookieMessages.AsNoTracking()
            .Where(m => m.UserId == userId && m.Id > messageId && m.From != "person" && m.CaseId == request.CaseId).OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.From, m.Text, m.At, m.InstructionId }).FirstOrDefaultAsync(ct);
        return reply is null ? null : new
        {
            MessageId = messageId, From = reply.From, Reply = reply.Text, reply.At,
            Draft = reply.InstructionId is null ? null : "Cookie filled in an instruction: confirm it in PNMT's chat (Overview → Cookie).",
        };
    }

    private static async Task<object> ReplyAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var call = ctx.Mcp ?? throw new ToolException("mcp_only", "Only over MCP.");
        var id = (long)IntOf(args, "messageId");
        ctx.CookieMessageId = id;
        return await ReplyOfAsync(db, call.UserId, id, ct) ?? new { Pending = true, MessageId = id };
    }

    private static async Task<object> StatusAsync(JsonElement args, ToolContext ctx, PnmtdDbContext db, CancellationToken ct)
    {
        var cookie = ctx.Services.GetRequiredService<CookieService>();
        var user = User(ctx);
        var status = await cookie.StatusAsync(user, ct);
        var summary = await cookie.SummaryAsync(user, ct);
        ctx.Summary = "Cookie's status";
        return new { Status = status, Summary = summary, Url = ctx.Link("cookie") };
    }
}
