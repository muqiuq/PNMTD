using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;

namespace PNMTD.Agents;

/// <summary>A file in the download folder that matches its <c>.sha256</c>: what may be served, and what the manifest lists.</summary>
public sealed record VerifiedFile(string Name, string Path, long Length, DateTime ModifiedAt, string Sha256);

/// <summary>
/// The agents' files of PNMTD's own version (docs/linux-sensor-plan.md §4.4, §9.4, §9.5, §9.7; docs/windows-sensor-plan.md §10.1):
/// <list type="bullet">
/// <item>the <b>download folder</b> (<see cref="AgentOptions.DownloadFolder"/>): a file is served only while it matches its
/// <c>.sha256</c> (<c>sha256sum</c>'s format), checked by size and time and its hash kept;</item>
/// <item>a file that isn't there is <b>fetched from the GitHub release of PNMTD's version</b> (the sensor tag,
/// <c>{AgentDownloadBaseUrl}/sensor-{version}/{file}</c>) when it is first asked for, checked against the release's
/// <c>.sha256</c> and kept. Requests meanwhile wait for the one fetch; a failed fetch is remembered for 10 minutes, as the release's
/// hashes are. A file that is there and doesn't match is not replaced, except by <i>Fetch again</i>;</item>
/// <item>the release's hashes (its <c>.sha256</c> files, fetched and cached as before L3) for the links and the install lines of
/// files that aren't here yet;</item>
/// <item>the <b>manifest</b> of the files here and verified, which <see cref="AgentDownloadEndpoint"/> signs;</item>
/// <item>the links, states and <b>Linux install lines</b> of <c>GET /api/v1/agents/downloads</c>.</item>
/// </list>
/// The fetcher is the named HTTP client <c>AgentDownloads</c>: the tests replace its handler.
/// </summary>
public sealed partial class AgentDownloads(IHttpClientFactory http, IOptionsMonitor<AgentOptions> options, ILogger<AgentDownloads> logger)
{
    public static readonly string[] Architectures = ["x64", "arm64"];

    /// <summary>The token in the lines of the Download tab; the Add server dialog puts the real one in its place.</summary>
    public const string TokenPlaceholder = "xxxx-xxxx";

    /// <summary>The update line (§4.4): run on the server; it checks PNMT's signed manifest against the key pinned at the join.</summary>
    public const string UpdateLine = "pnmt-agent update";

    /// <summary>A file larger than this is no agent: neither fetched nor served.</summary>
    public const long MaxBytes = 300L * 1024 * 1024;

    /// <summary>A failed fetch is not tried again before this, except by <i>Fetch again</i>.</summary>
    public static readonly TimeSpan FailedFor = TimeSpan.FromMinutes(10);

    /// <summary>Windows' executable for an architecture, as before L3.</summary>
    public static string FileName(string architecture) => AgentDownloadAssets.For(AgentPlatform.Windows, architecture);

    /// <summary>The tag whose release has the agents (.github/workflows/sensor.yml); version tags (v1.2.0) publish only the image.</summary>
    public static string ReleaseTag(string version) => $"sensor-{version}";

    public static string ArchitectureOf(string executable) => executable.Contains("arm64", StringComparison.Ordinal) ? "arm64" : "x64";

