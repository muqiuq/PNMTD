using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Decisions;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>
/// <c>cookie.action</c> (docs/cookie-plan.md §21.1): a proposal of Cookie's that waits for a person, because its action is at
/// <i>Propose</i>, its confidence was below the threshold, or Seraph asked for a person. <i>Do it</i> runs it through the
/// limits; <i>Don't</i> keeps the comment with it. Cookie handing something to a person is <i>Noted</i> or dismissed.
/// </summary>
public sealed class CookieActionDecision(IServiceProvider services, IHttpContextAccessor accessor) : IDecisionKind
{
    public string Kind => CookieExecutor.InboxKind;

    private static bool HandToPerson(DecisionEntity d) => d.Title.StartsWith($"Cookie proposes: {CookieExecutor.Label(CookieActionKind.HandToPerson)}", StringComparison.Ordinal);

    public IReadOnlyList<DecisionOption> Options(DecisionEntity decision) => HandToPerson(decision)
        ?
        [
            new("noted", "Noted", "Someone takes care of it.", false, false, true),
            new("dismiss", "Dismiss", "Nothing to do.", false, false, true),
        ]
        :
        [
            new("approve", "Do it", "Cookie does what it proposes, within the output limits.", false, false, true),
            new("reject", "Don't", "It doesn't happen. Say why in the comment: it helps Cookie's lessons.", false, false, true),
        ];

    public async Task<string?> ApplyAsync(DecisionEntity decision, string choice, DecideRequest request, DecisionContext context, CancellationToken ct)
    {
        var payload = DecisionBook.Payload<CookieActionPayload>(decision);
        var executor = services.GetRequiredService<CookieExecutor>();
        var by = accessor.HttpContext?.User.Identity?.Name ?? "someone";
        switch (choice)
        {
            case "approve" or "noted":
            {
                var log = await executor.RunApprovedAsync(payload.LogId, by, ct);
                return System.Text.Json.JsonSerializer.Serialize(new { outcome = log.Outcome, detail = log.Detail }, DecisionBook.Json);
            }
            case "reject" or "dismiss":
                await executor.DeclineAsync(payload.LogId, by, request.Comment, ct);
                return null;
            default:
                throw ApiException.Invalid("choice", "Not an option of this decision.");
        }
    }

    /// <summary>A proposal for a case that closed, or that a person stopped, is superseded.</summary>
    public async Task<bool> StillRelevantAsync(DecisionEntity decision, PnmtdDbContext db, CancellationToken ct) =>
        decision.SourceId is not { } caseId || await db.CookieCases.AnyAsync(c => c.Id == caseId && c.Open && c.StoppedAt == null, ct);
}
