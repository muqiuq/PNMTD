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
public sealed class CookieController(CookieService cookie) : V1ControllerBase
{
    [HttpGet("status")]
    public async Task<CookieStatusItem> Status(CancellationToken ct) => await cookie.StatusAsync(User, ct);

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

    [HttpGet("cases")]
    [RequireRight(Area.Cookie, Level.Use)]
    public async Task<Paged<CookieCaseItem>> Cases([FromQuery] CookieCaseQuery query, CancellationToken ct) => await cookie.CasesAsync(query, User, ct);

    [HttpGet("cases/{id:guid}")]
    [RequireRight(Area.Cookie, Level.Use)]
    public async Task<CookieCaseDetail> Case(Guid id, CancellationToken ct) => await cookie.CaseAsync(id, User, ct);

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

    [HttpGet("settings")]
    public async Task<CookieAllSettings> Settings(CancellationToken ct) => await cookie.SettingsAsync(ct);

    /// <summary>What went out, and what a limit held back (§6.1).</summary>
    [HttpGet("outbox")]
    public async Task<Paged<CookieOutboxItem>> Outbox([FromQuery] CookieOutboxQuery query, CancellationToken ct) => await cookie.OutboxAsync(query, ct);

    [HttpPost("outbox/{id:long}/send")]
    [Audit(AuditEvents.CookieHeldBackSent)]
    public async Task<CookieOutboxItem> Send(long id, CancellationToken ct) => await cookie.SendHeldBackAsync(id, User, ct);

    [HttpGet("defaults")]
    public CookieDefaults Defaults() => cookie.Defaults();

    [HttpGet("people")]
    public async Task<CookiePeople> People(CancellationToken ct) => await cookie.PeopleAsync(ct);

    [HttpGet("redmine")]
    public async Task<CookieRedmineLookups> Redmine(CancellationToken ct) => await cookie.RedmineLookupsAsync(ct);

    [HttpPut("settings/identity")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveIdentity(IdentitySettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/prompts")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SavePrompts(PromptSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/cases")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveCases(CaseSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/hours")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveHours(HoursSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/notifications")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveNotifications(NotificationSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/redmine")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveRedmine(CookieRedmineSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/actions")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveActions(ActionSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/models")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveModels(ModelSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpPut("settings/rights")]
    [Audit(AuditEvents.CookieSettingsChanged)]
    public async Task<CookieSaved> SaveRights(CookieRightsSettings value, CancellationToken ct) => await cookie.SaveAsync(value, User, ct);

    [HttpGet("settings/{section}/versions")]
    public async Task<IReadOnlyList<CookieSettingsVersion>> Versions(string section, CancellationToken ct) => await cookie.VersionsAsync(section, ct);

    [HttpGet("settings/{section}/versions/{version:int}")]
    public async Task<JsonElement> Version(string section, int version, CancellationToken ct) => await cookie.VersionAsync(section, version, ct);

    [HttpPost("settings/{section}/rollback/{version:int}")]
    [Audit(AuditEvents.CookieSettingsRolledBack)]
    public async Task<CookieSaved> Rollback(string section, int version, CancellationToken ct) => await cookie.RollbackAsync(section, version, User, ct);
}
