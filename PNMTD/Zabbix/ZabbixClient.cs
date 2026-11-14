using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PNMTD.Redmine;

namespace PNMTD.Zabbix;

public enum ZabbixFailure { NotConfigured, Unreachable, Unauthorized, Forbidden, Invalid }

/// <summary>Why a call to Zabbix failed, in words for people; never the token.</summary>
public sealed class ZabbixException(ZabbixFailure failure, string message) : Exception(message)
{
    public ZabbixFailure Failure { get; } = failure;
}

/// <summary>
/// Zabbix's JSON-RPC API (docs/audit-redmine-zabbix-plan.md §7.2), reads only. First <c>apiinfo.version</c>, without the
/// token (6.0 and 7.0 refuse it with one), at start and after every error; the version decides where the token goes: the
/// <c>Authorization: Bearer</c> header from 6.4 (8.0 accepts nothing else), the request's <c>auth</c> field before (6.0
/// doesn't take the header). Never in a URL. Answers are capped at 20 MiB, calls at the timeout.
/// </summary>
public sealed class ZabbixClient(IHttpClientFactory http, IOptionsMonitor<ZabbixOptions> options, ILogger<ZabbixClient> logger)
{
    public const string HttpClientName = "zabbix";

    public const long MaxBytes = 20 * 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private volatile ZabbixVersion? version;

    /// <summary>The version last read, if any: for the status and the links.</summary>
    public ZabbixVersion? KnownVersion => version;

    /// <summary>Read the version again with the next call (the test, after an upgrade of Zabbix).</summary>
    public void ForgetVersion() => version = null;

    public async Task<ZabbixVersion> VersionAsync(CancellationToken ct)
    {
        if (version is { } known)
        {
            return known;
        }
        var text = await SendAsync<string>("apiinfo.version", new Dictionary<string, object?>(), null, ct);
        var parsed = ZabbixVersion.Parse(text) ?? throw new ZabbixException(ZabbixFailure.Invalid, $"Zabbix answered an unknown version: {text}.");
        if (parsed < ZabbixVersion.Minimum)
        {
            throw new ZabbixException(ZabbixFailure.Invalid, $"Zabbix {text} is too old: PNMT needs 6.0 or later.");
        }
        version = parsed;
        return parsed;
    }

    /// <summary>A method of the API with the token where this version wants it.</summary>
    public async Task<T> CallAsync<T>(string method, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        var v = await VersionAsync(ct);
        try
        {
            return await SendAsync<T>(method, parameters, v, ct);
        }
        catch (ZabbixException)
        {
            // The version is read again with the next call: Zabbix may have been upgraded meanwhile.
            version = null;
            throw;
        }
    }

    private async Task<T> SendAsync<T>(string method, IReadOnlyDictionary<string, object?> parameters, ZabbixVersion? v, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (!o.Enabled)
        {
            throw new ZabbixException(ZabbixFailure.NotConfigured, "Zabbix is not configured (Zabbix__Url, Zabbix__ApiToken).");
        }
        var body = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters, ["id"] = 1 };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(o.Url!.TrimEnd('/') + "/api_jsonrpc.php"));
        if (v is { } known)
        {
            if (known >= ZabbixVersion.BearerFrom)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiToken);
            }
            else
            {
                body["auth"] = o.ApiToken;
            }
        }
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json-rpc");
        using var client = http.CreateClient(HttpClientName);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 2, 120)));
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ZabbixException(ZabbixFailure.Unreachable, $"Zabbix didn't answer within {o.TimeoutSeconds} seconds.");
        }
        catch (HttpRequestException e)
        {
            throw new ZabbixException(ZabbixFailure.Unreachable, $"Zabbix doesn't answer: {e.Message}");
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ZabbixException(ZabbixFailure.Unauthorized, "Zabbix's web server refused PNMT.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new ZabbixException(ZabbixFailure.Unreachable, $"Zabbix answered {(int)response.StatusCode} (is Zabbix__Url the frontend's address?).");
            }
            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new ZabbixException(ZabbixFailure.Invalid, "Zabbix's answer is too large.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var limited = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (limited.Length + read > MaxBytes)
                {
                    throw new ZabbixException(ZabbixFailure.Invalid, "Zabbix's answer is too large.");
                }
                limited.Write(buffer, 0, read);
            }
            limited.Position = 0;
            ZxResponse<T>? answer;
            try
            {
                answer = await JsonSerializer.DeserializeAsync<ZxResponse<T>>(limited, Json, timeout.Token);
            }
            catch (JsonException e)
            {
                logger.LogDebug(e, "Zabbix's answer to {Method} isn't JSON-RPC", method);
                throw new ZabbixException(ZabbixFailure.Invalid, "Zabbix's answer is not what PNMT expects (is Zabbix__Url Zabbix's frontend?).");
            }
            if (answer?.Error is { } error)
            {
                throw Failed(method, error);
            }
            return answer is { Result: { } result } ? result
                : throw new ZabbixException(ZabbixFailure.Invalid, "Zabbix's answer is empty.");
        }
    }

    /// <summary>Zabbix's error, as Z0 recorded them: "Not authorized." for a bad token, "No permissions to call …" outside the role's list.</summary>
    private static ZabbixException Failed(string method, ZxError error)
    {
        var data = error.Data ?? error.Message;
        if (data.Contains("Not authorized", StringComparison.OrdinalIgnoreCase) || data.Contains("Session terminated", StringComparison.OrdinalIgnoreCase)
            || data.Contains("API token expired", StringComparison.OrdinalIgnoreCase))
        {
            return new ZabbixException(ZabbixFailure.Unauthorized, "Zabbix refused the API token (Zabbix__ApiToken): unknown, expired or disabled.");
        }
        if (data.Contains("No permissions", StringComparison.OrdinalIgnoreCase))
        {
            return new ZabbixException(ZabbixFailure.Forbidden, $"Zabbix's role for PNMT doesn't allow {method} (deploy/README.md, \"Zabbix\").");
        }
        return new ZabbixException(ZabbixFailure.Invalid, $"Zabbix: {data}");
    }

    /// <summary>The handler: the system's validation, and with Zabbix__CertificateSha256 that certificate too (as Redmine's).</summary>
    public static HttpMessageHandler Handler(string? pinnedSha256) => RedmineClient.Handler(pinnedSha256);
}

/// <summary>Zabbix's version, major.minor.patch.</summary>
public sealed record ZabbixVersion(int Major, int Minor, int Patch) : IComparable<ZabbixVersion>
{
    public static readonly ZabbixVersion Minimum = new(6, 0, 0);

    /// <summary>The token in the Authorization header (6.4 on).</summary>
    public static readonly ZabbixVersion BearerFrom = new(6, 4, 0);

    /// <summary>selectHostGroups (6.2 on); before, selectGroups.</summary>
    public static readonly ZabbixVersion HostGroupsFrom = new(6, 2, 0);

    /// <summary>The host form only as a popup (Z0: 8.0 answers host.edit with its JSON); 7.0 still has the page.</summary>
    public static readonly ZabbixVersion HostPopupFrom = new(7, 2, 0);

    public static ZabbixVersion? Parse(string? text)
    {
        var parts = (text ?? "").Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor)
            ? new ZabbixVersion(major, minor, parts.Length > 2 && int.TryParse(parts[2], out var patch) ? patch : 0)
            : null;
    }

    public int CompareTo(ZabbixVersion? other) => other is null ? 1 : (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public static bool operator <(ZabbixVersion a, ZabbixVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(ZabbixVersion a, ZabbixVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(ZabbixVersion a, ZabbixVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(ZabbixVersion a, ZabbixVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
