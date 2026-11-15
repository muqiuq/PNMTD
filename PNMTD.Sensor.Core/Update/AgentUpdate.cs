using System.Security.Cryptography;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.State;

namespace PNMTD.Sensor.Core.Update;

/// <summary>
/// What PNMT offers this agent (docs/agent-update-plan.md §5.1, §6.2): the version its platform's agents get, whether it is newer
/// or older than this agent, and this architecture's file with where PNMT got it (null when PNMT doesn't have it).
/// </summary>
public sealed record UpdateOffer(string Version, bool PreRelease, string AssetName, DownloadAsset? Asset, bool Newer, bool Older);

/// <summary>A downloaded file, checked against the manifest: where it is, its version, and where PNMT got it.</summary>
public sealed record UpdateCandidate(string File, string Version, string? Source);

/// <summary>PNMT has nothing newer than this agent: the update says so and exits 0.</summary>
public sealed class AlreadyUpToDateException(string message) : Exception(message);

/// <summary>
/// The update's platform-neutral half (docs/agent-update-plan.md §6.1, §6.2), for both agents: PNMT's signed manifest for this
/// platform (<c>GET /agent/v1/download?platform=</c>), checked against the key pinned at the join and its expiry, so a
/// TLS-inspecting proxy can neither hand over another binary nor replay an old manifest; this architecture's file, downloaded
/// and checked against the manifest's size and SHA-256; and what the new binary's <c>version</c> must answer. Each platform
/// installs the file itself (Linux with systemd, Windows with the service manager). <b>Only the command line and the window call
/// this</b>, never the service: an update starts on the machine (§7).
/// </summary>
public static class AgentUpdate
{
    /// <summary>A file larger than this is no agent.</summary>
    public const long MaxBytes = 300L * 1024 * 1024;

    /// <summary><c>update --check</c>'s exit code when PNMT offers a newer version (0: up to date).</summary>
    public const int NewerExitCode = 10;

    /// <summary><c>update-install</c>'s exit code when the new version didn't start and the previous one runs again (§6.2, step 7).</summary>
    public const int RolledBackExitCode = 3;

