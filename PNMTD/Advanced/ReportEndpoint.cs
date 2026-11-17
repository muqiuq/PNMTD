using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Advanced;

/// <summary>
/// The reports' path (docs/advanced-sensor-plan.md §4.1), on PNMT's existing address and port: no cookie authentication, no
/// CSRF, no ProblemDetails, as the sensor API's paths. A report authenticates by its sensor's write token.
/// </summary>
public static class ReportPaths
{
    public const string Path = "/report";

    public static bool IsReportPath(PathString path) => path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One report per sensor every <c>Advanced__MinSecondsBetweenReports</c> (§4.1), dry runs included.</summary>
public sealed class ReportGate
{
    private readonly ConcurrentDictionary<Guid, DateTime> last = new();

    /// <summary>False with the wait when the sensor's last accepted report is too recent.</summary>
    public bool TryEnter(Guid sensorId, TimeSpan between, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (between <= TimeSpan.Zero)
        {
            return true;
        }
        var now = Clock.UtcNow;
        var wait = TimeSpan.Zero;
        var accepted = false;
        last.AddOrUpdate(sensorId, _ =>
        {
            accepted = true;
            return now;
        }, (_, previous) =>
        {
            if (now - previous >= between)
            {
                accepted = true;
                return now;
            }
            accepted = false;
            wait = between - (now - previous);
            return previous;
        });
        retryAfter = wait;
        if (last.Count > 100_000)
        {
            foreach (var (key, value) in last)
            {
                if (now - value > TimeSpan.FromHours(1))
                {
                    last.TryRemove(key, out _);
                }
            }
        }
        return accepted;
    }

