using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;

namespace PNMTD.Agents;

/// <summary>A release of the agents on GitHub (a <c>sensor-</c> tag): its version, whether it is a pre-release, when, its page and files.</summary>
public sealed record AgentRelease(string Version, bool PreRelease, DateTime? PublishedAt, string? Url, IReadOnlySet<string> Assets);

/// <summary>
/// GitHub's list of the agents' releases (docs/agent-update-plan.md §3.2), for <i>Admin → Servers → Versions</i>: the releases
/// whose tag starts with <c>sensor-</c>, drafts left out, from <see cref="AgentOptions.ReleasesApiUrl"/> (derived from
/// <see cref="AgentOptions.AgentDownloadBaseUrl"/> for GitHub, none for a mirror). Cached an hour, 10 minutes after a failure:
/// GitHub allows 60 requests an hour per address without a token. The named HTTP client <c>AgentDownloads</c>, which the tests
/// replace.
/// </summary>
public sealed partial class AgentReleaseCatalog(IHttpClientFactory http, IOptionsMonitor<AgentOptions> options, ILogger<AgentReleaseCatalog> logger)
{
    public static readonly TimeSpan CachedFor = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim gate = new(1, 1);
    private (DateTime At, string Url, IReadOnlyList<AgentRelease> Releases, string? Error)? cache;

    /// <summary>The list's URL: the setting, or derived from GitHub's download base; null when there is none.</summary>
    public string? ApiUrl
    {
        get
        {
            var settings = options.CurrentValue;
            if (settings.ReleasesApiUrl is { } set)
            {
                return string.IsNullOrWhiteSpace(set) ? null : set.Trim();
            }
            var match = GitHubDownloads().Match(settings.AgentDownloadBaseUrl.Trim());
            return match.Success ? $"https://api.github.com/repos/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}/releases?per_page=100" : null;
        }
    }

    /// <summary>The next <see cref="GetAsync"/> reads the list again.</summary>
    public void Forget() => cache = null;

    /// <summary>The releases (newest first) and the list's state: read now when stale, waiting at most 10 seconds.</summary>
    public async Task<(IReadOnlyList<AgentRelease> Releases, AgentReleaseCatalogState State)> GetAsync(CancellationToken ct)
    {
        if (ApiUrl is not { } url)
        {
            return ([], new AgentReleaseCatalogState(false, false, null, null));
        }
        if (cache is not { } c || c.Url != url || Clock.UtcNow - c.At > (c.Error is null ? CachedFor : AgentDownloads.FailedFor))
        {
            await RefreshAsync(url, ct);
        }
        var now = cache;
        return now is { } n && n.Url == url
            ? (n.Releases, new AgentReleaseCatalogState(true, n.Error is null, n.At, n.Error))
            : ([], new AgentReleaseCatalogState(true, false, null, "not read yet"));
    }

    private async Task RefreshAsync(string url, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cache is { } c && c.Url == url && Clock.UtcNow - c.At < TimeSpan.FromSeconds(5))
            {
                return;
            }
            var previous = cache is { } p && p.Url == url ? p.Releases : [];
            try
            {
                using var client = http.CreateClient(nameof(AgentDownloads));
                client.Timeout = TimeSpan.FromSeconds(10);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PNMTD", ServerInfo.Version.Split('+')[0]));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                using var response = await client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    cache = (Clock.UtcNow, url, previous, $"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}");
                    return;
                }
                cache = (Clock.UtcNow, url, Parse(await response.Content.ReadAsStringAsync(ct)), null);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                logger.LogInformation("GitHub's list of the agents' releases could not be read from {Url}: {Message}", url, e.Message);
                cache = (Clock.UtcNow, url, previous, e is TaskCanceledException ? $"{url} didn't answer within 10 seconds" : $"{url}: {e.Message}");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>GitHub's JSON: the releases with a <c>sensor-{version}</c> tag that aren't drafts, newest version first.</summary>
    public static IReadOnlyList<AgentRelease> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("GitHub's answer is not a list of releases.");
        }
        var releases = new List<AgentRelease>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var tag = Text(release, "tag_name");
            if (tag is null || !tag.StartsWith("sensor-", StringComparison.Ordinal) || Flag(release, "draft"))
            {
                continue;
            }
            var version = tag["sensor-".Length..];
            if (!AgentVersionOrder.IsValid(version))
            {
                continue;
            }
            var assets = release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(a => Text(a, "name")).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
            var published = Text(release, "published_at") is { } at && DateTime.TryParse(at, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var when)
                ? DateTime.SpecifyKind(when, DateTimeKind.Utc) : (DateTime?)null;
            releases.Add(new AgentRelease(version, Flag(release, "prerelease") || AgentVersionOrder.IsPreRelease(version), published, Text(release, "html_url"), assets));
        }
        return releases.DistinctBy(r => r.Version).OrderByDescending(r => r.Version, Comparer<string>.Create(AgentVersionOrder.Compare)).ToList();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"^https://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/releases/download/?$")]
    private static partial Regex GitHubDownloads();
}