    /// <summary>
    /// PNMT's offer for this agent: the manifest for <paramref name="platform"/>, verified; this architecture's file in it. A PNMT
    /// before 1.8 ignores <c>platform</c> and answers its own version's files of both platforms, which works the same.
    /// </summary>
    public static async Task<UpdateOffer> OfferAsync(AgentConfigFile config, AgentPlatform platform, string architecture, string currentVersion,
        IProgress<string> steps, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        using var pnmtdKey = AgentSigning.ImportPublicKey(config.PnmtdKey) ?? throw new InvalidOperationException("agent.json holds no valid PNMTD key: join again.");
        var baseUrl = BaseUrl(config);
        using var http = Client(handler, TimeSpan.FromMinutes(2));
        steps.Report($"Asking {config.Url} which version {platform} agents get");
        using var response = await http.GetAsync(new Uri(baseUrl, $"{AgentProtocol.DownloadPath}?platform={platform}"), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PNMT answered {(int)response.StatusCode} {response.ReasonPhrase} for {AgentProtocol.DownloadPath}"
                + (response.StatusCode == System.Net.HttpStatusCode.NotFound ? ": it doesn't serve the agent's files (Agents__ServeDownloads)." : "."));
        }
        var answer = AgentJson.Deserialize<DownloadManifestResponse>(await response.Content.ReadAsStringAsync(ct))
            ?? throw new InvalidOperationException("PNMT's manifest is empty.");
        var manifest = Verify(answer, pnmtdKey, DateTimeOffset.UtcNow);
        var name = AgentDownloadAssets.For(platform, architecture);
        return new UpdateOffer(manifest.Version, manifest.PreRelease || AgentVersionOrder.IsPreRelease(manifest.Version), name,
            manifest.Assets.FirstOrDefault(a => a.Name == name), AgentVersionOrder.IsNewer(manifest.Version, currentVersion),
            AgentVersionOrder.Compare(manifest.Version, currentVersion) < 0);
    }

    /// <summary>
    /// The manifest's signature against the pinned key, its type, its (empty) agent id and its expiry; refused otherwise. A PNMT
    /// before 1.8 signs no expiry.
    /// </summary>
    public static DownloadManifest Verify(DownloadManifestResponse answer, ECDsa pinned, DateTimeOffset now)
    {
        var content = AgentSigning.Verify(pinned, answer.Signed);
        if (content is null || content.Type != AgentMessageTypes.DownloadManifest || content.AgentId != Guid.Empty)
        {
            throw new InvalidOperationException("PNMT's manifest is not signed with the key pinned at the join: refused.");
        }
        if (content.ExpiresAt is { } expires && now > expires)
        {
            throw new InvalidOperationException(
                $"PNMT's manifest expired at {expires:u}, and this machine's clock says {now:u}: an old manifest, or this clock is wrong. Refused.");
        }
        return AgentJson.Deserialize<DownloadManifest>(content.Body) ?? throw new InvalidOperationException("PNMT's manifest is empty.");
    }

    /// <summary>
    /// The offer's file, downloaded into <paramref name="folder"/> (which <paramref name="secure"/> makes the administrators' only,
    /// before anything is written) and checked against the manifest's size and SHA-256.
    /// </summary>
    public static async Task<UpdateCandidate> DownloadAsync(AgentConfigFile config, UpdateOffer offer, string folder, Action<string> secure,
        IProgress<string> steps, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var asset = offer.Asset ?? throw new InvalidOperationException($"PNMT {offer.Version} has no {offer.AssetName} (yet): its Versions tab says why.");
        if (asset.Size <= 0 || asset.Size > MaxBytes)
        {
            throw new InvalidOperationException($"The manifest's size for {asset.Name} ({asset.Size} bytes) is not plausible.");
        }
        var baseUrl = BaseUrl(config);
        using var http = Client(handler, TimeSpan.FromMinutes(15));
        steps.Report($"Downloading {asset.Name} {offer.Version} ({asset.Size / (1024 * 1024)} MB)");
        secure(folder);
        var target = Path.Combine(folder, asset.Name);
        File.Delete(target);
        using (var download = await http.GetAsync(new Uri(baseUrl, $"{AgentProtocol.DownloadPath}/{asset.Name}"), HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!download.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"PNMT answered {(int)download.StatusCode} {download.ReasonPhrase} for {asset.Name}.");
            }
            await using var source = await download.Content.ReadAsStreamAsync(ct);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            }
            await using var output = new FileStream(target, options);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > asset.Size)
                {
                    throw new InvalidOperationException($"{asset.Name} is larger than the manifest says ({asset.Size} bytes): refused.");
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (total != asset.Size || sha256 != asset.Sha256.ToLowerInvariant())
            {
                throw new InvalidOperationException($"{asset.Name} doesn't match PNMT's signed manifest ({total} bytes, SHA-256 {sha256}): refused.");
            }
        }
        steps.Report($"Downloaded {offer.Version}: its size and SHA-256 match PNMT's signed manifest");
        return new UpdateCandidate(target, offer.Version, asset.Source);
    }

    /// <summary>
    /// Why a new binary's <c>version</c> answer is not what may be installed (null: it is): <c>{product} {version} ({architecture})</c>,
    /// for this agent's product and architecture, and the manifest's version when it came from PNMT (§6.2, step 4).
    /// </summary>
    public static string? VersionProblem(string answer, string product, string architecture, string? expectedVersion)
    {
        var line = answer.Trim().Split('\n')[0].Trim();
        if (!line.StartsWith(product + " ", StringComparison.Ordinal))
        {
            return $"it doesn't answer as a {product} (\"{Cut(line)}\")";
        }
        var parts = line[(product.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var version = parts.Length > 0 ? parts[0] : "";
        var arch = parts.Length > 1 ? parts[1].Trim('(', ')') : "";
        if (!AgentVersionOrder.IsValid(version))
        {
            return $"its version can't be read (\"{Cut(line)}\")";
        }
        if (arch.Length > 0 && arch != architecture)
        {
            return $"it is for {arch}, this machine is {architecture}";
        }
        if (expectedVersion is not null && version != expectedVersion)
        {
            return $"it is {version}, but PNMT's manifest says {expectedVersion}";
        }
        return null;
    }

    /// <summary>The version in a <c>version</c> answer: <c>PNMT agent 1.8.0 (arm64)</c> is 1.8.0.</summary>
    public static string? VersionOf(string answer, string product)
    {
        var line = answer.Trim().Split('\n')[0].Trim();
        return line.StartsWith(product + " ", StringComparison.Ordinal) && line[(product.Length + 1)..].Split(' ')[0] is var v && AgentVersionOrder.IsValid(v) ? v : null;
    }

    /// <summary>
    /// Whether the service of <paramref name="version"/> runs: it wrote <c>status.json</c> since <paramref name="since"/> with its
    /// version (§6.2, step 7). <paramref name="running"/> asks the platform whether the service is up.
    /// </summary>
    public static async Task<bool> WaitRunningAsync(string version, DateTimeOffset since, Func<bool> running, TimeSpan within, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + within;
        while (DateTimeOffset.UtcNow < until)
        {
            if (running() && AgentFiles.ReadStatus() is { } status && status.Version == version && status.UpdatedAt >= since.AddSeconds(-1))
            {
                return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return false;
    }

    private static Uri BaseUrl(AgentConfigFile config)
    {
        var baseUrl = new Uri(config.Url);
        if (baseUrl.Scheme != "https" && !config.PlainHttp)
        {
            throw new InvalidOperationException($"{config.Url} is not https://, and the join didn't confirm plain http://.");
        }
        return baseUrl;
    }

    private static HttpClient Client(HttpMessageHandler? handler, TimeSpan timeout)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = timeout;
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"pnmt-agent-update/{typeof(AgentUpdate).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        return http;
    }

    private static string Cut(string text) => text.Length > 80 ? text[..80] + "…" : text;
}
