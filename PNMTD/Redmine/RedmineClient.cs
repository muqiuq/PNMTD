using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PNMTD.Redmine;

public enum RedmineFailure { NotConfigured, Unreachable, Unauthorized, Forbidden, NotFound, Invalid }

/// <summary>Why a call to Redmine failed, in words for people; never the key or a URL with it.</summary>
public sealed class RedmineException(RedmineFailure failure, string message) : Exception(message)
{
    public RedmineFailure Failure { get; } = failure;
}

/// <summary>
/// Redmine's REST API (docs/audit-redmine-zabbix-plan.md §6.2): GET only. The key goes in the X-Redmine-API-Key header,
/// never in a URL; answers are capped at 5 MiB; a timeout, a refused key and a missing ticket each have their failure.
/// </summary>
public sealed class RedmineClient(IHttpClientFactory http, IOptionsMonitor<RedmineOptions> options)
{
    public const string HttpClientName = "redmine";

    public const long MaxBytes = 5 * 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A read. With <paramref name="asWriter"/>, with Cookie's write key instead of the reading key: only to ask Redmine whose key it
    /// is (<c>/users/current.json</c>), so that Cookie's own notes can be told apart.
    /// </summary>
    public async Task<T> GetAsync<T>(string pathAndQuery, CancellationToken ct, bool asWriter = false)
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 2, 120)));
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
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new RedmineException(RedmineFailure.Unauthorized, asWriter ? "Redmine refused the write key (Redmine__WriteApiKey)."
                        : "Redmine refused the API key (Redmine__ApiKey), or its REST API is off.");
                case HttpStatusCode.Forbidden:
                    throw new RedmineException(RedmineFailure.Forbidden, "Redmine's account for PNMT may not read this.");
                case HttpStatusCode.NotFound:
                    throw new RedmineException(RedmineFailure.NotFound, "Not found in Redmine.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new RedmineException(RedmineFailure.Unreachable, $"Redmine answered {(int)response.StatusCode}.");
            }
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
            limited.Position = 0;
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(limited, Json, timeout.Token)
                    ?? throw new RedmineException(RedmineFailure.Invalid, "Redmine's answer is empty.");
            }
            catch (JsonException)
            {
                throw new RedmineException(RedmineFailure.Invalid, "Redmine's answer is not what PNMT expects (is Redmine__Url Redmine's address?).");
            }
        }
    }

    /// <summary>
    /// A write with Redmine__WriteApiKey (docs/cookie-plan.md §19.1), never the reading key: POST or PUT with a JSON body. The
    /// answer's JSON (a created ticket), or null (204). Redmine's own messages on a 422 come back in the exception.
    /// </summary>
    public async Task<JsonDocument?> WriteAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.CanWrite)
        {
            throw new RedmineException(RedmineFailure.NotConfigured, "Writing to Redmine needs Redmine__WriteApiKey.");
        }
        using var client = http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(o.Url!.TrimEnd('/') + path))
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Redmine-API-Key", o.WriteApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 2, 120)));
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
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new RedmineException(RedmineFailure.Unauthorized, "Redmine refused the write key (Redmine__WriteApiKey).");
                case HttpStatusCode.Forbidden:
                    throw new RedmineException(RedmineFailure.Forbidden, "Redmine's account for Cookie may not write this.");
                case HttpStatusCode.NotFound:
                    throw new RedmineException(RedmineFailure.NotFound, "Not found in Redmine.");
                case HttpStatusCode.UnprocessableEntity:
                    throw new RedmineException(RedmineFailure.Invalid, $"Redmine refused it: {Errors(text)}");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new RedmineException(RedmineFailure.Unreachable, $"Redmine answered {(int)response.StatusCode}.");
            }
            return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
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
