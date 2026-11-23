using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PNMTD.Api.V1.Contracts;
using PNMTD.Audit;
using PNMTD.Auth.Rights;
using PNMTD.Cookie;

namespace PNMTD.Api.V1;

/// <summary>
/// Cookie (docs/cookie-plan.md §19.4): the status for every signed-in user (the dashboard's card), the cases for
/// <i>Cookie: Use</i>, each filtered by the reader's rights.
/// </summary>
[Route("api/v1/cookie")]
[Authorize]
public sealed class CookieController(CookieService cookie, CookieObjectNoteService notes) : V1ControllerBase
{
    [HttpGet("status")]
    public async Task<CookieStatusItem> Status(CancellationToken ct) => await cookie.StatusAsync(User, ct);

    /// <summary>A person removes one of Cookie's notes on a host or sensor (docs/cookie-object-notes-plan.md §3.6): whoever looks after the object.</summary>
    [HttpDelete("object-notes/{id:long}")]
    [RequireRight(Area.Monitoring, Level.Edit)]
    [Audit(AuditEvents.CookieNoteRemoved)]
    public async Task<IActionResult> RemoveObjectNote(long id, CancellationToken ct)
    {
        await notes.RemoveAsync(id, User, ct);
        return NoContent();
    }

    /// <summary>The reader's personal summary (§8): written when asked, kept for 15 minutes.</summary>
    [HttpGet("summary")]
    public async Task<CookieSummaryItem> Summary(CancellationToken ct) => await cookie.SummaryAsync(User, ct);

    [HttpGet("messages")]
    [RequireRight(Area.Cookie, Level.Use)]
    public async Task<CookieThread> Messages([FromQuery] long? afterId, CancellationToken ct) => await cookie.MessagesAsync(User, afterId, ct);

    [HttpPost("messages")]
    [RequireRight(Area.Cookie, Level.Use)]
    [NotAudited("a chat message, kept in the person's thread with Cookie")]
    public async Task<CookieMessageItem> Post(CookieMessageRequest request, CancellationToken ct) => await cookie.PostMessageAsync(request, User, ct);

    /// <summary>A message with Cookie's powers (docs/cookie-powers-plan.md §4): admins only, audited; Cookie acts on it.</summary>
    [HttpPost("messages/empowered")]
    [RequireAdmin]
    [Audit(AuditEvents.CookiePowersUsed)]
    public async Task<CookieMessageItem> PostEmpowered(CookieMessageRequest request, CancellationToken ct) => await cookie.PostEmpoweredAsync(request, User, ct);

    [HttpGet("cases")]
    [RequireRight(Area.Cookie, Level.Use)]
    public async Task<Paged<CookieCaseItem>> Cases([FromQuery] CookieCaseQuery query, CancellationToken ct) => await cookie.CasesAsync(query, User, ct);

    [HttpGet("cases/{id:guid}")]
    [RequireRight(Area.Cookie, Level.Use)]
    public async Task<CookieCaseDetail> Case(Guid id, CancellationToken ct) => await cookie.CaseAsync(id, User, ct);

    /// <summary>A comment on a case (§4.3): in its timeline, and for Cookie in its next round.</summary>
    [HttpPost("cases/{id:guid}/comments")]
    [RequireRight(Area.Cookie, Level.Use)]
    [Audit(AuditEvents.CookieCaseCommented)]
    public async Task<IActionResult> Comment(Guid id, CookieCaseCommentRequest request, CancellationToken ct)
    {
        await cookie.CommentAsync(id, request, User, ct);
        return NoContent();
    }

    /// <summary><i>Don't go on with this</i> (§4.3), with a comment that says why.</summary>
    [HttpPost("cases/{id:guid}/stop")]
    [RequireRight(Area.Cookie, Level.Instruct)]
    [Audit(AuditEvents.CookieCaseStopped)]
    public async Task<IActionResult> Stop(Guid id, CookieCaseCommentRequest request, CancellationToken ct)
    {
        await cookie.StopAsync(id, request, User, ct);
        return NoContent();
    }

    [HttpPost("cases/{id:guid}/go-on")]
    [RequireRight(Area.Cookie, Level.Instruct)]
    [Audit(AuditEvents.CookieCaseGoOn)]
    public async Task<IActionResult> GoOn(Guid id, CookieCaseCommentRequest request, CancellationToken ct)
    {
        await cookie.GoOnAsync(id, request, User, ct);
        return NoContent();
    }

    /// <summary><i>Ask the big brother</i> (§15.2): the next round calls it.</summary>
    [HttpPost("cases/{id:guid}/big-brother")]
    [RequireRight(Area.Cookie, Level.Instruct)]
    [Audit(AuditEvents.CookieBigBrotherAsked)]
    public async Task<IActionResult> AskBigBrother(Guid id, CancellationToken ct)
    {
        await cookie.AskBigBrotherAsync(id, User, ct);
        return NoContent();
    }

    /// <summary>Cookie's decision log (§11), by the reader's rights.</summary>
    [HttpGet("decisions")]
    [RequireRight(Area.Cookie, Level.Use)]
    public async Task<Paged<CookieDecisionItem>> Decisions([FromQuery] CookieDecisionQuery query, CancellationToken ct) => await cookie.DecisionsAsync(query, User, ct);

