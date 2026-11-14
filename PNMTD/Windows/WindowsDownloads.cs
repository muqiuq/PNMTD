using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Helper;

namespace PNMTD.Windows;

/// <summary>
/// The agent's release links (docs/windows-sensor-plan.md §10.1, §11): the GitHub release of the sensor tag of PNMTD's own version
/// (<c>sensor-1.2.0</c>), for x64 and ARM64, with the SHA-256 from the release's <c>.sha256</c> assets, fetched and cached.
/// Without GitHub, links only.
/// </summary>
public sealed partial class WindowsDownloads(IHttpClientFactory http, IOptionsMonitor<WindowsOptions> options, ILogger<WindowsDownloads> logger)
{
    public static readonly string[] Architectures = ["x64", "arm64"];

    public static string FileName(string architecture) => $"pnmt-sensor-{architecture}.exe";

    /// <summary>The tag whose release has the agent (.github/workflows/sensor.yml); version tags (v1.2.0) publish only the image.</summary>
    public static string ReleaseTag(string version) => $"sensor-{version}";

    private readonly SemaphoreSlim gate = new(1, 1);
    private (DateTime At, string Base, Dictionary<string, string?> Sums)? cache;
    private Task? refreshing;

    /// <summary>The links; <paramref name="wait"/>: fetch the checksums first when they are stale, else answer with what is cached.</summary>
    public async Task<WindowsAgentDownloads> GetAsync(CancellationToken ct, bool wait)
    {
        var baseUrl = options.CurrentValue.AgentDownloadBaseUrl.TrimEnd('/');
        var version = ServerInfo.Version;
        var stale = cache is not { } c || c.Base != baseUrl || Clock.UtcNow - c.At > (c.Sums.Values.Any(v => v is null) ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(6));
        if (stale)
        {
            if (wait)
            {
                await RefreshAsync(baseUrl, version, ct);
            }
            else
            {
                refreshing ??= Task.Run(async () =>
                {
                    try
                    {
                        await RefreshAsync(baseUrl, version, CancellationToken.None);
                    }
                    finally
                    {
                        refreshing = null;
                    }
                });
            }
        }
        var sums = cache is { } cached && cached.Base == baseUrl ? cached.Sums : new Dictionary<string, string?>();
        var files = Architectures.Select(a => new WindowsAgentDownload(a, FileName(a), $"{baseUrl}/{ReleaseTag(version)}/{FileName(a)}", sums.GetValueOrDefault(a))).ToList();
        var release = baseUrl.EndsWith("/releases/download", StringComparison.Ordinal) ? $"{baseUrl[..^"/download".Length]}/tag/{ReleaseTag(version)}" : null;
        var note = files.All(f => f.Sha256 is not null) ? null
            : "The SHA-256 could not be read from GitHub: compare with the .sha256 files of the release.";
        return new WindowsAgentDownloads(version, files, release, note);
    }

    private async Task RefreshAsync(string baseUrl, string version, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var sums = new Dictionary<string, string?>();
            using var client = http.CreateClient(nameof(WindowsDownloads));
            client.Timeout = TimeSpan.FromSeconds(5);
            foreach (var architecture in Architectures)
            {
                try
                {
                    var text = await client.GetStringAsync($"{baseUrl}/{ReleaseTag(version)}/{FileName(architecture)}.sha256", ct);
                    sums[architecture] = Sha256().Match(text) is { Success: true } m ? m.Value.ToLowerInvariant() : null;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    logger.LogDebug("The agent's checksum for {Architecture} could not be read: {Message}", architecture, e.Message);
                    sums[architecture] = null;
                }
            }
            cache = (Clock.UtcNow, baseUrl, sums);
        }
        finally
        {
            gate.Release();
        }
    }

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b")]
    private static partial Regex Sha256();
}