    private readonly SemaphoreSlim gate = new(1, 1);
    private (DateTime At, string Base, Dictionary<string, string?> Sums)? cache;
    private Task? refreshing;
    private readonly ConcurrentDictionary<string, (long Length, DateTime ModifiedAt, string Sha256)> hashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> fetches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, string Reason)> failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> fetched = new(StringComparer.Ordinal);

    /// <summary>The download folder, absolute; a relative one (<c>dist</c>) is the working directory's.</summary>
    public string Folder => Path.GetFullPath(string.IsNullOrWhiteSpace(options.CurrentValue.DownloadFolder) ? "/var/lib/pnmtd/downloads" : options.CurrentValue.DownloadFolder);

    /// <summary>
    /// Where fetched files go: <c>sensor-{version}/</c> in the download folder, which is a volume in the compose deployment and so
    /// outlives an update; a new version fetches into a folder of its own, and the older ones are deleted. Files placed in the
    /// download folder itself (an admin's for a PNMT without internet, or <c>dist/</c>) are served when this one has none.
    /// </summary>
    public string ReleaseFolder => Path.Combine(Folder, ReleaseTag(ServerInfo.Version));

    // --- the folder ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An executable and its <c>.sha256</c> in the folder: verified, or why not (a problem), or neither when either is missing.
    /// Hashed again only when its size or time changed.
    /// </summary>
    public (VerifiedFile? File, string? Problem) Check(string executable)
    {
        var fetchedHere = CheckIn(ReleaseFolder, executable);
        return fetchedHere.File is not null || fetchedHere.Problem is not null ? fetchedHere : CheckIn(Folder, executable);
    }

    private (VerifiedFile? File, string? Problem) CheckIn(string folder, string executable)
    {
        try
        {
            var path = Path.Combine(folder, executable);
            var sumPath = path + ".sha256";
            if (!File.Exists(path) || !File.Exists(sumPath))
            {
                return (null, null);
            }
            var sumInfo = new FileInfo(sumPath);
            if (sumInfo.Length > 4096)
            {
                return (null, $"{executable}.sha256 is not a checksum file (above 4 KB)");
            }
            var expected = ParseSum(File.ReadAllText(sumPath), executable, out var sumProblem);
            if (expected is null)
            {
                return (null, sumProblem);
            }
            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                return (null, $"{executable} is larger than {MaxBytes / 1024 / 1024} MB");
            }
            var modified = info.LastWriteTimeUtc;
            string actual;
            if (hashes.TryGetValue(path, out var known) && known.Length == info.Length && known.ModifiedAt == modified)
            {
                actual = known.Sha256;
            }
            else
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    actual = Convert.ToHexStringLower(SHA256.HashData(stream));
                }
                hashes[path] = (info.Length, modified, actual);
            }
            return actual == expected
                ? (new VerifiedFile(executable, path, info.Length, modified, actual), null)
                : (null, $"{executable} does not match its .sha256: not served");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"{executable} can't be read: {e.Message}");
        }
    }

    /// <summary>
    /// The file, opened, while it is the one that was verified (the same size and time on the open handle); else null. A file
    /// renamed into place meanwhile is a new one, and the open handle keeps reading the verified one.
    /// </summary>
    public static FileStream? OpenVerified(VerifiedFile file)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true);
            if (RandomAccess.GetLength(stream.SafeFileHandle) == file.Length && File.GetLastWriteTimeUtc(stream.SafeFileHandle) == file.ModifiedAt)
            {
                return stream;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        stream?.Dispose();
        return null;
    }

    /// <summary>
    /// <c>sha256sum</c>'s line for this file: the hash (64 hex digits), two spaces (or a space and *), and its name, which must be
    /// this file's (a path to it is fine). The hash in lower case, or null with the problem.
    /// </summary>
    public static string? ParseSum(string text, string executable, out string? problem)
    {
        problem = null;
        var line = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var match = SumLine().Match(line);
        if (!match.Success)
        {
            problem = $"{executable}.sha256 can't be read: expected the hash, two spaces and the name";
            return null;
        }
        var name = match.Groups["name"].Value.Trim().TrimStart('*');
        if (name.Length > 0 && Path.GetFileName(name.Replace('\\', '/')) != executable)
        {
            problem = $"{executable}.sha256 is for another file ({Cuts.Cut(name, 100)})";
            return null;
        }
        return match.Groups["hash"].Value.ToLowerInvariant();
    }

    // --- fetching ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An executable for a download: verified, fetched first when it isn't in the folder (waiting for the fetch that is running), or
    /// why it can't be served.
    /// </summary>
    public async Task<(VerifiedFile? File, string? Problem)> PrepareAsync(string executable, CancellationToken ct)
    {
        var (file, problem) = Check(executable);
        if (file is not null || problem is not null)
        {
            return (file, problem);
        }
        if (RecentFailure(executable) is { } failed)
        {
            return (null, failed);
        }
        var error = await StartFetch(executable).WaitAsync(ct);
        return error is null ? Check(executable) : (null, error);
    }

    /// <summary>
    /// Starts fetching these executables when they aren't here (<paramref name="again"/>: also those that don't match, and those
    /// that failed within <see cref="FailedFor"/>): the Add server dialog asks ahead, <i>Fetch again</i> asks again. Never waits.
    /// </summary>
    public IReadOnlyList<Task<string?>> FetchAhead(IEnumerable<string> executables, bool again = false)
    {
        var started = new List<Task<string?>>();
        foreach (var executable in executables.Where(AgentDownloadAssets.Executables.Contains))
        {
            var (file, problem) = Check(executable);
            if (file is not null || (problem is not null && !again) || (!again && RecentFailure(executable) is not null))
            {
                continue;
            }
            if (again)
            {
                failures.TryRemove(executable, out _);
            }
            started.Add(StartFetch(executable));
        }
        return started;
    }

    /// <summary>The executables of a platform.</summary>
    public static IReadOnlyList<string> Of(AgentPlatform platform) =>
        AgentDownloadAssets.Executables.Where(e => AgentDownloadAssets.PlatformOf(e) == platform).ToList();

    private string? RecentFailure(string executable) =>
        failures.TryGetValue(executable, out var failure) && Clock.UtcNow - failure.At < FailedFor ? failure.Reason : null;

    /// <summary>One fetch per file at a time: a second request waits for the first.</summary>
    private Task<string?> StartFetch(string executable)
    {
        var lazy = fetches.GetOrAdd(executable, name => new Lazy<Task<string?>>(() => Task.Run(() => FetchAsync(name))));
        var task = lazy.Value;
        _ = task.ContinueWith(_ => fetches.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(executable, lazy)), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// Fetches an executable and its <c>.sha256</c> from the release into the folder: the hash first, then the file into a temporary
    /// name, hashed while it is written, and renamed into place only when it matches. Null when it worked, else why not.
    /// </summary>
    private async Task<string?> FetchAsync(string executable)
    {
        var baseUrl = options.CurrentValue.AgentDownloadBaseUrl.TrimEnd('/');
        var tag = ReleaseTag(ServerInfo.Version);
        var url = $"{baseUrl}/{tag}/{executable}";
        string? temporary = null;
        string? temporarySum = null;
        try
        {
            var folder = ReleaseFolder;
            using var client = http.CreateClient(nameof(AgentDownloads));
            client.Timeout = Timeout.InfiniteTimeSpan;
            string expected;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                using var response = await client.GetAsync(url + ".sha256", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return Failed(executable, $"the release {tag} answered {(int)response.StatusCode} {response.ReasonPhrase} for {executable}.sha256");
                }
                var text = await ReadTextAsync(response, 4096, timeout.Token);
                if (ParseSum(text, executable, out var problem) is not { } sum)
                {
                    return Failed(executable, $"the release's {problem}");
                }
                expected = sum;
            }
            try
            {
                Directory.CreateDirectory(folder);
                temporary = Path.Combine(folder, $".{executable}.{Guid.NewGuid():N}.part");
                await using (File.Create(temporary))
                {
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                temporary = null;
                return Failed(executable, $"the download folder {folder} can't be written: {e.Message}");
            }
            string actual;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return Failed(executable, $"the release {tag} answered {(int)response.StatusCode} {response.ReasonPhrase} for {executable}");
                }
                if (response.Content.Headers.ContentLength > MaxBytes)
                {
                    return Failed(executable, $"the release's {executable} is larger than {MaxBytes / 1024 / 1024} MB");
                }
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = new FileStream(temporary, FileMode.Truncate, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        return Failed(executable, $"the release's {executable} is larger than {MaxBytes / 1024 / 1024} MB");
                    }
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                actual = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (actual != expected)
            {
                return Failed(executable, $"the {executable} the release sent does not match its .sha256: not kept");
            }
            temporarySum = Path.Combine(folder, $".{executable}.sha256.{Guid.NewGuid():N}.part");
            await File.WriteAllTextAsync(temporarySum, $"{expected}  {executable}\n");
            File.Move(temporarySum, Path.Combine(folder, executable + ".sha256"), overwrite: true);
            temporarySum = null;
            File.Move(temporary, Path.Combine(folder, executable), overwrite: true);
            temporary = null;
            failures.TryRemove(executable, out _);
            fetched[executable] = Clock.UtcNow;
            logger.LogInformation("Fetched {File} from {Url} into {Folder}: SHA-256 {Sha256}", executable, url, folder, expected);
            DeleteOlderReleases();
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            return Failed(executable, e is OperationCanceledException ? $"fetching {url} timed out" : $"fetching {url} failed: {e.Message}");
        }
        finally
        {
            foreach (var leftover in new[] { temporary, temporarySum }.OfType<string>())
            {
                try
                {
                    File.Delete(leftover);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>The folders older versions fetched into (<c>sensor-*</c> but this version's): an update leaves them behind in the volume.</summary>
    private void DeleteOlderReleases()
    {
        try
        {
            var current = Path.GetFileName(ReleaseFolder);
            foreach (var old in Directory.GetDirectories(Folder, "sensor-*").Where(d => Path.GetFileName(d) != current))
            {
                Directory.Delete(old, recursive: true);
                logger.LogInformation("Deleted the agent files of an older version: {Folder}", old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The older versions' agent files could not be deleted: {Message}", e.Message);
        }
    }

    private string Failed(string executable, string reason)
    {
        failures[executable] = (Clock.UtcNow, reason);
        logger.LogWarning("The agent's file {File} could not be fetched: {Reason}", executable, reason);
        return reason;
    }

    private static async Task<string> ReadTextAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[max];
        var total = 0;
        int read;
        while (total < max && (read = await stream.ReadAsync(buffer.AsMemory(total, max - total), ct)) > 0)
        {
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    // --- the manifest (§4.4) ------------------------------------------------------------------------------------

    /// <summary>
    /// The files here and verified, for <c>GET /agent/v1/download</c>. <c>pnmt-agent update</c> asks for a Linux file: those that
    /// aren't here yet are fetched first, waited for at most 30 seconds.
    /// </summary>
    public async Task<DownloadManifest> ManifestAsync(CancellationToken ct)
    {
        var fetching = FetchAhead(Of(AgentPlatform.Linux));
        if (fetching.Count > 0)
        {
            try
            {
                await Task.WhenAll(fetching).WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            catch (TimeoutException)
            {
            }
        }
        var assets = AgentDownloadAssets.Executables.Select(e => Check(e).File).OfType<VerifiedFile>()
            .Select(f => new DownloadAsset(f.Name, f.Length, f.Sha256)).ToList();
        return new DownloadManifest(ServerInfo.Version, assets);
    }

    // --- the links (GET /api/v1/agents/downloads) ---------------------------------------------------------------

    /// <summary>
    /// Every file's links, hash, size and state, Windows' links as before L3, and the Linux install and update lines with PNMT's
    /// URL (<paramref name="pnmtUrl"/>) and <see cref="TokenPlaceholder"/>. <paramref name="wait"/>: fetch the release's hashes first
    /// when they are stale, else answer with what is cached.
    /// </summary>
    public async Task<AgentDownloadLinks> GetAsync(string pnmtUrl, CancellationToken ct, bool wait)
    {
        var settings = options.CurrentValue;
        var baseUrl = settings.AgentDownloadBaseUrl.TrimEnd('/');
        var version = ServerInfo.Version;
        var stale = cache is not { } c || c.Base != baseUrl || Clock.UtcNow - c.At > (c.Sums.Values.Any(v => v is null) ? FailedFor : TimeSpan.FromHours(6));
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
        var tag = ReleaseTag(version);
        pnmtUrl = pnmtUrl.TrimEnd('/');
        string? PnmtLink(string file) => settings.ServeDownloads && settings.Enabled ? $"{pnmtUrl}{AgentProtocol.DownloadPath}/{file}" : null;
        string GitHubLink(string file) => $"{baseUrl}/{tag}/{file}";

        var assets = AgentDownloadAssets.Executables.Select(executable =>
        {
            var (file, problem) = Check(executable);
            var failure = failures.TryGetValue(executable, out var f) ? f.Reason : null;
            var state = file is not null ? AgentDownloadState.Here
                : fetches.ContainsKey(executable) ? AgentDownloadState.Fetching
                : problem is not null || failure is not null ? AgentDownloadState.Failed
                : AgentDownloadState.NotFetched;
            return new AgentDownloadFile(executable, AgentDownloadAssets.PlatformOf(executable), ArchitectureOf(executable), PnmtLink(executable),
                GitHubLink(executable), file?.Sha256 ?? sums.GetValueOrDefault(executable), file?.Length, state,
                state is AgentDownloadState.Failed ? problem ?? failure : null, fetched.TryGetValue(executable, out var at) ? at : null);
        }).ToList();

        var windows = Architectures.Select(a =>
        {
            var name = AgentDownloadAssets.For(AgentPlatform.Windows, a);
            var asset = assets.Single(x => x.Name == name);
            return new AgentDownloadLink(a, name, asset.GitHubUrl, sums.GetValueOrDefault(name) ?? asset.Sha256, asset.PnmtUrl);
        }).ToList();
        var linux = Architectures.Select(a =>
        {
            var name = AgentDownloadAssets.For(AgentPlatform.Linux, a);
            var asset = assets.Single(x => x.Name == name);
            // The hash PNMTD verified for its own link, the release's for GitHub's: the same unless the folder holds other files.
            var local = asset.State == AgentDownloadState.Here ? asset.Sha256 : sums.GetValueOrDefault(name);
            var release = sums.GetValueOrDefault(name) ?? local;
            return new LinuxInstallLines(a, name, local ?? release,
                asset.PnmtUrl is { } p && local is not null ? InstallLine(p, local, pnmtUrl, TokenPlaceholder, curl: false) : null,
                asset.PnmtUrl is { } q && local is not null ? InstallLine(q, local, pnmtUrl, TokenPlaceholder, curl: true) : null,
                release is not null ? InstallLine(asset.GitHubUrl, release, pnmtUrl, TokenPlaceholder, curl: false) : null,
                release is not null ? InstallLine(asset.GitHubUrl, release, pnmtUrl, TokenPlaceholder, curl: true) : null);
        }).ToList();

        var releaseUrl = baseUrl.EndsWith("/releases/download", StringComparison.Ordinal) ? $"{baseUrl[..^"/download".Length]}/tag/{tag}" : null;
        var unknown = assets.Where(a => a.Sha256 is null).Select(a => a.Name).ToList();
        var note = unknown.Count == 0 ? null
            : $"The SHA-256 of {string.Join(", ", unknown)} could not be read from GitHub or the download folder: compare with the .sha256 files of the release.";
        return new AgentDownloadLinks(version, windows, releaseUrl, note, assets, linux, UpdateLine, settings.ServeDownloads && settings.Enabled, pnmtUrl,
            TokenPlaceholder);
    }

    /// <summary>
    /// The one line of §4.4: download, check the hash before anything runs, install, and join. <c>--allow-http</c> for a PNMT on
    /// plain http://, which the agent asks to confirm otherwise.
    /// </summary>
    public static string InstallLine(string downloadUrl, string sha256, string pnmtUrl, string token, bool curl) =>
        $"{(curl ? "curl -fsSLo" : "wget -qO")} /tmp/pnmt-agent {downloadUrl} && echo \"{sha256}  /tmp/pnmt-agent\" | sha256sum -c --quiet"
        + $" && install -m 0755 /tmp/pnmt-agent /usr/local/sbin/pnmt-agent && rm /tmp/pnmt-agent && {JoinLine(pnmtUrl, token)}";

    /// <summary>The join on its own, for a binary installed another way: <c>pnmt-agent join --url … --token …</c>.</summary>
    public static string JoinLine(string pnmtUrl, string token) =>
        $"pnmt-agent join --url {pnmtUrl} --token {token}{(pnmtUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? " --allow-http" : "")}";

    private async Task RefreshAsync(string baseUrl, string version, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var client = http.CreateClient(nameof(AgentDownloads));
            client.Timeout = TimeSpan.FromSeconds(5);
            async Task<(string File, string? Sum)> ReadAsync(string executable)
            {
                try
                {
                    var text = await client.GetStringAsync($"{baseUrl}/{ReleaseTag(version)}/{executable}.sha256", ct);
                    return (executable, Sha256().Match(text) is { Success: true } m ? m.Value.ToLowerInvariant() : null);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    logger.LogDebug("The agent's checksum of {File} could not be read: {Message}", executable, e.Message);
                    return (executable, null);
                }
            }
            var sums = (await Task.WhenAll(AgentDownloadAssets.Executables.Select(ReadAsync))).ToDictionary(x => x.File, x => x.Sum, StringComparer.Ordinal);
            cache = (Clock.UtcNow, baseUrl, sums);
        }
        finally
        {
            gate.Release();
        }
    }

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b")]
    private static partial Regex Sha256();

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})(?:\s+(?<name>\S.*))?$")]
    private static partial Regex SumLine();
}