    /// <summary>For tests.</summary>
    public void Reset() => last.Clear();
}

/// <summary>What a report's answer says the verdict will be (§4.3): the pure function's, with nothing written.</summary>
public sealed record ReportPreview(int Code, string State, string Message, IReadOnlyList<string> Failed, IReadOnlyList<string> Warnings);

/// <summary>The answer to a report (§4.3): small JSON, camelCase, never the report itself.</summary>
public sealed record ReportAnswer(bool Ok, Guid? ReportId = null, ReportPreview? Preview = null, string? Error = null, string? Message = null,
    bool? DryRun = null);

/// <summary>
/// <c>POST /report</c> with <c>Authorization: Bearer &lt;write token&gt;</c>, or <c>POST /report/&lt;write token&gt;</c> (§4.1):
/// anonymous, authenticated by the token. Stores the report and wakes the evaluator; judges nothing itself (§7.1).
/// </summary>
public static class ReportEndpoint
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Task<IResult> HeaderAsync(HttpContext http, IDbContextFactory<PnmtdDbContext> dbs, AdvancedEvaluator evaluator, ReportGate gate,
        IOptionsMonitor<AdvancedOptions> options, CancellationToken ct)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (string.IsNullOrEmpty(token))
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Task.FromResult(Answer(StatusCodes.Status401Unauthorized, new ReportAnswer(false, Error: "no_token",
                Message: "Send the sensor's write token: Authorization: Bearer <token>, or POST /report/<token>.")));
        }
        return HandleAsync(http, token, dbs, evaluator, gate, options, ct);
    }

    public static Task<IResult> PathAsync(HttpContext http, string token, IDbContextFactory<PnmtdDbContext> dbs, AdvancedEvaluator evaluator,
        ReportGate gate, IOptionsMonitor<AdvancedOptions> options, CancellationToken ct) =>
        HandleAsync(http, token, dbs, evaluator, gate, options, ct);

    private static IResult Answer(int status, ReportAnswer answer) => Results.Json(answer, Json, statusCode: status);

    private static async Task<IResult> HandleAsync(HttpContext http, string token, IDbContextFactory<PnmtdDbContext> dbs, AdvancedEvaluator evaluator,
        ReportGate gate, IOptionsMonitor<AdvancedOptions> options, CancellationToken ct)
    {
        var o = options.CurrentValue;
        await using var db = await dbs.CreateDbContextAsync(ct);
        var sensor = await db.Sensors.AsNoTracking().Where(s => s.SecretWriteToken == token && s.Enabled).OrderBy(s => s.Created)
            .Select(s => new { s.Id, s.Type, s.Interval, s.GracePeriod }).FirstOrDefaultAsync(ct);
        if (sensor is null)
        {
            // As on /event: nothing says whether the token exists.
            return Results.NotFound();
        }
        if (sensor.Type != SensorType.ADVANCED)
        {
            return Answer(StatusCodes.Status409Conflict, new ReportAnswer(false, Error: "wrong_type",
                Message: $"This sensor is a {sensor.Type}; reports go to Advanced sensors. Its URL is /event/<token>/<code>."));
        }
        var dryRun = http.Request.Query.TryGetValue("dryRun", out var flag) && (flag == "true" || flag == "1");
        if (!gate.TryEnter(sensor.Id, o.MinBetween, out var wait))
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return Answer(StatusCodes.Status429TooManyRequests, new ReportAnswer(false, Error: "too_soon",
                Message: $"At most one report every {o.MinBetween.TotalSeconds:0} seconds per sensor: try again in {seconds} s."));
        }

        using var report = await ReadAsync(http, o.MaxBytes, ct);
        var source = Source(http);
        var now = Clock.UtcNow;
        var preview = await PreviewAsync(db, sensor.Id, report, now, TimeSpan.FromSeconds(Math.Max(0, sensor.Interval)),
            TimeSpan.FromSeconds(Math.Max(0, sensor.GracePeriod)), ct);
        if (dryRun)
        {
            return report.Valid
                ? Answer(StatusCodes.Status200OK, new ReportAnswer(true, Preview: preview, DryRun: true))
                : Answer(report.Kind == AdvancedReportError.TooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status422UnprocessableEntity,
                    new ReportAnswer(false, Error: ErrorCode(report.Kind), Message: report.Error, DryRun: true));
        }
        var id = await AdvancedReports.StoreAsync(db, sensor.Id, report, source, now, o.Keep, ct);
        evaluator.Nudge();
        if (report.Valid)
        {
            return Answer(StatusCodes.Status202Accepted, new ReportAnswer(true, id, preview));
        }
        return Answer(report.Kind == AdvancedReportError.TooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status422UnprocessableEntity,
            new ReportAnswer(false, id, Error: ErrorCode(report.Kind), Message: report.Error));
    }

    private static string ErrorCode(AdvancedReportError? kind) => kind switch
    {
        AdvancedReportError.TooLarge => "too_large",
        AdvancedReportError.NotObject => "not_an_object",
        AdvancedReportError.TooDeep => "too_deep",
        AdvancedReportError.DuplicateKey => "duplicate_key",
        _ => "invalid_json",
    };

    /// <summary>
    /// The body within the size limit, counted after decompression (§4.2): refused by <c>Content-Length</c> before reading,
    /// and while reading for a chunked or compressed one. Read as JSON whatever the Content-Type says.
    /// </summary>
    private static async Task<AdvancedParsedReport> ReadAsync(HttpContext http, int maxBytes, CancellationToken ct)
    {
        var compressed = http.Request.Headers.ContentEncoding.Count > 0;
        if (!compressed && http.Request.ContentLength is { } announced && announced > maxBytes)
        {
            return AdvancedReportReader.TooLarge(announced, maxBytes);
        }
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            // Kestrel's own ceiling, above ours: the counting below decides, also for a decompressed body.
            limit.MaxRequestBodySize = compressed ? null : maxBytes + 1L;
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
                    return AdvancedReportReader.TooLarge(compressed ? null : http.Request.ContentLength, maxBytes);
                }
                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return AdvancedReportReader.TooLarge(http.Request.ContentLength, maxBytes);
        }
        catch (InvalidDataException)
        {
            return new AdvancedParsedReport
            {
                Valid = false, Kind = AdvancedReportError.NotJson, Error = "The report isn't valid gzip", Bytes = (int)buffer.Length,
            };
        }
        return AdvancedReportReader.Read(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>The client's address, after the forwarded headers (the Proxy setting); an IPv4 address without its ::ffff: prefix.</summary>
    private static string? Source(HttpContext http)
    {
        var address = http.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        return address.Equals(IPAddress.IPv6Loopback) ? "::1" : address.ToString();
    }

    /// <summary>The verdict the same pure function gives this report now (§4.3), against the newest stored one; writes nothing.</summary>
    private static async Task<ReportPreview?> PreviewAsync(PnmtdDbContext db, Guid sensorId, AdvancedParsedReport report, DateTime now,
        TimeSpan interval, TimeSpan grace, CancellationToken ct)
    {
        var rules = (await db.AdvancedRules.AsNoTracking().Where(r => r.SensorId == sensorId).OrderBy(r => r.Order).ThenBy(r => r.Created).ToListAsync(ct))
            .Select(r => r.ToSpec()).ToList();
        JsonDocument? previous = null;
        try
        {
            if (report.Valid && rules.Any(r => r.Enabled && AdvancedRules.IsChange(r.Operator)))
            {
                var latest = await db.AdvancedReports.AsNoTracking().Where(r => r.SensorId == sensorId && r.Valid)
                    .OrderByDescending(r => r.LastReceivedAt).Select(r => new { r.Sha256, r.Body }).FirstOrDefaultAsync(ct);
                previous = AdvancedReportReader.Parse(latest?.Body);
            }
            AdvancedVerdict? verdict;
            try
            {
                verdict = AdvancedVerdict.Evaluate(rules, report.Input, previous?.RootElement, now, now, interval, grace);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The preview is a courtesy: the evaluator decides, and deals with what can't be evaluated (§7.3, Errors).
                return null;
            }
            return verdict is null
                ? null
                : new ReportPreview(verdict.Code, verdict.State, verdict.Message,
                    verdict.Results.Where(r => r.Outcome == AdvancedOutcome.Fail).Select(r => r.Name).ToList(),
                    verdict.Results.Where(r => r.Outcome == AdvancedOutcome.Warn).Select(r => r.Name).ToList());
        }
        finally
        {
            previous?.Dispose();
        }
    }
}

