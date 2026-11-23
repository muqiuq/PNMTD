using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace PNMTD.Api;

public static class ApiPaths
{
    public const string V1 = "/api/v1";

    public static bool IsApi(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// /api/v1's own JSON and errors (docs/webui-migration-plan.md §3.7), kept apart from the sensor API,
/// which stays on MVC's defaults:
/// <list type="bullet">
/// <item>JSON: web defaults (camelCase) with enums as strings and strict numbers. The input and output formatters only
///   take requests under /api, so nothing here can change a sensor response. The same options feed
///   the OpenAPI document.</item>
/// <item>Errors: ProblemDetails with a stable <c>code</c>, for /api only.</item>
/// </list>
/// </summary>
public static class ApiSetup
{
    public static IServiceCollection AddPnmtdApi(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            // Numbers are numbers: the web defaults also read "5" as 5, which types every number of
            // the OpenAPI document as number-or-string, and the UI's generated types with it.
            o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });

        services.AddOptions<MvcOptions>()
            .Configure<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>, ILoggerFactory>((mvc, json, loggers) =>
            {
                var options = json.Value.SerializerOptions;
                // MVC's input options start from the same web defaults; only the converters differ.
                var input = new JsonOptions();
                input.JsonSerializerOptions.NumberHandling = options.NumberHandling;
                foreach (var converter in options.Converters)
                {
                    input.JsonSerializerOptions.Converters.Add(converter);
                }
                mvc.InputFormatters.Insert(0, new V1JsonInputFormatter(input, loggers.CreateLogger<SystemTextJsonInputFormatter>()));
                mvc.OutputFormatters.Insert(0, new V1JsonOutputFormatter(options));
            });

        // An IFormFile parameter (the firmware upload) would add a Consumes constraint, which routing
        // checks before authorization: other roles would get 404 instead of 403 (§3.6).
        services.Configure<ApiBehaviorOptions>(o => o.SuppressConsumesConstraintForFormFileParameters = true);

        services.AddProblemDetails(o => o.CustomizeProblemDetails = context =>
        {
            // The sensor API's ProblemDetails (404, 400, 425) come from the same factory: untouched.
            if (!ApiPaths.IsApi(context.HttpContext.Request.Path))
            {
                return;
            }
            var status = context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode;
            // A session whose 30 days ended (docs/mcp-plan.md §3.3): the sign-in page says why.
            if (status == StatusCodes.Status401Unauthorized && context.HttpContext.Items.ContainsKey(Auth.SessionRules.LoginExpiredItem))
            {
                context.ProblemDetails.Detail ??= "Please sign in again: PNMT asks for the password at least once a month.";
                context.ProblemDetails.Extensions.TryAdd("code", Auth.SessionRules.LoginExpiredCode);
            }
            context.ProblemDetails.Extensions.TryAdd("code", ApiErrorCodes.ForStatus(status));
        });

        return services;
    }

    /// <summary>
    /// For /api only: unhandled exceptions and empty error responses (the cookie handler's 401 and
    /// 403, routing's 405) become ProblemDetails. The sensor API keeps its empty bodies.
    /// </summary>
    public static IApplicationBuilder UseApiErrors(this IApplicationBuilder app) =>
        app.UseWhen(c => ApiPaths.IsApi(c.Request.Path) || Boards.BoardPaths.IsApi(c.Request.Path), api =>
        {
            // Logged as well: the client only learns that something failed.
            api.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => false });
            api.UseStatusCodePages();
        });
}

internal sealed class V1JsonInputFormatter(JsonOptions options, ILogger<SystemTextJsonInputFormatter> logger)
    : SystemTextJsonInputFormatter(options, logger)
{
    public override bool CanRead(InputFormatterContext context) =>
        ApiPaths.IsApi(context.HttpContext.Request.Path) && base.CanRead(context);
}

internal sealed class V1JsonOutputFormatter(JsonSerializerOptions options) : SystemTextJsonOutputFormatter(options)
{
    public override bool CanWriteResult(OutputFormatterCanWriteContext context) =>
        ApiPaths.IsApi(context.HttpContext.Request.Path) && base.CanWriteResult(context);
}

/// <summary>Turns <see cref="ApiException"/> and unique-key violations into ProblemDetails.</summary>
public sealed class ApiExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        ProblemDetails? problem = context.Exception switch
        {
            ApiException { Errors: not null } ex => WithCode(factory.CreateValidationProblemDetails(
                context.HttpContext, ToModelState(ex.Errors), ex.Status, detail: ex.Message), ex.Code),
            ApiException ex => WithCode(factory.CreateProblemDetails(context.HttpContext, ex.Status, detail: ex.Message), ex.Code),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } =>
                WithCode(factory.CreateProblemDetails(context.HttpContext, StatusCodes.Status409Conflict,
                    detail: "An entry with this key exists already."), ApiErrorCodes.DuplicateKey),
            _ => null,
        };
        if (problem is null)
        {
            return;
        }
        if (context.Exception is ApiException { Extensions: { } extensions })
        {
            foreach (var (name, value) in extensions)
            {
                problem.Extensions[name] = value;
            }
        }
        context.Result = new ObjectResult(problem)
        {
            StatusCode = problem.Status,
            ContentTypes = { "application/problem+json" },
        };
        context.ExceptionHandled = true;
    }

    private static ProblemDetails WithCode(ProblemDetails problem, string code)
    {
        problem.Extensions["code"] = code;
        return problem;
    }

    private static ModelStateDictionary ToModelState(IDictionary<string, string[]> errors)
    {
        var state = new ModelStateDictionary();
        foreach (var (field, messages) in errors)
        {
            foreach (var message in messages)
            {
                state.AddModelError(field, message);
            }
        }
        return state;
    }
}

/// <summary>The base of every v1 controller: JSON in and out, ProblemDetails for errors.</summary>
[ApiController]
[ApiExceptionFilter]
[Auth.CsrfProtection]
public abstract class V1ControllerBase : ControllerBase
{
    /// <summary>Tokens and sensor URLs: Monitoring's Edit (docs/audit-redmine-zabbix-plan.md §5.2).</summary>
    protected bool CanSeeSecrets => Auth.Rights.RightsExtensions.Has(User, Auth.Rights.Area.Monitoring, Auth.Rights.Level.Edit);

    /// <summary>What names people, as a server's logins, event log, journal and SSH keys: Monitoring's Details (docs/cookie-object-notes-plan.md §4).</summary>
    protected bool CanSeeDetails => Auth.Rights.RightsExtensions.Has(User, Auth.Rights.Area.Monitoring, Auth.Rights.Level.Details);
}
