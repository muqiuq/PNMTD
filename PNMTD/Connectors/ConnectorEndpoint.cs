using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Connectors;

/// <summary>
/// The connectors' paths (docs/connectors-plan.md §4.1), on PNMT's existing address and port: no cookie authentication, no CSRF, no
/// ProblemDetails, as the sensor API's paths. A call authenticates by its caller's key.
/// </summary>
public static class ConnectorPaths
{
    public const string Path = "/connector";

    public const string V1 = "/connector/v1";

    public static bool IsConnectorPath(PathString path) => path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A refusal at the endpoint (§4.3): the same small body for each, naming the arguments and patterns, never a value.</summary>
public sealed record ConnectorErrorAnswer(bool Ok, string Error, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Fields = null);

/// <summary>A call's outcome (§4.4): its state, and once final, the run's exit code and times; the output only where the connector returns it.</summary>
public sealed record ConnectorAnswer(
    bool Ok,
    Guid Ticket,
    string State,
    string? RefusedBy,
    string? WaitingFor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Reviewed,
    int? ExitCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Output,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OutputTruncated,
    string? Message,
    DateTime ReceivedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    long? DurationMs,
    string Poll,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool DryRun = false);

/// <summary>An exposed argument as the caller sees it (§4.7): never the fixed or omitted ones, the server, the script or the review's checks.</summary>
public sealed record ConnectorDescribedArgument(
    string Name,
    string Type,
    bool Required,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Pattern,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Help,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? OneOf,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Min,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Max,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Multiline);

/// <summary><c>GET /connector/v1/{slug}</c> (§4.7): what a caller needs to call the connector.</summary>
public sealed record ConnectorDescription(string Name, string Slug, string Purpose, string Mode, int? WaitSeconds, bool ReturnsOutput, bool Review,
    int CallsPerHour, string Url, IReadOnlyList<ConnectorDescribedArgument> Arguments);

/// <summary>An entry of <c>GET /connector/v1</c>: the connectors granted to the key's caller.</summary>
public sealed record ConnectorListed(string Name, string Slug, string Purpose, string Mode, bool Enabled, string Url);

/// <summary>
/// <c>/connector/v1</c> (docs/connectors-plan.md §4): the checks of §4.3 in their order, cheap and secret-free ones first; a call that
/// passes them is stored as queued and the worker nudged. Ticket mode answers at once; wait mode waits for the call to become final, at
/// most the connector's wait, and answers its ticket otherwise. The endpoint never changes a call after it stored it.
/// </summary>
public sealed class ConnectorGateway(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<ConnectorOptions> options, ConnectorGuard guard,
    ConnectorSecretVault vault, ConnectorCallSignals signals, ConnectorQueue queue, ConnectorAlarms alarms, ConnectorPollGate polls,
    ILogger<ConnectorGateway> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions BodyOptions = new() { MaxDepth = 16, AllowDuplicateProperties = false };

    private const int MaxReference = 200;
    private const int MaxNote = 1000;

    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder app)
    {
        app.MapPost(ConnectorPaths.V1 + "/{slug}", (HttpContext http, string slug, ConnectorGateway gateway, CancellationToken ct) => gateway.CallAsync(http, slug, ct))
            .AllowAnonymous().DisableAntiforgery().ExcludeFromDescription();
        app.MapGet(ConnectorPaths.V1 + "/{slug}/tickets/{ticket}",
                (HttpContext http, string slug, string ticket, ConnectorGateway gateway, CancellationToken ct) => gateway.TicketAsync(http, slug, ticket, ct))
            .AllowAnonymous().ExcludeFromDescription();
        app.MapGet(ConnectorPaths.V1 + "/{slug}", (HttpContext http, string slug, ConnectorGateway gateway, CancellationToken ct) => gateway.DescribeAsync(http, slug, ct))
            .AllowAnonymous().ExcludeFromDescription();
        app.MapGet(ConnectorPaths.V1, (HttpContext http, ConnectorGateway gateway, CancellationToken ct) => gateway.ListAsync(http, ct))
            .AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    private static IResult Error(int status, string error, string message, IReadOnlyDictionary<string, string>? fields = null) =>
        Results.Json(new ConnectorErrorAnswer(false, error, message, fields), Json, statusCode: status);

    public static string Poll(string slug, Guid ticket) => $"{ConnectorPaths.V1}/{slug}/tickets/{ticket}";

    public static string StateName(ConnectorCallState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());

    public static long ConnectorLock(Guid connectorId) => BitConverter.ToInt64(connectorId.ToByteArray(), 8) ^ 0x434F4E4E;

    private sealed record Caller(Guid Id, string Name, bool Enabled, IReadOnlyList<ConnectorNetwork> Networks);

    // --- checks 1 to 6 ---------------------------------------------------------------------------------------------------------------

    /// <summary>Checks 1 to 5 (§4.3): connectors on, the address not blocked, a key, the key's caller, the caller's networks.</summary>
    private async Task<(Caller? Caller, IResult? Refusal)> AuthenticateAsync(HttpContext http, PnmtdDbContext db, string? slug, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Enabled)
        {
            return (null, Results.NotFound());
        }
        var address = ConnectorNetworks.Text(http.Connection.RemoteIpAddress) ?? "unknown";
        if (guard.BlockedUntil(address) is { } until)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((until - Clock.UtcNow).TotalSeconds));
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return (null, Error(StatusCodes.Status429TooManyRequests, "blocked", $"Too many calls with a wrong key from this address: try again in {seconds} s."));
        }
        // R11: the key only in the header; one in the path or the query counts as none.
        var header = http.Request.Headers.Authorization.ToString();
        var key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        Caller? caller = null;
        if (ConnectorKeys.LooksLikeOne(key))
        {
            var hash = ConnectorKeys.Hash(key!);
            var row = await db.ConnectorCallers.AsNoTracking().Where(c => c.KeyHash == hash).Select(c => new { c.Id, c.Name, c.Enabled, c.Networks }).FirstOrDefaultAsync(ct);
            caller = row is null ? null : new Caller(row.Id, row.Name, row.Enabled, ConnectorNetworks.Read(row.Networks));
        }
        if (caller is null)
        {
            logger.LogInformation("Connector call to {Slug} from {Address}: {Why}", slug ?? "(list)", address, key is null ? "no key" : "an unknown key");
            if (guard.Failed(address, Math.Max(1, o.FailedKeysPerAddress), TimeSpan.FromMinutes(Math.Max(1, o.BlockMinutes))))
            {
                await alarms.RefusedAsync($"The address {address} is blocked on /connector for {o.BlockMinutes} minutes after {o.FailedKeysPerAddress} calls with a wrong key.",
                    new AuditWho(AuditActor.Connector, null, "", null, address, null), null, ct);
            }
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return (null, Error(StatusCodes.Status401Unauthorized, "unauthorized", "Send a caller's key: Authorization: Bearer pnmt_cx_…"));
        }
        if (!ConnectorNetworks.Contains(caller.Networks, http.Connection.RemoteIpAddress))
        {
            ConnectorEntity? named = null;
            if (slug is not null)
            {
                var lowered = slug.ToLowerInvariant();
                named = await db.Connectors.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == lowered && c.Grants.Any(g => g.CallerId == caller.Id), ct);
            }
            await StoreRefusedAsync(db, caller, named, slug ?? "", address, http.Request.Method == HttpMethods.Post ? "address_refused" : null,
                $"The key of {caller.Name} came from {address}, outside its networks.", ct);
            await alarms.RefusedAsync($"{caller.Name}{(named is null ? "" : $" ({named.Name})")}: its key from {address}, outside its networks. Rotate the key if this isn't expected.",
                AuditWho.Connector(address, caller.Id, caller.Name, named?.Id), AuditTarget.Of(AuditTargets.ConnectorCaller, caller.Id, caller.Name), ct);
            return (null, Error(StatusCodes.Status403Forbidden, "address_refused", $"This key may not call from {address}."));
        }
        if (!caller.Enabled)
        {
            return (caller, Error(StatusCodes.Status403Forbidden, "caller_disabled", "This key's caller is disabled in PNMT."));
        }
        return (caller, null);
    }

    /// <summary>Check 6: the slug names a connector granted to the caller (a missing one and one not granted answer the same), enabled.</summary>
    private static async Task<(ConnectorEntity? Connector, IResult? Refusal, string? RefusedBy)> ConnectorAsync(PnmtdDbContext db, Caller caller, string slug, bool requireEnabled,
        CancellationToken ct)
    {
        var lowered = slug.ToLowerInvariant();
        var connector = await db.Connectors.AsNoTracking().Include(c => c.Script).Include(c => c.Arguments)
            .FirstOrDefaultAsync(c => c.Slug == lowered && c.Grants.Any(g => g.CallerId == caller.Id), ct);
        if (connector is null || connector.Script is null)
        {
            return (null, Error(StatusCodes.Status404NotFound, "unknown_connector", "This key's caller has no connector of that name."), "unknown_connector");
        }
        if (requireEnabled && !connector.Enabled)
        {
            return (connector, Error(StatusCodes.Status403Forbidden, "connector_disabled", $"{connector.Name} is disabled in PNMT."), "connector_disabled");
        }
        return (connector, null, null);
    }

    /// <summary>A call refused at the endpoint, stored final at once (§4.3): only with a valid key, so nobody can fill the table.</summary>
    private static async Task StoreRefusedAsync(PnmtdDbContext db, Caller caller, ConnectorEntity? connector, string slug, string address, string? refusedBy,
        string message, CancellationToken ct, ConnectorArgumentCheck? check = null, string? reference = null, string? note = null)
    {
        if (refusedBy is null)
        {
            return;
        }
        var now = Clock.UtcNow;
        db.ConnectorCalls.Add(new ConnectorCallEntity
        {
            Id = Guid.NewGuid(), ConnectorId = connector?.Id, ConnectorName = Cuts.Cut(connector?.Name ?? slug, 200), Slug = Cuts.Cut(slug.ToLowerInvariant(), 64),
            CallerId = caller.Id, CallerName = caller.Name, ReceivedAt = now, From = address, State = ConnectorCallState.Refused, RefusedBy = refusedBy,
            Message = Cuts.Cut(message, 2000), FinishedAt = now, Mode = connector?.Mode ?? ConnectorMode.Wait, Reference = reference, Note = note,
            Arguments = check is { CallerPlain.Count: > 0 } ? JsonSerializer.Serialize(check.CallerPlain) : null,
            SecureNames = check?.Secure.Keys.ToList() ?? [],
        });
        await db.SaveChangesAsync(ct);
    }

    // --- POST /connector/v1/{slug} ---------------------------------------------------------------------------------------------------

    public async Task<IResult> CallAsync(HttpContext http, string slug, CancellationToken ct)
    {
        var o = options.CurrentValue;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var (caller, refusal) = await AuthenticateAsync(http, db, slug, ct);
        var address = ConnectorNetworks.Text(http.Connection.RemoteIpAddress) ?? "unknown";
        if (refusal is not null)
        {
            if (caller is not null)
            {
                await StoreRefusedAsync(db, caller, null, slug, address, "caller_disabled", "The caller is disabled.", ct);
            }
            return refusal;
        }
        var (connector, refused, refusedBy) = await ConnectorAsync(db, caller!, slug, requireEnabled: true, ct);
        if (refused is not null)
        {
            await StoreRefusedAsync(db, caller!, connector, slug, address, refusedBy, refusedBy == "connector_disabled"
                ? $"{connector!.Name} is disabled." : $"No connector \"{slug}\" is granted to {caller!.Name}.", ct);
            return refused;
        }
        await TouchAsync(db, caller!.Id, connector!.Id, address, ct);

        // 7: the body.
        var (body, bodyError) = await ReadAsync(http, o.MaxBodyBytes, ct);
        if (bodyError is not null)
        {
            await StoreRefusedAsync(db, caller, connector, slug, address, bodyError.Value.Code, bodyError.Value.Message, ct);
            return Error(bodyError.Value.Code == "too_large" ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest, bodyError.Value.Code,
                bodyError.Value.Message);
        }
        var request = body!;

        // §5.5: a changed text whose arguments wait for an admin refuses calls until they are saved again.
        if (connector.Script!.NeedsReview)
        {
            await StoreRefusedAsync(db, caller, connector, slug, address, "arguments_need_review",
                "The connector's script changed, and its arguments wait for an admin in PNMT.", ct);
            return Error(StatusCodes.Status409Conflict, "arguments_need_review",
                $"{connector.Name}'s script changed, and its arguments wait for an admin in PNMT: try again later.");
        }

        // 8: a retry with the same Idempotency-Key answers the first call.
        var idempotency = http.Request.Headers["Idempotency-Key"].ToString();
        if (idempotency.Length > 0 && (idempotency.Length > 100 || idempotency.Any(c => c < 0x21 || c > 0x7E)))
        {
            await StoreRefusedAsync(db, caller, connector, slug, address, "invalid_body", "Idempotency-Key: 1 to 100 printable characters.", ct);
            return Error(StatusCodes.Status400BadRequest, "invalid_body", "Idempotency-Key: 1 to 100 printable characters, no spaces.");
        }
        var argumentsHash = ArgumentsHash(request.Arguments, request.Reference, request.Note);
        if (idempotency.Length > 0 && !request.DryRun)
        {
            if (await FirstCallAsync(db, caller.Id, connector.Id, idempotency, o, ct) is { } first)
            {
                if (first.ArgumentsHash != argumentsHash)
                {
                    return Error(StatusCodes.Status422UnprocessableEntity, "idempotency_mismatch",
                        "This Idempotency-Key was used with other arguments: use a new key for a new call.");
                }
                return await AnswerAsync(db, first.Id, connector.Slug, ct);
            }
        }

        // 9: the arguments, every problem listed; one refuses the call.
        var rules = connector.Arguments.OrderBy(a => a.Order).Select(ConnectorArguments.Rule).ToList();
        var check = ConnectorArguments.Check(rules, request.Arguments, o.MaxValueChars, o.PatternTimeoutMs);
        if (!check.Ok)
        {
            await StoreRefusedAsync(db, caller, connector, slug, address, "arguments_invalid",
                $"{check.Message} " + string.Join("; ", check.Fields.Select(f => $"{f.Key}: {f.Value}")), ct, check, request.Reference, request.Note);
            return Error(StatusCodes.Status422UnprocessableEntity, "arguments_invalid", check.Message, check.Fields);
        }

        // 10 and 11: the limits under the connector's lock, and the call stored as queued.
        var now = Clock.UtcNow;
        var call = new ConnectorCallEntity
        {
            Id = Guid.NewGuid(), ConnectorId = connector.Id, ConnectorName = connector.Name, Slug = connector.Slug, CallerId = caller.Id, CallerName = caller.Name,
            ReceivedAt = now, From = address, State = ConnectorCallState.Queued, Accepted = true, DryRun = request.DryRun, Mode = connector.Mode, WaitingFor = "worker",
            ExpiresAt = now.AddSeconds(Math.Clamp(connector.MaxQueueSeconds, 10, 3600)), ScriptName = connector.Script!.Name, ScriptRevision = connector.Script.ScriptRevision,
            Hash = connector.Script.Hash, Function = connector.Script.Function,
            Arguments = check.CallerPlain.Count > 0 ? JsonSerializer.Serialize(check.CallerPlain) : null, SecureNames = check.Secure.Keys.ToList(),
            Reference = request.Reference, Note = request.Note, IdempotencyKey = idempotency.Length > 0 && !request.DryRun ? idempotency : null,
            ArgumentsHash = argumentsHash,
        };
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({ConnectorLock(connector.Id)})", ct);
            var hourAgo = now.AddHours(-1);
            var lastHour = await db.ConnectorCalls.Where(c => c.ConnectorId == connector.Id && c.Accepted && c.ReceivedAt > hourAgo)
                .OrderBy(c => c.ReceivedAt).Select(c => c.ReceivedAt).Take(3600).ToListAsync(ct);
            if (lastHour.Count >= Math.Clamp(connector.MaxCallsPerHour, 1, 3600))
            {
                var seconds = Math.Max(1, (int)Math.Ceiling((Clock.AsUtc(lastHour[0]).AddHours(1) - now).TotalSeconds));
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await StoreRefusedAsync(db, caller, connector, slug, address, "rate_limited", $"{lastHour.Count} calls in the last hour, the most it takes.", ct,
                    check, request.Reference, request.Note);
                await transaction.CommitAsync(ct);
                return Error(StatusCodes.Status429TooManyRequests, "rate_limited",
                    $"{connector.Name} takes at most {connector.MaxCallsPerHour} calls an hour: try again in {seconds} s.");
            }
            var waiting = await db.ConnectorCalls.CountAsync(c => c.ConnectorId == connector.Id && c.State == ConnectorCallState.Queued, ct);
            if (waiting >= Math.Clamp(connector.MaxQueued, 1, 100))
            {
                http.Response.Headers.RetryAfter = "10";
                await StoreRefusedAsync(db, caller, connector, slug, address, "queue_full", $"{waiting} calls waiting, the most it takes.", ct, check,
                    request.Reference, request.Note);
                await transaction.CommitAsync(ct);
                return Error(StatusCodes.Status429TooManyRequests, "queue_full", $"{connector.Name} has {waiting} calls waiting, the most it takes: try again in 10 s.");
            }
            // §5.4: the secrets wait in memory, never in the table; in place before the row is visible to the worker.
            vault.Put(call.Id, check.Secure);
            db.ConnectorCalls.Add(call);
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && call.IdempotencyKey is not null)
            {
                vault.Remove(call.Id);
                await transaction.RollbackAsync(ct);
                await using var again = await dbs.CreateDbContextAsync(ct);
                var first = await FirstCallAsync(again, caller.Id, connector.Id, idempotency, o, ct);
                return first is null ? Error(StatusCodes.Status409Conflict, "idempotency_conflict", "Send the call again.")
                    : first.ArgumentsHash != argumentsHash
                        ? Error(StatusCodes.Status422UnprocessableEntity, "idempotency_mismatch", "This Idempotency-Key was used with other arguments: use a new key for a new call.")
                        : await AnswerAsync(again, first.Id, connector.Slug, ct);
            }
            catch
            {
                vault.Remove(call.Id);
                throw;
            }
        }

        var waiter = connector.Mode == ConnectorMode.Wait ? signals.WaitAsync(call.Id, TimeSpan.FromSeconds(2), ct) : null;
        queue.Nudge();
        if (connector.Mode == ConnectorMode.Ticket)
        {
            http.Response.Headers.Location = Poll(connector.Slug, call.Id);
            return await AnswerAsync(db, call.Id, connector.Slug, ct, forceAccepted: true);
        }

        // Wait mode (§4.5, §9.3): the signal, and a read every 2 s as the fallback; 202 with the ticket when the time is up.
        var until = now.AddSeconds(Math.Clamp(connector.WaitSeconds, 5, o.WaitLimit));
        try
        {
            while (true)
            {
                await waiter!;
                var state = await db.ConnectorCalls.AsNoTracking().Where(c => c.Id == call.Id).Select(c => c.State).FirstAsync(ct);
                if (!IsActive(state))
                {
                    break;
                }
                var remaining = until - Clock.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    http.Response.Headers.Location = Poll(connector.Slug, call.Id);
                    break;
                }
                waiter = signals.WaitAsync(call.Id, remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), ct);
            }
        }
        finally
        {
            signals.Forget(call.Id);
        }
        return await AnswerAsync(db, call.Id, connector.Slug, ct);
    }

    public static bool IsActive(ConnectorCallState state) => state is ConnectorCallState.Queued or ConnectorCallState.Running;

    private static async Task<ConnectorCallEntity?> FirstCallAsync(PnmtdDbContext db, Guid callerId, Guid connectorId, string key, ConnectorOptions o, CancellationToken ct)
    {
        var since = Clock.UtcNow.AddHours(-Math.Max(1, o.IdempotencyHours));
        return await db.ConnectorCalls.AsNoTracking()
            .Where(c => c.CallerId == callerId && c.ConnectorId == connectorId && c.IdempotencyKey == key && c.ReceivedAt >= since).FirstOrDefaultAsync(ct);
    }

    /// <summary>The SHA-256 of what decides a retry is the same call: the arguments (secrets included), the reference, the note.</summary>
    private static string ArgumentsHash(IReadOnlyDictionary<string, JsonElement> arguments, string? reference, string? note)
    {
        var text = JsonSerializer.Serialize(new { a = arguments.OrderBy(a => a.Key, StringComparer.Ordinal).ToDictionary(a => a.Key, a => a.Value), r = reference, n = note });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The caller's and the connector's last call, at most once a minute (like MCP's last use).</summary>
    private static async Task TouchAsync(PnmtdDbContext db, Guid callerId, Guid connectorId, string address, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var minuteAgo = now.AddMinutes(-1);
        await db.ConnectorCallers.Where(c => c.Id == callerId && (c.LastCallAt == null || c.LastCallAt < minuteAgo || c.LastCallFrom != address))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.LastCallAt, now).SetProperty(c => c.LastCallFrom, address), ct);
        await db.Connectors.Where(c => c.Id == connectorId && (c.LastCallAt == null || c.LastCallAt < minuteAgo))
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.LastCallAt, now), ct);
    }

    /// <summary>The answer of §4.4 for a call: 200 when it is final, 202 while it isn't.</summary>
    private static async Task<IResult> AnswerAsync(PnmtdDbContext db, Guid callId, string slug, CancellationToken ct, bool forceAccepted = false)
    {
        var answer = await AnswerOfAsync(db, callId, slug, ct);
        return Results.Json(answer, Json, statusCode: forceAccepted || IsActiveName(answer.State) ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
    }

    private static bool IsActiveName(string state) => state is "queued" or "running";

    public static async Task<ConnectorAnswer> AnswerOfAsync(PnmtdDbContext db, Guid callId, string slug, CancellationToken ct)
    {
        var call = await db.ConnectorCalls.AsNoTracking().SingleAsync(c => c.Id == callId, ct);
        var returnsOutput = call.ConnectorId is { } connectorId && await db.Connectors.AsNoTracking().Where(c => c.Id == connectorId).Select(c => c.ReturnOutput).FirstOrDefaultAsync(ct);
        var run = call.RunId is { } runId
            ? await db.AgentScriptRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => new { r.ExitCode, r.Output, r.DurationMs }).FirstOrDefaultAsync(ct)
            : null;
        var review = ConnectorReviewRecord.Read(call.Review);
        bool? reviewed = review?.State switch
        {
            ConnectorReviewRecord.Reviewed => true,
            ConnectorReviewRecord.Unreviewed => false,
            _ => null,
        };
        var final = !IsActive(call.State);
        var output = final && returnsOutput ? run?.Output : null;
        long? duration = run?.DurationMs is { } ms ? (long)Math.Round(ms)
            : call.StartedAt is { } s && call.FinishedAt is { } f ? (long)(f - s).TotalMilliseconds : null;
        return new ConnectorAnswer(true, call.Id, StateName(call.State), call.RefusedBy, IsActive(call.State) ? call.WaitingFor : null, reviewed,
            final ? run?.ExitCode : null, output, output is null ? null : output.EndsWith("… (cut at 1 MiB)", StringComparison.Ordinal),
            call.Message, Clock.AsUtc(call.ReceivedAt), call.StartedAt is { } st ? Clock.AsUtc(st) : null, call.FinishedAt is { } fi ? Clock.AsUtc(fi) : null,
            final ? duration : null, Poll(slug, call.Id), call.DryRun);
    }

    // --- the body (§4.2) -------------------------------------------------------------------------------------------------------------

    private sealed record CallBody(IReadOnlyDictionary<string, JsonElement> Arguments, string? Reference, string? Note, bool DryRun);

    /// <summary>
    /// The body within Connectors__MaxBodyBytes: refused by Content-Length before reading, and while reading a chunked one. Read as UTF-8
    /// JSON whatever the Content-Type says; one object with no key twice and nothing but its four properties.
    /// </summary>
    private static async Task<(CallBody? Body, (string Code, string Message)? Error)> ReadAsync(HttpContext http, int maxBytes, CancellationToken ct)
    {
        maxBytes = Math.Clamp(maxBytes, 1024, 1024 * 1024);
        if (http.Request.ContentLength is { } announced && announced > maxBytes)
        {
            return (null, ("too_large", $"The body has {announced} bytes; at most {maxBytes}."));
        }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes + 1L;
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await http.Request.Body.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > maxBytes)
                {
                    return (null, ("too_large", $"The body is larger than {maxBytes} bytes."));
                }
                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, ("too_large", $"The body is larger than {maxBytes} bytes."));
        }
        if (buffer.Length == 0)
        {
            return (new CallBody(new Dictionary<string, JsonElement>(), null, null, false), null);
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), BodyOptions);
        }
        catch (JsonException e)
        {
            return (null, ("invalid_body", $"The body isn't valid JSON: {Cuts.Cut(e.Message, 200)}"));
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, ("invalid_body", "The body is one JSON object: { \"arguments\": { … } }."));
            }
            var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            string? reference = null, note = null;
            var dryRun = false;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "arguments":
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            break;
                        }
                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            return (null, ("invalid_body", "arguments is an object, one property per argument."));
                        }
                        foreach (var argument in property.Value.EnumerateObject())
                        {
                            arguments[argument.Name] = argument.Value.Clone();
                        }
                        break;
                    case "reference":
                        if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { } r
                            || ConnectorArguments.CharactersProblem(r, false, MaxReference) is not null)
                        {
                            return (null, ("invalid_body", $"reference is a string of at most {MaxReference} characters, without control characters."));
                        }
                        reference = r.Length == 0 ? null : r;
                        break;
                    case "note":
                        if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { } n
                            || ConnectorArguments.CharactersProblem(n, true, MaxNote) is not null)
                        {
                            return (null, ("invalid_body", $"note is a string of at most {MaxNote} characters."));
                        }
                        note = n.Length == 0 ? null : n;
                        break;
                    case "dryRun":
                        if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            return (null, ("invalid_body", "dryRun is true or false."));
                        }
                        dryRun = property.Value.GetBoolean();
                        break;
                    default:
                        return (null, ("invalid_body", $"Unknown property {Cuts.Cut(property.Name, 60)}: the body has arguments, reference, note and dryRun."));
                }
            }
            return (new CallBody(arguments, reference, note, dryRun), null);
        }
    }

    // --- GET: a ticket, the description, the list -----------------------------------------------------------------------------------------

    /// <summary><c>GET /connector/v1/{slug}/tickets/{ticket}</c> (§4.6): the outcome, for the caller that made the call, once a second.</summary>
    public async Task<IResult> TicketAsync(HttpContext http, string slug, string ticket, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var (caller, refusal) = await AuthenticateAsync(http, db, null, ct);
        if (refusal is not null)
        {
            return refusal;
        }
        var lowered = slug.ToLowerInvariant();
        var call = Guid.TryParse(ticket, out var id)
            ? await db.ConnectorCalls.AsNoTracking().Where(c => c.Id == id && c.CallerId == caller!.Id && c.Slug == lowered && c.Accepted)
                .Select(c => new { c.Id, c.State, c.CollectedAt }).FirstOrDefaultAsync(ct)
            : null;
        if (call is null)
        {
            return Error(StatusCodes.Status404NotFound, "unknown_ticket", "No such ticket for this key's caller and this connector.");
        }
        if (!polls.TryEnter(call.Id))
        {
            http.Response.Headers.RetryAfter = "1";
            return Error(StatusCodes.Status429TooManyRequests, "rate_limited", "Ask for a ticket at most once a second.");
        }
        if (!IsActive(call.State) && call.CollectedAt is null)
        {
            var now = Clock.UtcNow;
            await db.ConnectorCalls.Where(c => c.Id == call.Id && c.CollectedAt == null).ExecuteUpdateAsync(u => u.SetProperty(c => c.CollectedAt, now), ct);
        }
        return Results.Json(await AnswerOfAsync(db, call.Id, lowered, ct), Json);
    }

    /// <summary><c>GET /connector/v1/{slug}</c> (§4.7).</summary>
    public async Task<IResult> DescribeAsync(HttpContext http, string slug, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var (caller, refusal) = await AuthenticateAsync(http, db, null, ct);
        if (refusal is not null)
        {
            return refusal;
        }
        var (connector, refused, _) = await ConnectorAsync(db, caller!, slug, requireEnabled: true, ct);
        if (refused is not null)
        {
            return refused;
        }
        return Results.Json(Describe(connector!, PublicBase(http)), Json);
    }

    /// <summary><c>GET /connector/v1</c>: the connectors granted to the key's caller.</summary>
    public async Task<IResult> ListAsync(HttpContext http, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var (caller, refusal) = await AuthenticateAsync(http, db, null, ct);
        if (refusal is not null)
        {
            return refusal;
        }
        var baseUrl = PublicBase(http);
        var connectors = await db.ConnectorGrants.AsNoTracking().Where(g => g.CallerId == caller!.Id).Select(g => g.Connector).OrderBy(c => c.Name).ToListAsync(ct);
        return Results.Json(new
        {
            ok = true,
            caller = caller!.Name,
            connectors = connectors.Select(c => new ConnectorListed(c.Name, c.Slug, c.Purpose, StateNameOf(c.Mode), c.Enabled, $"{baseUrl}{ConnectorPaths.V1}/{c.Slug}")),
        }, Json);
    }

    private static string StateNameOf(ConnectorMode mode) => mode == ConnectorMode.Ticket ? "ticket" : "wait";

    private static string PublicBase(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    /// <summary>The description (§4.7): the exposed arguments with their types, requirements, patterns, sets and ranges.</summary>
    public static ConnectorDescription Describe(ConnectorEntity connector, string baseUrl)
    {
        var arguments = connector.Arguments.OrderBy(a => a.Order).Select(ConnectorArguments.Rule).Where(r => r.Mode == ConnectorArgumentMode.Exposed)
            .Select(r => new ConnectorDescribedArgument(r.Parameter.Name, r.Kind switch
            {
                ConnectorValueKind.Bool => "boolean",
                ConnectorValueKind.Number => "number",
                _ => "string",
            }, r.Required || r.Parameter.Mandatory, r.Pattern, r.Description ?? r.Parameter.HelpMessage,
                r.Kind == ConnectorValueKind.Secure ? null : r.Parameter.Default, r.Parameter.ValidateSet is { Count: > 0 } set ? set : null, r.Parameter.RangeMin,
                r.Parameter.RangeMax, r.Multiline && r.Kind == ConnectorValueKind.Text)).ToList();
        return new ConnectorDescription(connector.Name, connector.Slug, connector.Purpose, StateNameOf(connector.Mode),
            connector.Mode == ConnectorMode.Wait ? connector.WaitSeconds : null, connector.ReturnOutput, connector.ReviewEnabled, connector.MaxCallsPerHour,
            $"{baseUrl}{ConnectorPaths.V1}/{connector.Slug}", arguments);
    }
}