/// <summary>Storing a report (§7.1): one transaction under the sensor's advisory lock; a repeat folds into the latest row.</summary>
public static class AdvancedReports
{
    public static async Task<Guid> StoreAsync(PnmtdDbContext db, Guid sensorId, AdvancedParsedReport parsed, string? source, DateTime now, int keep,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await AdvancedEvaluator.LockAsync(db, sensorId, ct);
        var side = await db.AdvancedSensors.SingleOrDefaultAsync(a => a.SensorId == sensorId, ct);
        if (side is null)
        {
            side = new AdvancedSensorEntity { SensorId = sensorId, Created = now };
            db.AdvancedSensors.Add(side);
        }
        var latest = side.LastReportId is { } lastId ? await db.AdvancedReports.SingleOrDefaultAsync(r => r.Id == lastId, ct) : null;
        source = source is { Length: > AdvancedLimits.MaxSource } ? source[..AdvancedLimits.MaxSource] : source;
        AdvancedReportEntity report;
        if (parsed.Valid && latest is { Valid: true } && latest.Sha256 == parsed.Sha256 && latest.Bytes == parsed.Bytes)
        {
            // The same report again (§7.2): one row with a count, judged again, as ages move and changes compare.
            latest.LastReceivedAt = now;
            latest.Repeats++;
            latest.Source = source;
            latest.EvaluatedAt = null;
            latest.EvaluationAttempts = 0;
            report = latest;
        }
        else
        {
            report = new AdvancedReportEntity
            {
                Id = Guid.NewGuid(),
                SensorId = sensorId,
                ReceivedAt = now,
                LastReceivedAt = now,
                Source = source,
                Bytes = parsed.Bytes,
                Sha256 = parsed.Sha256,
                Body = parsed.Kind == AdvancedReportError.TooLarge ? null : parsed.Text,
                Valid = parsed.Valid,
                ErrorKind = parsed.Kind,
                Error = parsed.Error,
                Status = parsed.Status,
                Message = parsed.Message,
                MeasuredAt = parsed.MeasuredAt,
            };
            db.AdvancedReports.Add(report);
        }
        side.LastReportId = report.Id;
        side.LastReportAt = now;
        if (parsed.Valid)
        {
            side.LastValidReportAt = now;
        }
        side.LastSource = source;
        await db.SaveChangesAsync(ct);
        await AdvancedEvaluator.TrimAsync(db, sensorId, keep, ct);
        await transaction.CommitAsync(ct);
        return report.Id;
    }
}
