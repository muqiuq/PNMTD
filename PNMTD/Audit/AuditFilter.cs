using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PNMTD.Api;
using PNMTD.Data;

namespace PNMTD.Audit;

/// <summary>
/// One entry per write request (docs/audit-redmine-zabbix-plan.md §3.3): around every action with
/// <see cref="AuditAttribute"/>, it opens the request's <see cref="AuditScope"/>, and afterwards sets the outcome and
/// writes what the request's own saves didn't: a failure (in a context of its own, as the request's may hold what
/// failed), or an action that saved nothing through the change tracker. Ordered first, so it sees the 400 of a model
/// that didn't bind as well.
/// </summary>
public sealed class AuditFilter(IDbContextFactory<PnmtdDbContext> dbs, ILogger<AuditFilter> logger) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => int.MinValue;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<AuditAttribute>().FirstOrDefault() is not { } audit)
        {
            await next();
            return;
        }
        var http = context.HttpContext;
        var scope = AuditScope.Begin(audit.EventTypes, AuditWho.From(http), RequestOf(context.ActionDescriptor as ControllerActionDescriptor, http),
            http.TraceIdentifier, context.RouteData.Values.TryGetValue("id", out var id) ? id?.ToString() : null);
        int status;
        string? code = null;
        try
        {
            var executed = await next();
            (status, code) = executed.Exception is { } e && !executed.ExceptionHandled ? StatusOf(e) : (StatusOf(executed.Result, http), null);
        }
        catch (Exception e)
        {
            (status, code) = StatusOf(e);
            await CompleteAsync(scope, status, code);
            throw;
        }
        await CompleteAsync(scope, status, code);
    }

    private Task CompleteAsync(AuditScope scope, int status, string? code) => CompleteAsync(dbs, logger, scope, status, code);

    /// <summary>
    /// Ends a scope and writes what its request's own saves didn't. Also for the MCP server's writes, which aren't MVC
    /// actions (docs/mcp-plan.md §9.4).
    /// </summary>
    internal static async Task CompleteAsync(IDbContextFactory<PnmtdDbContext> dbs, ILogger logger, AuditScope scope, int status, string? code)
    {
        scope.End(status >= 400 ? AuditOutcome.Failed : AuditOutcome.Succeeded, status >= 400 ? $"{status}{(code is null ? "" : " " + code)}" : null);
        if (scope.Persisted && !scope.Dirty)
        {
            return;
        }
        try
        {
            await using var db = await dbs.CreateDbContextAsync(CancellationToken.None);
            var entry = scope.Apply();
            if (scope.Persisted)
            {
                db.AuditLog.Update(entry);
            }
            else
            {
                db.AuditLog.Add(entry);
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "The audit entry of {Request} could not be written", scope.Request);
        }
    }

    /// <summary>"PUT /api/v1/sensors/{id:guid}", as the role matrix names endpoints.</summary>
    public static string? RequestOf(ControllerActionDescriptor? action, HttpContext http) =>
        action?.AttributeRouteInfo?.Template is { } template ? $"{http.Request.Method} /{template}" : null;

    internal static (int Status, string? Code) StatusOf(Exception e) => e switch
    {
        ApiException a => (a.Status, a.Code),
        DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } => (409, ApiErrorCodes.DuplicateKey),
        OperationCanceledException => (499, "cancelled"),
        BadHttpRequestException b => (b.StatusCode, null),
        _ => (500, null),
    };

    private static int StatusOf(Microsoft.AspNetCore.Mvc.IActionResult? result, HttpContext http) => result switch
    {
        IStatusCodeActionResult { StatusCode: { } s } => s,
        null => http.Response.StatusCode,
        _ => 200,
    };
}

/// <summary>
/// A signed-in user refused a write endpoint (403): a <c>Denied</c> entry with the endpoint's event and the missing right
/// (§3.5). Authorization runs before MVC's filters, so this decorates the middleware's result handler, which then says
/// which right is missing (<see cref="Auth.Rights.MissingRightResultHandler"/>). A burst is capped per user: 60 entries a
/// minute, then one "and n more" per minute.
/// </summary>
public sealed class AuditDeniedHandler(IDbContextFactory<PnmtdDbContext> dbs, ILogger<AuditDeniedHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    public const int PerMinute = 60;

    private readonly Auth.Rights.MissingRightResultHandler inner = new();
    private readonly Dictionary<string, (long Minute, int Count)> recent = [];
    private readonly Lock gate = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && context.User.Identity?.IsAuthenticated == true
            && context.GetEndpoint()?.Metadata.GetMetadata<AuditAttribute>() is { } audit)
        {
            await DeniedAsync(context, audit, authorizeResult);
        }
        await inner.HandleAsync(next, context, policy, authorizeResult);
    }

    private async Task DeniedAsync(HttpContext context, AuditAttribute audit, PolicyAuthorizationResult result)
    {
        var who = AuditWho.From(context);
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        int count;
        lock (gate)
        {
            var key = $"{who.UserId}/{who.Username}";
            count = recent.TryGetValue(key, out var r) && r.Minute == minute ? r.Count + 1 : 1;
            recent[key] = (minute, count);
            if (recent.Count > 1000)
            {
                foreach (var stale in recent.Where(kv => kv.Value.Minute != minute).Select(kv => kv.Key).ToList())
                {
                    recent.Remove(stale);
                }
            }
        }
        if (count > PerMinute + 1)
        {
            return;
        }
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var missing = Auth.Rights.MissingRightResultHandler.Missing(result)?.ToString();
        var detail = count == PerMinute + 1 ? "and further refusals this minute, not listed one by one" : missing is null ? null : $"missing: {missing}";
        try
        {
            await using var db = await dbs.CreateDbContextAsync(context.RequestAborted);
            var routeId = context.Request.RouteValues.TryGetValue("id", out var id) ? id?.ToString() : null;
            var targetType = AuditCatalog.Get(audit.EventTypes[0]).TargetType;
            var entry = AuditLog.Entry(audit.EventTypes[0], who, detail,
                routeId is not null && targetType is not null ? AuditTarget.Of(targetType, routeId, null) : null,
                AuditOutcome.Denied, context.TraceIdentifier, AuditFilter.RequestOf(action, context));
            entry.Error = "403";
            db.AuditLog.Add(entry);
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "The audit entry of a refused request could not be written");
        }
    }
}