    [HttpPut("decisions/{id:long}/rating")]
    [RequireRight(Area.Cookie, Level.Use)]
    [Audit(AuditEvents.CookieDecisionRated)]
    public async Task<CookieDecisionItem> Rate(long id, CookieRatingRequest request, CancellationToken ct) => await cookie.RateAsync(id, request, User, ct);

    /// <summary>Confirm a draft Cookie filled in from one's message (§10.3).</summary>
    [HttpPost("instructions/{id:guid}/confirm")]
    [RequireRight(Area.Cookie, Level.Instruct)]
    [Audit(AuditEvents.CookieInstructionConfirmed)]
    public async Task<CookieInstructionItem> Confirm(Guid id, CancellationToken ct) => await cookie.ConfirmInstructionAsync(id, User, ct);

    /// <summary><i>End now</i>.</summary>
    [HttpPost("instructions/{id:guid}/end")]
    [RequireRight(Area.Cookie, Level.Instruct)]
    [Audit(AuditEvents.CookieInstructionEnded)]
    public async Task<CookieInstructionItem> End(Guid id, CancellationToken ct) => await cookie.EndInstructionAsync(id, User, ct);

    /// <summary>The trace by the reader's rights (§9.2); <c>afterId</c> for polling while a round runs.</summary>
    [HttpGet("trace")]
    [RequireRight(Area.Cookie, Level.Use)]
    public async Task<CookieTrace> Trace([FromQuery] CookieTraceQuery query, CancellationToken ct) => await cookie.TraceAsync(query, User, ct);
}

/// <summary>Admin → Cookie (docs/cookie-plan.md §9.3): <i>Cookie: Manage</i>. Cookie's own tools never reach any of it.</summary>
[Route("api/v1/cookie/admin")]
[RequireRight(Area.Cookie, Level.Manage)]
public sealed class CookieAdminController(CookieService cookie) : V1ControllerBase
{
    [HttpGet("overview")]
    public async Task<CookieAdminOverview> Overview(CancellationToken ct) => await cookie.OverviewAsync(User, ct);

    [HttpPost("start")]
    [Audit(AuditEvents.CookieStarted)]
    public async Task<CookieStatusItem> Start(CancellationToken ct) => await cookie.StartAsync(User, ct);

    [HttpPost("stop")]
    [Audit(AuditEvents.CookieStopped)]
    public async Task<CookieStatusItem> Stop(CancellationToken ct) => await cookie.StopAsync(User, ct);

    [HttpPost("run")]
    [Audit(AuditEvents.CookieRunRequested)]
    public async Task<CookieStatusItem> Run(CancellationToken ct) => await cookie.RunNowAsync(User, ct);

    [HttpGet("rounds")]
    public async Task<IReadOnlyList<CookieRoundItem>> Rounds(CancellationToken ct) => await cookie.RoundsAsync(ct);

    /// <summary>A prompt as the model receives it: a prompt's key, or "case:" and a kind of case.</summary>
    [HttpGet("prompts/preview")]
    public async Task<CookiePromptPreview> Preview([FromQuery] string key, CancellationToken ct) => await cookie.PreviewAsync(key, ct);

    /// <summary>Every section; the Redmine section only for <i>Redmine: Manage</i> (docs/cookie-redmine-tab-plan.md §3.2).</summary>
    [HttpGet("settings")]
    public async Task<CookieAllSettings> Settings(CancellationToken ct) => await cookie.SettingsAsync(User, ct);

    /// <summary>What went out, and what a limit held back (§6.1).</summary>
    [HttpGet("outbox")]
    public async Task<Paged<CookieOutboxItem>> Outbox([FromQuery] CookieOutboxQuery query, CancellationToken ct) => await cookie.OutboxAsync(query, ct);

    [HttpPost("outbox/{id:long}/send")]
    [Audit(AuditEvents.CookieHeldBackSent)]
    public async Task<CookieOutboxItem> Send(long id, CancellationToken ct) => await cookie.SendHeldBackAsync(id, User, ct);

    [HttpGet("defaults")]
    public CookieDefaults Defaults() => cookie.Defaults();

