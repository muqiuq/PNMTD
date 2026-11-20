using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Outbound;

namespace PNMTD.Redmine;

public enum RedmineFailure { NotConfigured, Unreachable, Unauthorized, Forbidden, NotFound, Invalid }

/// <summary>
/// Why a call to Redmine failed, in words for people; never the key or a URL with it. <see cref="HttpStatus"/> and
/// <see cref="RetryAfter"/> when Redmine answered, so the worker can hold back after a 429 or 503
/// (docs/redmine-queue-outbound-plan.md §3.4).
/// </summary>
public sealed class RedmineException(RedmineFailure failure, string message, int? httpStatus = null, TimeSpan? retryAfter = null) : Exception(message)
{
    public RedmineFailure Failure { get; } = failure;

    public int? HttpStatus { get; } = httpStatus;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>What a call is for, for the Redmine log (§6.2): the job, its lane, how long the call waited for a slot.</summary>
public sealed record RedmineCallInfo(Guid JobId, RedmineLane Lane, int QueuedMs, string Purpose, string AskedBy, string Summary);

/// <summary>
/// Redmine's REST API (docs/audit-redmine-zabbix-plan.md §6.2): reads, and Cookie's writes. The key goes in the
/// X-Redmine-API-Key header, never in a URL; answers are capped at 5 MiB; a timeout, a refused key and a missing ticket each
/// have their failure. <b>Only the worker calls it</b> (docs/redmine-queue-outbound-plan.md §3.1): its methods are internal,
/// and <c>RedmineWorkerTests</c> checks that nothing else uses the type. Every call goes into the Redmine log (§6.3).
/// </summary>
public sealed class RedmineClient(IHttpClientFactory http, IOptionsMonitor<RedmineOptions> options, OutboundLog outbound)
{
    public const string HttpClientName = "redmine";

    public const long MaxBytes = 5 * 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private OutboundLog Outbound => outbound;

    private RedmineOptions Options => options.CurrentValue;

