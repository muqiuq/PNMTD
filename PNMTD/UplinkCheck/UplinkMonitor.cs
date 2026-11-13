using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Services.Domain;
using PNMTD.Services.Events;

namespace PNMTD.UplinkCheck;

/// <summary>An uplink endpoint: <c>UplinkCheck:Url1..3</c>.</summary>
public sealed record UplinkEndpoint(short Number, string Url, string Host);

/// <summary>
/// PNMTD's own uplink (docs/ai-operations-plan.md §7): every 120 s the next endpoint answers a challenge with
/// <c>HMAC-SHA256(key, challenge-random),random</c>. Three failures in a row set <c>UPLINK_OK</c> false, which pauses
/// the monitoring tasks (<c>TaskRunDecisionMaker</c>), and write 420 on the Uplink system sensor; the next success
/// sets it true again and writes 220. Every check is kept 30 days, and the state survives a restart. One check at a
/// time: the task's and <c>POST /api/v1/uplink/check</c>'s.
/// </summary>
public sealed class UplinkMonitor(IDbContextFactory<PnmtdDbContext> dbs, IConfiguration configuration, IHttpClientFactory http,
    SystemSensors systemSensors, ILogger<UplinkMonitor> logger)
{
    public const int FailuresForDown = 3;
    public const int IntervalSeconds = 120;
    public const int KeepDays = 30;
    public const string HttpClientName = "uplink";

    private readonly SemaphoreSlim gate = new(1, 1);
    private int position = -1;
    private bool loaded;
    private int failuresInARow;
    private DateTime? downSince;
    private readonly List<UplinkOutcome> failures = [];
    private DateTime lastCleanup = DateTime.MinValue;

    public bool Configured => SystemService.UplinkConfigured(configuration);

    public bool SharedKeySet => !string.IsNullOrEmpty(configuration["UplinkCheck:SharedKey"]);

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("UplinkCheck:TimeoutSeconds", 15), 1, 110));

    public IReadOnlyList<UplinkEndpoint> Endpoints =>
        Enumerable.Range(1, 3)
            .Select(i => (Number: (short)i, Url: configuration[$"UplinkCheck:Url{i}"]))
            .Where(e => e.Url is { } url && (url.StartsWith("http://", StringComparison.Ordinal) || url.StartsWith("https://", StringComparison.Ordinal)))
            .Select(e => new UplinkEndpoint(e.Number, e.Url!, Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) ? uri.Host : e.Url!))
            .ToList();

    /// <summary>Failures in a row now, and since when the uplink is down (null: up).</summary>
    public (int FailuresInARow, DateTime? DownSince) State => (failuresInARow, downSince);

    /// <summary>
    /// Reads the state of before a restart. Without the settings the uplink counts as up, so that monitoring never
    /// stays paused by a check that no longer runs.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await LoadLockedAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task LoadLockedAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        if (!Configured)
        {
            db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_OK, true);
            db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_FAILURES_IN_A_ROW, 0);
            db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_DOWN_SINCE, "");
            failuresInARow = 0;
            downSince = null;
        }
        else
        {
            failuresInARow = db.GetKeyValueByEnum<int>(KeyValueKeyEnums.UPLINK_FAILURES_IN_A_ROW);
            downSince = DateTime.TryParse(db.GetKeyValueByEnum<string>(KeyValueKeyEnums.UPLINK_DOWN_SINCE), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var since) ? DateTime.SpecifyKind(since, DateTimeKind.Utc) : null;
            db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_OK, downSince is null);
        }
        loaded = true;
    }

    /// <summary>One check of the next endpoint: recorded, and the state and sensor updated. Null when not configured.</summary>
    public async Task<UplinkCheckEntity?> CheckAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!loaded)
            {
                await LoadLockedAsync(ct);
            }
            var endpoints = Endpoints;
            if (!Configured || endpoints.Count == 0)
            {
                return null;
            }
            position = (position + 1) % endpoints.Count;
            var endpoint = endpoints[position];
            var check = await ProbeAsync(endpoint, ct);
            await RecordAsync(check, ct);
            return check;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<UplinkCheckEntity> ProbeAsync(UplinkEndpoint endpoint, CancellationToken ct)
    {
        var check = new UplinkCheckEntity { At = Clock.UtcNow, Endpoint = endpoint.Number, EndpointHost = Truncate(endpoint.Host, 255)! };
        var challenge = Challenge();
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var response = await http.CreateClient(HttpClientName).GetAsync(QueryHelpers.AddQueryString(endpoint.Url, "challenge", challenge), timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            check.LatencyMs = (int)watch.ElapsedMilliseconds;
            check.HttpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                check.Outcome = UplinkOutcome.HttpStatus;
                check.Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                return check;
            }
            var parts = body.Trim().Split(',');
            if (parts.Length != 2)
            {
                check.Outcome = UplinkOutcome.BadAnswer;
                check.Error = Truncate($"Not \"hash,random\": {body.Trim()}", 500);
                return check;
            }
            var expected = Hmac(challenge, configuration["UplinkCheck:SharedKey"] ?? "", parts[1]);
            if (!string.Equals(expected, parts[0], StringComparison.OrdinalIgnoreCase))
            {
                check.Outcome = UplinkOutcome.BadSignature;
                check.Error = "The answer's HMAC does not match the shared key.";
                return check;
            }
            check.Outcome = UplinkOutcome.Ok;
            return check;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            check.Outcome = UplinkOutcome.Timeout;
            check.Error = $"No answer within {Timeout.TotalSeconds:0} s.";
        }
        catch (HttpRequestException ex)
        {
            check.Outcome = Classify(ex);
            check.Error = Truncate(ex.Message, 500);
        }
        check.LatencyMs = (int)watch.ElapsedMilliseconds;
        return check;
    }

    internal static UplinkOutcome Classify(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => UplinkOutcome.Dns,
        HttpRequestError.SecureConnectionError => UplinkOutcome.Tls,
        HttpRequestError.ConnectionError => UplinkOutcome.ConnectError,
        _ when ex.InnerException is AuthenticationException => UplinkOutcome.Tls,
        _ when ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } => UplinkOutcome.Dns,
        _ => UplinkOutcome.ConnectError,
    };

    private async Task RecordAsync(UplinkCheckEntity check, CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.UplinkChecks.Add(check);
        await db.SaveChangesAsync(ct);
        db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_UPLINK_CHECK);
        db.SetKeyValueEntryByEnum(KeyValueKeyEnums.LAST_UPLINK_CHECKED_LINK, Endpoints.FirstOrDefault(e => e.Number == check.Endpoint)?.Url ?? check.EndpointHost);
        string? up = null;
        string? down = null;
        if (check.Outcome == UplinkOutcome.Ok)
        {
            db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_UPLINK_CHECK_SUCCESSFULL);
            if (downSince is { } since)
            {
                up = $"Uplink up again after {Duration(Clock.UtcNow - since)}.";
                logger.LogWarning("Uplink OK again after {Duration}", Duration(Clock.UtcNow - since));
            }
            failuresInARow = 0;
            failures.Clear();
            downSince = null;
            db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_OK, true);
        }
        else
        {
            failuresInARow++;
            failures.Add(check.Outcome);
            if (failures.Count > FailuresForDown)
            {
                failures.RemoveAt(0);
            }
            if (failuresInARow >= FailuresForDown && downSince is null)
            {
                // Down since the first failure of the run.
                downSince = await db.UplinkChecks.Where(c => c.Outcome == UplinkOutcome.Ok && c.At < check.At).OrderByDescending(c => c.At)
                    .Select(c => (DateTime?)c.At).FirstOrDefaultAsync(ct) is { } lastOk
                    ? await db.UplinkChecks.Where(c => c.At > lastOk).OrderBy(c => c.At).Select(c => c.At).FirstAsync(ct)
                    : check.At;
                db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_OK, false);
                down = $"Uplink down: {failuresInARow} checks failed in a row ({string.Join(", ", failures.Select(Label))}).";
                logger.LogWarning("Uplink failed {Count} times in a row: UPLINK_OK is now false", failuresInARow);
            }
        }
        db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_FAILURES_IN_A_ROW, failuresInARow);
        db.SetKeyValueEntryByEnum(KeyValueKeyEnums.UPLINK_DOWN_SINCE, downSince?.ToString("o") ?? "");
        if (Clock.UtcNow - lastCleanup > TimeSpan.FromHours(1))
        {
            lastCleanup = Clock.UtcNow;
            var cutoff = Clock.UtcNow.AddDays(-KeepDays);
            await db.UplinkChecks.Where(c => c.At < cutoff).ExecuteDeleteAsync(ct);
        }
        try
        {
            if (down is not null)
            {
                await systemSensors.WriteAsync(SystemSensorKeys.Uplink, PNMTStatusCodes.UPLINK_DOWN, down, ct);
            }
            else if (up is not null)
            {
                await systemSensors.WriteAsync(SystemSensorKeys.Uplink, PNMTStatusCodes.SYSTEM_OK, up, ct);
            }
            else if (check.Outcome == UplinkOutcome.Ok)
            {
                await systemSensors.WriteAsync(SystemSensorKeys.Uplink, PNMTStatusCodes.SYSTEM_OK, "Uplink OK.", ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The Uplink system sensor could not be written");
        }
    }

    public static string Label(UplinkOutcome outcome) => outcome switch
    {
        UplinkOutcome.Ok => "ok",
        UplinkOutcome.HttpStatus => "HTTP error",
        UplinkOutcome.Timeout => "timeout",
        UplinkOutcome.ConnectError => "no connection",
        UplinkOutcome.Dns => "DNS",
        UplinkOutcome.Tls => "TLS",
        UplinkOutcome.BadAnswer => "bad answer",
        UplinkOutcome.BadSignature => "wrong signature",
        _ => outcome.ToString(),
    };

    public static string Duration(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} s"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : $"{(int)span.TotalDays} d {span.Hours} h";

    /// <summary>As v0.12's check: 32 characters of A–Z and 0–9, and the HMAC over "challenge-random".</summary>
    private static string Challenge()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return string.Create(32, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }

    public static string Hmac(string message, string secret, string randomPart)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(message + "-" + randomPart)));
    }

    private static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}
