using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;

namespace PNMTD.Agents;

/// <summary>
/// Downloads per address (docs/linux-sensor-plan.md §4.4, §9.7): <c>Agents:DownloadsPerHour</c> (20) of the agents' files, in a
/// window of an hour.
/// </summary>
public sealed class DownloadGuard
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, (DateTime Start, int Count)> perAddress = new(StringComparer.Ordinal);

    /// <summary>False when the address used up its downloads; <paramref name="retryAfter"/> is when its window ends.</summary>
    public bool TryEnter(string address, int perHour, out TimeSpan retryAfter)
    {
        var now = Clock.UtcNow;
        var entry = perAddress.AddOrUpdate(address, _ => (now, 1), (_, e) => now - e.Start >= Window ? (now, 1) : (e.Start, e.Count + 1));
        if (perAddress.Count > 10_000)
        {
            foreach (var (key, value) in perAddress)
            {
                if (now - value.Start >= Window)
                {
                    perAddress.TryRemove(key, out _);
                }
            }
        }
        retryAfter = entry.Start + Window - now;
        return entry.Count <= Math.Max(1, perHour);
    }

    /// <summary>For tests.</summary>
    public void Reset() => perAddress.Clear();
}

/// <summary>
/// The agents' files from PNMTD itself (docs/linux-sensor-plan.md §4.4), two anonymous paths under <c>/agent/v1/</c>: a server
/// isn't joined when it downloads, and the files are public on GitHub anyway. Both answer 404 while the agents or
/// <see cref="AgentOptions.ServeDownloads"/> are off.
/// <list type="bullet">
/// <item><c>GET /agent/v1/download</c>: the manifest of the files here and verified, signed with PNMTD's agent signing key, which
/// <c>pnmt-agent update</c> checks against the key it pinned at the join;</item>
/// <item><c>GET /agent/v1/download/{asset}</c>: one of <see cref="AgentDownloadAssets.All"/>, only while it matches its
/// <c>.sha256</c>; anything else is 404. 20 per hour and address (429 after).</item>
/// </list>
/// </summary>
public static class AgentDownloadEndpoint
{
    public static async Task<IResult> ManifestAsync(AgentDownloads downloads, AgentKeyStore keys, IOptionsMonitor<AgentOptions> options, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled || !settings.ServeDownloads)
        {
            return Results.NotFound();
        }
        var manifest = await downloads.ManifestAsync(ct);
        var key = await keys.GetAsync(ct);
        var signed = AgentSigning.Sign(key.Key, AgentMessageTypes.DownloadManifest, Guid.Empty, manifest);
        return Results.Json(new DownloadManifestResponse(key.Fingerprint, signed), AgentJson.Options);
    }

    public static async Task<IResult> AssetAsync(string asset, HttpContext http, AgentDownloads downloads, DownloadGuard guard, IOptionsMonitor<AgentOptions> options,
        CancellationToken ct)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled || !settings.ServeDownloads || !AgentDownloadAssets.IsKnown(asset))
        {
            return Results.NotFound();
        }
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!guard.TryEnter(address, settings.DownloadsPerHour, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return Results.Text($"Too many downloads from this address: at most {settings.DownloadsPerHour} per hour. GitHub's release has the same files.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        var sum = asset.EndsWith(".sha256", StringComparison.Ordinal);
        var executable = sum ? asset[..^".sha256".Length] : asset;
        var (file, problem) = await downloads.PrepareAsync(executable, ct);
        if (file is null)
        {
            return Results.Text($"{asset} is not available on this PNMT: {problem ?? "it is not in the download folder"}. GitHub's release has the same files.",
                statusCode: StatusCodes.Status404NotFound);
        }
        if (sum)
        {
            // The line that was verified, as sha256sum writes it.
            return Results.Bytes(Encoding.UTF8.GetBytes($"{file.Sha256}  {executable}\n"), "application/octet-stream");
        }
        var stream = AgentDownloads.OpenVerified(file);
        if (stream is null)
        {
            return Results.Text($"{asset} changed while it was checked: try again.", statusCode: StatusCodes.Status404NotFound);
        }
        return Results.Stream(stream, "application/octet-stream");
    }
}