    /// <summary>
    /// A read. With <paramref name="asWriter"/>, with Cookie's write key instead of the reading key: only to ask Redmine whose key it
    /// is (<c>/users/current.json</c>), so that Cookie's own notes can be told apart.
    /// </summary>
    internal async Task<T> GetAsync<T>(string pathAndQuery, CancellationToken ct, bool asWriter = false, RedmineCallInfo? info = null)
    {
        var o = options.CurrentValue;
        if (!o.Enabled)
        {
            throw new RedmineException(RedmineFailure.NotConfigured, "Redmine is not configured (Redmine__Url, Redmine__ApiKey, Redmine__Projects).");
        }
        if (asWriter && !o.CanWrite)
        {
            throw new RedmineException(RedmineFailure.NotConfigured, "Reading as Cookie needs Redmine__WriteApiKey.");
        }
        using var client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(o.Url!.TrimEnd('/') + pathAndQuery));
        request.Headers.Add("X-Redmine-API-Key", asWriter ? o.WriteApiKey : o.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        var call = Begin(request, RedmineCallKind.Read, info);
        string? text = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 2, 120)));
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RedmineException(RedmineFailure.Unreachable, $"Redmine didn't answer within {o.TimeoutSeconds} seconds.");
            }
            catch (HttpRequestException e)
            {
                throw new RedmineException(RedmineFailure.Unreachable, $"Redmine doesn't answer: {e.Message}");
            }
            using (response)
            {
                call.Answered(response);
                if (response.Content.Headers.ContentLength > MaxBytes)
                {
                    throw new RedmineException(RedmineFailure.Invalid, "Redmine's answer is too large.");
                }
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var limited = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (limited.Length + read > MaxBytes)
                    {
                        throw new RedmineException(RedmineFailure.Invalid, "Redmine's answer is too large.");
                    }
                    limited.Write(buffer, 0, read);
                }
                text = Encoding.UTF8.GetString(limited.GetBuffer(), 0, (int)limited.Length);
                Check(response, asWriter);
                limited.Position = 0;
                try
                {
                    var result = await JsonSerializer.DeserializeAsync<T>(limited, Json, timeout.Token)
                        ?? throw new RedmineException(RedmineFailure.Invalid, "Redmine's answer is empty.");
                    call.Done(null, text, null);
                    return result;
                }
                catch (JsonException)
                {
                    throw new RedmineException(RedmineFailure.Invalid, "Redmine's answer is not what PNMT expects (is Redmine__Url Redmine's address?).");
                }
            }
        }
        catch (Exception e)
        {
            call.Done(null, text, e, ct.IsCancellationRequested);
            throw;
        }
    }

    /// <summary>
    /// A write with Redmine__WriteApiKey (docs/cookie-plan.md §19.1), never the reading key: POST or PUT with a JSON body. The
    /// answer's JSON (a created ticket), or null (204). Redmine's own messages on a 422 come back in the exception.
    /// </summary>
    internal async Task<JsonDocument?> WriteAsync(HttpMethod method, string path, object body, CancellationToken ct, RedmineCallInfo? info = null)
    {
        var o = options.CurrentValue;
        if (!o.CanWrite)
        {
            throw new RedmineException(RedmineFailure.NotConfigured, "Writing to Redmine needs Redmine__WriteApiKey.");
        }
        using var client = http.CreateClient(HttpClientName);
        var json = JsonSerializer.Serialize(body, Json);
        using var request = new HttpRequestMessage(method, new Uri(o.Url!.TrimEnd('/') + path))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Redmine-API-Key", o.WriteApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        var call = Begin(request, RedmineCallKind.Write, info);
        string? text = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 2, 120)));
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RedmineException(RedmineFailure.Unreachable, $"Redmine didn't answer within {o.TimeoutSeconds} seconds.");
            }
            catch (HttpRequestException e)
            {
                throw new RedmineException(RedmineFailure.Unreachable, $"Redmine doesn't answer: {e.Message}");
            }
            using (response)
            {
                call.Answered(response);
                text = await response.Content.ReadAsStringAsync(timeout.Token);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.Unauthorized:
                        throw new RedmineException(RedmineFailure.Unauthorized, "Redmine refused the write key (Redmine__WriteApiKey).", 401);
                    case HttpStatusCode.Forbidden:
                        throw new RedmineException(RedmineFailure.Forbidden, "Redmine's account for Cookie may not write this.", 403);
                    case HttpStatusCode.NotFound:
                        throw new RedmineException(RedmineFailure.NotFound, "Not found in Redmine.", 404);
                    case HttpStatusCode.UnprocessableEntity:
                        throw new RedmineException(RedmineFailure.Invalid, $"Redmine refused it: {Errors(text)}", 422);
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw new RedmineException(RedmineFailure.Unreachable, $"Redmine answered {(int)response.StatusCode}.", (int)response.StatusCode,
                        response.Headers.RetryAfter?.Delta);
                }
                call.Done(json, text, null);
                return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
            }
        }
        catch (Exception e)
        {
            call.Done(json, text, e, ct.IsCancellationRequested);
            throw;
        }
    }

    private static void Check(HttpResponseMessage response, bool asWriter)
    {
        var status = (int)response.StatusCode;
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new RedmineException(RedmineFailure.Unauthorized, asWriter ? "Redmine refused the write key (Redmine__WriteApiKey)."
                    : "Redmine refused the API key (Redmine__ApiKey), or its REST API is off.", status);
            case HttpStatusCode.Forbidden:
                throw new RedmineException(RedmineFailure.Forbidden, "Redmine's account for PNMT may not read this.", status);
            case HttpStatusCode.NotFound:
                throw new RedmineException(RedmineFailure.NotFound, "Not found in Redmine.", status);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new RedmineException(RedmineFailure.Unreachable, $"Redmine answered {status}.", status, response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null));
        }
    }

    private static string Errors(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                return string.Join("; ", errors.EnumerateArray().Select(e => e.ToString()));
            }
        }
        catch (JsonException)
        {
        }
        return body.Length > 200 ? body[..200] : body;
    }

    private Call Begin(HttpRequestMessage request, RedmineCallKind kind, RedmineCallInfo? info) => new(this, request, kind, info);

    /// <summary>One call's record for the Redmine log, filled as it goes.</summary>
    private sealed class Call
    {
        private readonly RedmineClient owner;
        private readonly RedmineCallEntity entry;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly long running;
        private bool done;

        public Call(RedmineClient owner, HttpRequestMessage request, RedmineCallKind kind, RedmineCallInfo? info)
        {
            this.owner = owner;
            entry = new RedmineCallEntity
            {
                StartedAt = Clock.UtcNow, Kind = kind, Method = request.Method.Method, Target = request.RequestUri?.ToString() ?? "",
                Summary = info?.Summary ?? "", Purpose = info?.Purpose ?? "", AskedBy = info?.AskedBy ?? "PNMT", JobId = info?.JobId,
                Lane = info?.Lane ?? RedmineLane.Interactive, QueuedMs = info?.QueuedMs ?? 0,
                RequestHeaders = OutboundRedactor.Json(OutboundRedactor.Headers(request.Headers, request.Content?.Headers)),
            };
            running = owner.Outbound.Begin(OutboundLog.RedmineLog, "Redmine", entry.Method, entry.Target, entry.Purpose, entry.AskedBy);
        }

        public void Answered(HttpResponseMessage response)
        {
            entry.StatusCode = (int)response.StatusCode;
            entry.ResponseHeaders = OutboundRedactor.Json(OutboundRedactor.Headers(response.Headers, response.Content.Headers));
        }

        public void Done(string? request, string? response, Exception? failure, bool cancelled = false)
        {
            if (done)
            {
                return;
            }
            done = true;
            owner.Outbound.End(running);
            entry.DurationMs = (int)watch.ElapsedMilliseconds;
            entry.Outcome = failure is null ? OutboundOutcome.Ok : cancelled ? OutboundOutcome.Cancelled : OutboundOutcome.Failed;
            entry.Error = failure?.Message;
            if (!owner.Options.ShowPrivateNotes)
            {
                response = OutboundRedactor.WithoutPrivateNotes(response);
            }
            owner.Outbound.Record(entry, request, response);
        }
    }

    /// <summary>
    /// The handler: the system's certificate validation, and with Redmine__CertificateSha256 also that certificate (its
    /// SHA-256, colons and case ignored), for a server with a certificate of its own.
    /// </summary>
    public static HttpMessageHandler Handler(string? pinnedSha256)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var pin = Normalize(pinnedSha256);
        if (pin is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None
                || (certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).Equals(pin, StringComparison.OrdinalIgnoreCase));
        }
        return handler;
    }

    internal static string? Normalize(string? sha256) =>
        string.IsNullOrWhiteSpace(sha256) ? null : sha256.Replace(":", "").Replace(" ", "").Trim();
}
