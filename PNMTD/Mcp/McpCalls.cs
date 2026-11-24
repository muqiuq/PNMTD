using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PNMTD.Api;
using PNMTD.Audit;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Tools;

namespace PNMTD.Mcp;

/// <summary>A catalog tool as the SDK offers it (docs/mcp-plan.md §6.1). The SDK sees no PNMT types beyond this adapter.</summary>
public sealed class CatalogTool(ToolSpec spec) : McpServerTool
{
    public ToolSpec Spec { get; } = spec;

    public override Tool ProtocolTool { get; } = new()
    {
        Name = spec.Name,
        Title = spec.Title,
        Description = spec.Description + (spec.RightText == "none" ? "" : $" Needs {spec.RightText}."),
        InputSchema = JsonSerializer.SerializeToElement(spec.InputSchema),
        Annotations = new ToolAnnotations
        {
            Title = spec.Title,
            ReadOnlyHint = spec.Kind == ToolKind.Read,
            DestructiveHint = spec.Kind == ToolKind.Delete || spec.Destructive,
            IdempotentHint = spec.Kind == ToolKind.Read || spec.Idempotent,
            OpenWorldHint = spec.OpenWorld,
        },
    };

    public override IReadOnlyList<object> Metadata { get; } = [spec];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) =>
        McpCalls.InvokeAsync(Spec, request, cancellationToken);
}

/// <summary>
/// The MCP server's requests (docs/mcp-plan.md §5, §6.2, §9): what a token is offered, what it may call, and the call
/// itself, inside the <c>/mcp</c> request, with the token's principal on <c>HttpContext.User</c>. Without that request
/// nothing runs: PNMT's services read the caller from it, and some take a missing one for the CLI.
/// </summary>
public static class McpCalls
{
    /// <summary>The client's name and version, where it says (the user agent until the SDK has parsed its info).</summary>
    public static string? ClientOf(HttpContext http) => McpLog.Cut(http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null, 200);

    private static string? ClientOf(MessageContext request, HttpContext http) =>
        request.JsonRpcMessage.Context?.ClientInfo is { } info ? McpLog.Cut($"{info.Name} {info.Version}".Trim(), 200) : ClientOf(http);

    /// <summary>Where PNMT's pages are, as this request reached PNMT: the internal name the person uses.</summary>
    public static string BaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    private static (HttpContext Http, McpCaller Caller)? Request(MessageContext request)
    {
        var http = request.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
        return http is not null && McpPrincipal.IsMcp(http.User) && McpTokenHandler.CallerOf(http) is { } caller ? (http, caller) : null;
    }

