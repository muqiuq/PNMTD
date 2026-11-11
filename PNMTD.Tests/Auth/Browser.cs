using System.Net.Http.Json;
using PNMTD.Api.V1.Contracts;

namespace PNMTD.Tests.Auth;

/// <summary>
/// A client that keeps cookies like a browser and sends the XSRF-TOKEN cookie back in X-XSRF-TOKEN,
/// as the SPA will. Against a TestApp with the real cookie scheme (testSignIn: false).
/// </summary>
internal sealed class Browser(TestApp app)
{
    private readonly HttpClient client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    /// <summary>The raw Set-Cookie headers of the last response.</summary>
    public List<string> LastSetCookies { get; } = [];

    public bool SendCsrfToken { get; set; } = true;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: ApiJson.Options);
        }
        if (Cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        }
        if (SendCsrfToken && Cookies.TryGetValue("XSRF-TOKEN", out var token))
        {
            request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(token));
        }
        var response = await client.SendAsync(request);
        LastSetCookies.Clear();
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var header in setCookies)
            {
                LastSetCookies.Add(header);
                var pair = header.Split(';')[0];
                var name = pair[..pair.IndexOf('=')];
                var value = pair[(pair.IndexOf('=') + 1)..];
                var expired = header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
                if (expired || value.Length == 0)
                {
                    Cookies.Remove(name);
                }
                else
                {
                    Cookies[name] = value;
                }
            }
        }
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null) => SendAsync(HttpMethod.Post, url, body ?? new { });

    public async Task<LoginResponse> LoginAsync(string username, string password)
    {
        using var response = await PostAsync("/api/v1/auth/login", new { username, password });
        return await response.ReadAsync<LoginResponse>();
    }
}