    [HttpPut("settings/identity")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveIdentity(IdentitySettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/prompts")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SavePrompts(PromptSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/cases")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveCases(CaseSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/actions")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveActions(ActionSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/models")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveModels(ModelSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpGet("instructions")]
    public async Task<IReadOnlyList<CookieInstructionItem>> Instructions(CancellationToken ct) => await cookie.InstructionsAsync(ct);

    [HttpPost("instructions")]
    [Audit(AuditEvents.CookieInstructionCreated)]
    public async Task<CookieInstructionItem> CreateInstruction(CookieInstructionRequest request, CancellationToken ct) =>
        await cookie.CreateInstructionAsync(request, User, ct);

    [HttpPut("settings/lessons")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveLessons(LessonSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    /// <summary>The access watch (docs/cookie-access-plan.md §8).</summary>
    [HttpPut("settings/access")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveAccess(PNMTD.Cookie.Access.AccessSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    /// <summary>The lounge (docs/lounge-plan.md §12): its rhythm, what it reads with, its powers, memory, convictions and budget.</summary>
    [HttpPut("settings/lounge")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveLounge(PNMTD.Cookie.Lounge.LoungeSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/rights")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveRights(CookieRightsSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    /// <summary>A section's history; the Redmine section's is on <see cref="CookieRedmineAdminController"/>.</summary>
    [HttpGet("settings/{section}/versions")]
    public async Task<IReadOnlyList<CookieSettingsVersion>> Versions(string section, CancellationToken ct) =>
        await cookie.VersionsAsync(CookieService.NotRedmine(section), ct);

    [HttpGet("settings/{section}/versions/{version:int}")]
    public async Task<JsonElement> Version(string section, int version, CancellationToken ct) =>
        await cookie.VersionAsync(CookieService.NotRedmine(section), version, ct);

    [HttpPost("settings/{section}/rollback/{version:int}")]
    [Audit(AuditEvents.CookieSettingsRolledBack)]
    public async Task<CookieSaved> Rollback(string section, int version, CancellationToken ct) =>
        await cookie.RollbackAsync(CookieService.NotRedmine(section), version, User, ct);
}

/// <summary>
/// What Admin → Cookie's pickers list (PNMT's users and groups; Redmine's projects, roles, statuses and trackers): for
/// <i>Cookie: Manage</i>, and for the Redmine tab's <i>Redmine: Manage</i> (docs/cookie-redmine-tab-plan.md §3.2).
/// </summary>
[Route("api/v1/cookie/admin")]
[RequireAnyRight(Area.Cookie, Level.Manage, Area.Redmine, Level.Manage)]
public sealed class CookieAdminPickersController(CookieService cookie) : V1ControllerBase
{
    [HttpGet("people")]
    public async Task<CookiePeople> People(CancellationToken ct) => await cookie.PeopleAsync(ct);

    [HttpGet("redmine")]
    public async Task<CookieRedmineLookups> Redmine(CancellationToken ct) => await cookie.RedmineLookupsAsync(ct);
}

/// <summary>
/// Admin → Cookie → Redmine (docs/cookie-redmine-tab-plan.md §3, §8): <i>Redmine: Manage</i>, not <i>Cookie: Manage</i>. The
/// watched projects and their deadlines, Cookie's tickets, and the section's history; the duties' switches stay on Actions,
/// and are given here read only.
/// </summary>
[Route("api/v1/cookie/admin/redmine")]
[RequireRight(Area.Redmine, Level.Manage)]
public sealed class CookieRedmineAdminController(CookieService cookie, RedmineLinkService links) : V1ControllerBase
{
    [HttpGet("settings")]
    public async Task<CookieRedmineAdminSettings> Settings(CancellationToken ct) => await cookie.RedmineSettingsAsync(ct);

    [HttpPut("settings")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> Save(CookieRedmineSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpGet("settings/versions")]
    public async Task<IReadOnlyList<CookieSettingsVersion>> Versions(CancellationToken ct) =>
        await cookie.VersionsAsync(CookieRedmineSettings.Section, ct);

    [HttpGet("settings/versions/{version:int}")]
    public async Task<JsonElement> Version(int version, CancellationToken ct) => await cookie.VersionAsync(CookieRedmineSettings.Section, version, ct);

    [HttpPost("settings/rollback/{version:int}")]
    [Audit(AuditEvents.CookieSettingsRolledBack)]
    public async Task<CookieSaved> Rollback(int version, CancellationToken ct) =>
        await cookie.RollbackAsync(CookieRedmineSettings.Section, version, User, ct);

    /// <summary>A newly watched project's deadlines.</summary>
    [HttpGet("defaults")]
    public WatchedProject Defaults() => WatchedProject.Default(0);

    /// <summary>Which Redmine user each active PNMT user is (§4.3), and the Redmine users without one.</summary>
    [HttpGet("links")]
    public async Task<RedmineLinksView> Links(CancellationToken ct) => await links.ViewAsync(User, ct);

    /// <summary>Links a user to a Redmine user PNMT knows, or unlinks them (null).</summary>
    [HttpPut("links/{userId:guid}")]
    [Audit(AuditEvents.RedmineUserLinkChanged)]
    public async Task<RedmineLinkRow> Link(Guid userId, RedmineUserLinkRequest request, CancellationToken ct) =>
        await links.LinkAsync(userId, request.RedmineUserId, User, ct);

    /// <summary>The away watch's comments, newest first, 50 a page (§9).</summary>
    [HttpGet("away")]
    public async Task<Paged<CookieAwayCommentItem>> Away([FromQuery] int page, CancellationToken ct) => await links.AwayAsync(page, ct);

    /// <summary>Every user whose name is exactly one Redmine user's, linked by that id.</summary>
    [HttpPost("links/exact")]
    [Audit(AuditEvents.RedmineUserLinkChanged)]
    public async Task<RedmineLinkedCount> LinkExact(CancellationToken ct) => await links.LinkExactAsync(User, ct);
}