    public static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListFilter(McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (request, ct) =>
        {
            var result = await next(request, ct);
            if (Request(request) is not var (http, caller))
            {
                result.Tools = [];
                return result;
            }
            var allowed = ToolCatalog.ForMcp(caller.Rights, caller.Token.Kind).Where(t => Available(t, http.RequestServices)).Select(t => t.Name).ToHashSet();
            result.Tools = [.. result.Tools.Where(t => allowed.Contains(t.Name))];
            await http.RequestServices.GetRequiredService<McpLog>().WriteAsync(Entry(http, caller, request, "tools/list", null, McpOutcome.Ok,
                $"{result.Tools.Count} tools"));
            return result;
        };

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> CallFilter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, ct) =>
        {
            if (Request(request) is not var (http, caller))
            {
                return Error("internal_error", "This call has no MCP request around it.");
            }
            var name = request.Params?.Name ?? "";
            var spec = ToolCatalog.Find(name);
            var log = http.RequestServices.GetRequiredService<McpLog>();
            if (spec is null || !ToolCatalog.KindAllows(spec, caller.Token.Kind) || !spec.Allows(caller.Rights, ToolCaller.Mcp)
                || !Available(spec, http.RequestServices))
            {
                // Not in this token's list: a client only has it from another token, or guessed (§8.6).
                var message = spec is null ? $"There is no tool {name}." : $"{name} isn't available to this token.";
                await log.WriteAsync(Entry(http, caller, request, "tools/call", spec, McpOutcome.Denied, message, "not_available", message, strike: true));
                await http.RequestServices.GetRequiredService<McpStrikes>().RecordAsync(caller.Token.Id, BaseUrl(http), CancellationToken.None);
                return Error("not_available", message);
            }
            if (http.RequestServices.GetRequiredService<McpLimits>().Take(caller.Token.Id, spec.Writes) is { } wait)
            {
                var seconds = (int)Math.Ceiling(wait.TotalSeconds);
                var message = $"Too many calls: try again in {seconds} seconds.";
                await log.WriteAsync(Entry(http, caller, request, "tools/call", spec, McpOutcome.RateLimited, message, "rate_limited", message));
                return Error("rate_limited", message, new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds });
            }
            if (spec.Kind == ToolKind.Delete && await DeleteRefusedAsync(http, caller) is { } refused)
            {
                await log.WriteAsync(Entry(http, caller, request, "tools/call", spec, McpOutcome.Denied, refused.Message, refused.Code, refused.Message));
                return Error(refused.Code, refused.Message);
            }
            return await next(request, ct);
        };

    /// <summary>A tool that depends on something configured, as <c>cookie_ask</c> on Jev, is offered only while it is.</summary>
    private static bool Available(ToolSpec spec, IServiceProvider services) => spec.Available?.Invoke(services) ?? true;

    /// <summary>The window for deleting (§7.5), counted before the delete runs: the 21st call is refused, whatever happens to the 20th.</summary>
    private static async Task<(string Code, string Message)?> DeleteRefusedAsync(HttpContext http, McpCaller caller)
    {
        var options = http.RequestServices.GetRequiredService<IOptionsMonitor<McpOptions>>().CurrentValue;
        var db = http.RequestServices.GetRequiredService<PnmtdDbContext>();
        var now = Clock.UtcNow;
        var max = Math.Max(1, options.DeletesPerWindow);
        var counted = await db.McpTokens.Where(t => t.Id == caller.Token.Id && t.DeleteUntil > now && t.DeleteCount < max)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.DeleteCount, t => t.DeleteCount + 1));
        if (counted == 1)
        {
            return null;
        }
        var open = await db.McpTokens.AnyAsync(t => t.Id == caller.Token.Id && t.DeleteUntil > now);
        return open
            ? ("delete_limit_reached", $"This window for deleting allowed {max} objects, and they are used up. Ask the person to open a new one.")
            : ("delete_not_allowed", "Deleting isn't allowed right now. Ask the person to click \"Allow deleting for 10 minutes\" on Account → Security in PNMT, then try again.");
    }

    /// <summary>The call itself (§6.2): the tool in this request's scope, its audit for a change, its result, its log entry.</summary>
    public static async ValueTask<CallToolResult> InvokeAsync(ToolSpec spec, RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        if (Request(request) is not var (http, caller))
        {
            return Error("internal_error", "This call has no MCP request around it.");
        }
        var services = http.RequestServices;
        var options = services.GetRequiredService<IOptionsMonitor<McpOptions>>().CurrentValue;
        var started = Stopwatch.GetTimestamp();
        var argumentsNode = JsonSerializer.SerializeToNode(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
        var args = JsonSerializer.SerializeToElement(argumentsNode);
        var ctx = new ToolContext(services, caller.Rights, NoMasking.Instance)
        {
            Mcp = new McpCallInfo(caller.User.Id, caller.User.Username, caller.Token.Id, caller.Token.Kind, BaseUrl(http), http.TraceIdentifier),
        };
        var db = services.GetRequiredService<PnmtdDbContext>();
        // A write the endpoint doesn't audit (ToolSpec.NotAudited) has no scope: the MCP log has its call.
        var scope = spec.Writes && spec.AuditEvents.Count > 0
            ? AuditScope.Begin(spec.AuditEvents, AuditWho.From(http), $"MCP {spec.Name}", http.TraceIdentifier, null)
            : null;
        object? result = null;
        (int Status, string Code, string Message, IReadOnlyDictionary<string, object?>? Extensions)? failure = null;
        try
        {
            result = await spec.Run(args, ctx, db, ct);
        }
        catch (ToolException e)
        {
            failure = (e.Code == "not_found" ? 404 : 400, e.Code, e.Message, e.Extensions);
        }
        catch (ApiException e)
        {
            var message = e.Errors is { Count: > 0 } errors ? $"{e.Message} {string.Join(" ", errors.Select(x => $"{x.Key}: {string.Join(" ", x.Value)}"))}" : e.Message;
            failure = (e.Status, e.Code, message, e.Extensions);
        }
        catch (RedmineException e)
        {
            failure = (502, "redmine", $"Redmine: {e.Message}", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            failure = (499, "cancelled", "The call was cancelled.", null);
        }
        catch (Exception e)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpCalls)).LogError(e, "MCP tool {Tool} failed", spec.Name);
            failure = (500, ApiErrorCodes.InternalError, $"Something went wrong in PNMT ({http.TraceIdentifier}).", null);
        }
        if (scope is not null)
        {
            await AuditFilter.CompleteAsync(services.GetRequiredService<IDbContextFactory<PnmtdDbContext>>(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpCalls)), scope, failure?.Status ?? 200, failure?.Code);
        }
        CallToolResult answer;
        string text;
        var truncated = false;
        if (failure is { } f)
        {
            answer = Error(f.Code, f.Message, f.Extensions);
            text = ((TextContentBlock)answer.Content[0]).Text;
        }
        else
        {
            (text, var node, truncated) = ToolResults.Serialize(result, Math.Max(2000, options.MaxResultChars));
            answer = new CallToolResult { Content = [new TextContentBlock { Text = text }] };
            if (node is JsonObject)
            {
                answer.StructuredContent = JsonSerializer.SerializeToElement(node);
            }
        }
        var entry = Entry(http, caller, request, "tools/call", spec, failure is null ? McpOutcome.Ok : McpOutcome.ToolError,
            ctx.Summary ?? (failure is { } ff ? ff.Message : spec.Title), failure?.Code, failure?.Message);
        entry.Arguments = McpLog.Redact(argumentsNode);
        (entry.Result, var cut) = McpLog.Pack(text);
        entry.Truncated = truncated || cut;
        entry.Targets = ctx.Targets.Count == 0 ? null
            : JsonSerializer.Serialize(ctx.Targets.Select(t => new { type = t.Type, id = t.Id, name = t.Name }));
        entry.DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        entry.CookieMessageId = ctx.CookieMessageId;
        entry.Guard = ctx.Guard;
        entry.Strike = ctx.Strike;
        if (ctx.Strike)
        {
            entry.Outcome = McpOutcome.Refused;
        }
        await services.GetRequiredService<McpLog>().WriteAsync(entry);
        if (ctx.Strike)
        {
            await services.GetRequiredService<McpStrikes>().RecordAsync(caller.Token.Id, BaseUrl(http), CancellationToken.None);
        }
        return answer;
    }

    /// <summary>A refusal or failure the client's AI can read: <c>{ error, message, … }</c>.</summary>
    public static CallToolResult Error(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var body = new JsonObject { ["error"] = code, ["message"] = message };
        foreach (var (key, value) in extensions ?? new Dictionary<string, object?>())
        {
            body[key] = JsonSerializer.SerializeToNode(value, ToolResults.Json);
        }
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = body.ToJsonString(ToolResults.Json) }] };
    }

    private static McpLogEntity Entry(HttpContext http, McpCaller caller, MessageContext request, string method, ToolSpec? spec, McpOutcome outcome,
        string summary, string? code = null, string? error = null, bool strike = false)
    {
        var audit = AuditContext.From(http);
        return new McpLogEntity
        {
            At = Clock.UtcNow,
            CorrelationId = http.TraceIdentifier,
            TokenId = caller.Token.Id,
            Kind = caller.Token.Kind,
            UserId = caller.User.Id,
            Username = caller.User.Username,
            Ip = audit.IpAddress,
            UserAgent = McpLog.Cut(audit.UserAgent, 300),
            Client = ClientOf(request, http),
            Method = method,
            Tool = spec?.Name ?? (request is RequestContext<CallToolRequestParams> call ? McpLog.Cut(call.Params?.Name, 64) : null),
            ToolKind = spec?.Kind.ToString(),
            Outcome = outcome,
            ErrorCode = code,
            Error = McpLog.Cut(error, 1000),
            Summary = McpLog.Cut(summary, 500) ?? "",
            Strike = strike,
        };
    }
}
