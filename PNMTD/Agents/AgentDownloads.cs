using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;

namespace PNMTD.Agents;

/// <summary>A file in the download folder that matches its <c>.sha256</c>: what may be served, and what the manifest lists.</summary>
public sealed record VerifiedFile(string Name, string Path, long Length, DateTime ModifiedAt, string Sha256);

/// <summary>Where a file of a version came from (docs/agent-update-plan.md §3.1), kept next to it in <see cref="AgentDownloads.OriginsFile"/>.</summary>
public sealed record AgentFileOrigin(AgentFileSource Source, DateTime At, string? By);

/// <summary>The versions each platform's agents get (docs/agent-update-plan.md §4): chosen, or PNMTD's own.</summary>
public sealed record AgentChosenVersions(string Windows, string Linux, bool WindowsFollows = true, bool LinuxFollows = true)
{
    /// <summary>PNMTD's own version for both, as before 1.8 and while nothing is chosen.</summary>
    public static AgentChosenVersions Pnmt => new(ServerInfo.Version, ServerInfo.Version);

    public string For(AgentPlatform platform) => platform == AgentPlatform.Linux ? Linux : Windows;
}

/// <summary>
/// The agents' files (docs/linux-sensor-plan.md §4.4, §9.4, §9.5, §9.7; docs/windows-sensor-plan.md §10.1; docs/agent-update-plan.md
/// §3, §5), of several versions:
/// <list type="bullet">
/// <item>the <b>download folder</b> (<see cref="AgentOptions.DownloadFolder"/>) holds a folder <c>sensor-{version}/</c> per version. A
/// file is served only while it matches its <c>.sha256</c> (<c>sha256sum</c>'s format), checked by size and time and its hash kept.
/// Files in the folder itself (an admin's for a PNMT without internet, or <c>dist/</c>) count as PNMTD's own version when its
/// folder lacks them;</item>
/// <item>a file that isn't there is <b>fetched from the GitHub release of its version</b> (the sensor tag,
/// <c>{AgentDownloadBaseUrl}/sensor-{version}/{file}</c>) when it is first asked for, checked against the release's <c>.sha256</c>
/// and kept. Requests meanwhile wait for the one fetch; a failed fetch is remembered for 10 minutes, as the release's hashes are.
/// A file that is there and doesn't match is not replaced, except by <i>Fetch again</i>;</item>
/// <item>an admin's <b>upload</b>, checked (name, size, the executable's header and architecture) and kept with its hash;</item>
/// <item><b>where each file came from</b>, in the version's folder (<see cref="OriginsFile"/>): the manifest says so, and the
/// <i>Versions</i> tab;</item>
/// <item><b>what is kept</b> (<see cref="Retain"/>): the chosen versions, PNMTD's own, versions with an upload, and the newest
/// <see cref="AgentOptions.KeepVersions"/> others;</item>
/// <item>the release's hashes (its <c>.sha256</c> files, fetched and cached per version) for the links and the install lines of
/// files that aren't here yet;</item>
/// <item>the <b>manifest</b> of a platform's chosen version, which <see cref="AgentDownloadEndpoint"/> signs;</item>
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

    /// <summary>A file larger than this is no agent: neither fetched, uploaded nor served.</summary>
    public const long MaxBytes = 300L * 1024 * 1024;

    /// <summary>A failed fetch is not tried again before this, except by <i>Fetch again</i>.</summary>
    public static readonly TimeSpan FailedFor = TimeSpan.FromMinutes(10);

    /// <summary>Where a version's files came from, in its folder: never served (not one of <see cref="AgentDownloadAssets.All"/>).</summary>
    public const string OriginsFile = "agent-version.json";

    /// <summary>Windows' executable for an architecture, as before L3.</summary>
    public static string FileName(string architecture) => AgentDownloadAssets.For(AgentPlatform.Windows, architecture);

    /// <summary>The tag whose release has the agents (.github/workflows/sensor.yml); version tags (v1.2.0) publish only the image.</summary>
    public static string ReleaseTag(string version) => $"sensor-{version}";

    public static string ArchitectureOf(string executable) => executable.Contains("arm64", StringComparison.Ordinal) ? "arm64" : "x64";

    private static readonly JsonSerializerOptions OriginsJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock originsLock = new();
    private readonly ConcurrentDictionary<string, (DateTime At, string Base, Dictionary<string, string?> Sums)> sums = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> refreshing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (long Length, DateTime ModifiedAt, string Sha256)> hashes = new(StringComparer.Ordinal);
    // Fetches, failures and fetch times by "{version}/{file}".
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> fetches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, string Reason)> failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> fetched = new(StringComparer.Ordinal);
    private volatile AgentChosenVersions? chosen;

    /// <summary>The download folder, absolute; a relative one (<c>dist</c>) is the working directory's.</summary>
    public string Folder => Path.GetFullPath(string.IsNullOrWhiteSpace(options.CurrentValue.DownloadFolder) ? "/var/lib/pnmtd/downloads" : options.CurrentValue.DownloadFolder);

    /// <summary>
    /// Where a version's files are: <c>sensor-{version}/</c> in the download folder, which is a volume in the compose deployment and
    /// so outlives an update of PNMT.
    /// </summary>
    public string VersionFolder(string version) => Path.Combine(Folder, ReleaseTag(Valid(version)));

    private static string Key(string version, string executable) => $"{version}/{executable}";

    private static string Valid(string version) =>
        AgentVersionOrder.IsValid(version) ? version : throw new ArgumentException($"{version} is no agent version.", nameof(version));

    /// <summary>
    /// The chosen versions last read (by the API, the endpoints and the maintenance), for <see cref="Retain"/> after a fetch: a
    /// fetch must not delete what agents get.
    /// </summary>
    public void Remember(AgentChosenVersions versions) => chosen = versions;

    // --- the folder ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A version's executable and its <c>.sha256</c> in the folder: verified, or why not (a problem), or neither when either is
    /// missing. PNMTD's own version also finds the files in the folder itself. Hashed again only when its size or time changed.
    /// </summary>
    public (VerifiedFile? File, string? Problem) Check(string version, string executable)
    {
        var inVersion = CheckIn(VersionFolder(version), executable);
        return inVersion.File is not null || inVersion.Problem is not null || version != ServerInfo.Version ? inVersion : CheckIn(Folder, executable);
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

    /// <summary>The versions with a folder here (<c>sensor-{version}/</c>), newest first.</summary>
    public IReadOnlyList<string> VersionsHere()
    {
        try
        {
            if (!Directory.Exists(Folder))
            {
                return [];
            }
            return Directory.GetDirectories(Folder, "sensor-*").Select(d => Path.GetFileName(d)["sensor-".Length..]).Where(AgentVersionOrder.IsValid)
                .OrderByDescending(v => v, Comparer<string>.Create(AgentVersionOrder.Compare)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("The download folder {Folder} can't be read: {Message}", Folder, e.Message);
            return [];
        }
    }

    /// <summary>The files and folders under the download folder, in bytes.</summary>
    public long FolderBytes()
    {
        try
        {
            return Directory.Exists(Folder) ? new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // --- where the files came from ---------------------------------------------------------------------------

    /// <summary>The origins kept in a version's folder, by file name; none for a folder from before 1.8.</summary>
    public IReadOnlyDictionary<string, AgentFileOrigin> Origins(string version)
    {
        var path = Path.Combine(VersionFolder(version), OriginsFile);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024)
            {
                return new Dictionary<string, AgentFileOrigin>();
            }
            var origins = JsonSerializer.Deserialize<Dictionary<string, AgentFileOrigin>>(File.ReadAllText(path), OriginsJson);
            return origins?.Where(o => AgentDownloadAssets.Executables.Contains(o.Key)).ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, AgentFileOrigin>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger.LogWarning("{File} of {Version} can't be read: {Message}", OriginsFile, version, e.Message);
            return new Dictionary<string, AgentFileOrigin>();
        }
    }

    /// <summary>
    /// Where a verified file of a version came from: the folder itself (outside <c>sensor-{version}/</c>), what its version's folder
    /// keeps, or GitHub (what PNMTD fetched before 1.8 kept no origin).
    /// </summary>
    public AgentFileOrigin OriginOf(string version, VerifiedFile file) =>
        Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(file.Path) ?? "") == Path.TrimEndingDirectorySeparator(Folder)
            ? new AgentFileOrigin(AgentFileSource.Folder, file.ModifiedAt, null)
        : Origins(version).GetValueOrDefault(file.Name) ?? new AgentFileOrigin(AgentFileSource.GitHub, file.ModifiedAt, null);

    /// <summary>The origin in words, for the manifest and so for the machine's update (§6.2, step 5).</summary>
    public static string Describe(string version, AgentFileOrigin origin) => origin.Source switch
    {
        AgentFileSource.Upload => $"uploaded to PNMT{(origin.By is { Length: > 0 } by ? $" by {by}" : "")} on {origin.At:yyyy-MM-dd}",
        AgentFileSource.Folder => "PNMT's download folder",
        _ => $"GitHub's release {ReleaseTag(version)}",
    };

    private void SetOrigin(string version, string executable, AgentFileOrigin origin)
    {
        lock (originsLock)
        {
            var all = Origins(version).ToDictionary(o => o.Key, o => o.Value, StringComparer.Ordinal);
            all[executable] = origin;
            var path = Path.Combine(VersionFolder(version), OriginsFile);
            var temporary = $"{path}.{Guid.NewGuid():N}.part";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, OriginsJson));
            File.Move(temporary, path, overwrite: true);
        }
    }

    // --- fetching ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A version's executable for a download: verified, fetched first when it isn't in the folder (waiting for the fetch that is
    /// running), or why it can't be served.
    /// </summary>
    public async Task<(VerifiedFile? File, string? Problem)> PrepareAsync(string version, string executable, CancellationToken ct)
    {
        var (file, problem) = Check(version, executable);
        if (file is not null || problem is not null)
        {
            return (file, problem);
        }
        if (RecentFailure(version, executable) is { } failed)
        {
            return (null, failed);
        }
        var error = await StartFetch(version, executable).WaitAsync(ct);
        return error is null ? Check(version, executable) : (null, error);
    }

    /// <summary>
    /// Starts fetching these executables of a version when they aren't here (<paramref name="again"/>: also those that don't match,
    /// and those that failed within <see cref="FailedFor"/>): the Add server dialog asks ahead, <i>Fetch again</i> asks again.
    /// Never waits.
    /// </summary>
    public IReadOnlyList<Task<string?>> FetchAhead(string version, IEnumerable<string> executables, bool again = false)
    {
        var started = new List<Task<string?>>();
        foreach (var executable in executables.Where(AgentDownloadAssets.Executables.Contains))
        {
            var (file, problem) = Check(version, executable);
            if (file is not null || (problem is not null && !again) || (!again && RecentFailure(version, executable) is not null))
            {
                continue;
            }
            if (again)
            {
                failures.TryRemove(Key(version, executable), out _);
            }
            started.Add(StartFetch(version, executable));
        }
        return started;
    }

    /// <summary>Whether a file of a version is being fetched now.</summary>
    public bool IsFetching(string version, string? executable = null) =>
        executable is null ? fetches.Keys.Any(k => k.StartsWith(version + "/", StringComparison.Ordinal)) : fetches.ContainsKey(Key(version, executable));

    /// <summary>The executables of a platform.</summary>
    public static IReadOnlyList<string> Of(AgentPlatform platform) =>
        AgentDownloadAssets.Executables.Where(e => AgentDownloadAssets.PlatformOf(e) == platform).ToList();

    /// <summary>A file's state here: verified, being fetched, failed (with the reason), or not fetched; and when PNMTD fetched it.</summary>
    public (AgentDownloadState State, string? Error, DateTime? FetchedAt, VerifiedFile? File) StateOf(string version, string executable)
    {
        var (file, problem) = Check(version, executable);
        var key = Key(version, executable);
        var failure = failures.TryGetValue(key, out var f) ? f.Reason : null;
        var state = file is not null ? AgentDownloadState.Here
            : fetches.ContainsKey(key) ? AgentDownloadState.Fetching
            : problem is not null || failure is not null ? AgentDownloadState.Failed
            : AgentDownloadState.NotFetched;
        return (state, state is AgentDownloadState.Failed ? problem ?? failure : null, fetched.TryGetValue(key, out var at) ? at : null, file);
    }

    private string? RecentFailure(string version, string executable) =>
        failures.TryGetValue(Key(version, executable), out var failure) && Clock.UtcNow - failure.At < FailedFor ? failure.Reason : null;

    /// <summary>One fetch per file at a time: a second request waits for the first.</summary>
    private Task<string?> StartFetch(string version, string executable)
    {
        var key = Key(version, executable);
        var lazy = fetches.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => Task.Run(() => FetchAsync(version, executable))));
        var task = lazy.Value;
        _ = task.ContinueWith(_ => fetches.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(key, lazy)), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>
    /// Fetches a version's executable and its <c>.sha256</c> from the release into its folder: the hash first, then the file into a
    /// temporary name, hashed while it is written, and renamed into place only when it matches. Null when it worked, else why not.
    /// </summary>
    private async Task<string?> FetchAsync(string version, string executable)
    {
        var baseUrl = options.CurrentValue.AgentDownloadBaseUrl.TrimEnd('/');
        var tag = ReleaseTag(version);
        var url = $"{baseUrl}/{tag}/{executable}";
        string? temporary = null;
        string? temporarySum = null;
        try
        {
            var folder = VersionFolder(version);
            using var client = http.CreateClient(nameof(AgentDownloads));
            client.Timeout = Timeout.InfiniteTimeSpan;
            string expected;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                using var response = await client.GetAsync(url + ".sha256", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return Failed(version, executable, $"the release {tag} answered {(int)response.StatusCode} {response.ReasonPhrase} for {executable}.sha256");
                }
                var text = await ReadTextAsync(response, 4096, timeout.Token);
                if (ParseSum(text, executable, out var problem) is not { } sum)
                {
                    return Failed(version, executable, $"the release's {problem}");
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
                return Failed(version, executable, $"the download folder {folder} can't be written: {e.Message}");
            }
            string actual;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return Failed(version, executable, $"the release {tag} answered {(int)response.StatusCode} {response.ReasonPhrase} for {executable}");
                }
                if (response.Content.Headers.ContentLength > MaxBytes)
                {
                    return Failed(version, executable, $"the release's {executable} is larger than {MaxBytes / 1024 / 1024} MB");
                }
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                var (hash, tooLarge) = await CopyHashedAsync(input, temporary, timeout.Token);
                if (tooLarge)
                {
                    return Failed(version, executable, $"the release's {executable} is larger than {MaxBytes / 1024 / 1024} MB");
                }
                actual = hash;
            }
            if (actual != expected)
            {
                return Failed(version, executable, $"the {executable} the release sent does not match its .sha256: not kept");
            }
            temporarySum = Path.Combine(folder, $".{executable}.sha256.{Guid.NewGuid():N}.part");
            await File.WriteAllTextAsync(temporarySum, $"{expected}  {executable}\n");
            File.Move(temporarySum, Path.Combine(folder, executable + ".sha256"), overwrite: true);
            temporarySum = null;
            File.Move(temporary, Path.Combine(folder, executable), overwrite: true);
            temporary = null;
            SetOrigin(version, executable, new AgentFileOrigin(AgentFileSource.GitHub, Clock.UtcNow, null));
            failures.TryRemove(Key(version, executable), out _);
            fetched[Key(version, executable)] = Clock.UtcNow;
            logger.LogInformation("Fetched {File} of {Version} from {Url} into {Folder}: SHA-256 {Sha256}", executable, version, url, folder, expected);
            if (chosen is { } now)
            {
                Retain(now, version);
            }
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            return Failed(version, executable, e is OperationCanceledException ? $"fetching {url} timed out" : $"fetching {url} failed: {e.Message}");
        }
        finally
        {
            Delete(temporary, temporarySum);
        }
    }

    /// <summary>Copies into a file while hashing it; stops beyond <see cref="MaxBytes"/>.</summary>
    private static async Task<(string Sha256, bool TooLarge)> CopyHashedAsync(Stream input, string path, CancellationToken ct)
    {
        await using var output = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                return ("", true);
            }
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return (Convert.ToHexStringLower(sha.GetHashAndReset()), false);
    }

    private string Failed(string version, string executable, string reason)
    {
        failures[Key(version, executable)] = (Clock.UtcNow, reason);
        logger.LogWarning("The agent's file {File} of {Version} could not be fetched: {Reason}", executable, version, reason);
        return reason;
    }

    private static void Delete(params string?[] paths)
    {
        foreach (var leftover in paths.OfType<string>())
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

    // --- uploading (docs/agent-update-plan.md §3.5) -----------------------------------------------------------------------

    /// <summary>
    /// An admin's file for a version: one of the four names, at most <see cref="MaxBytes"/>, an executable of its platform and
    /// architecture (<see cref="HeaderProblem"/>), and, with <paramref name="expectedSha256"/>, that hash. Written to a temporary
    /// name, then its <c>.sha256</c> and the file renamed into place, replacing what was there; its origin kept. The verified
    /// file, or <see cref="ArgumentException"/> with why not.
    /// </summary>
    public async Task<VerifiedFile> StoreUploadAsync(string version, string executable, Stream content, string? expectedSha256, string? by, CancellationToken ct)
    {
        if (!AgentDownloadAssets.Executables.Contains(executable))
        {
            throw new ArgumentException($"Only the agents' files: {string.Join(", ", AgentDownloadAssets.Executables)}.");
        }
        if (IsFetching(version, executable))
        {
            throw new ArgumentException($"{executable} of {version} is being fetched: try again when it is done.");
        }
        var folder = VersionFolder(version);
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{executable}.{Guid.NewGuid():N}.part");
        string? temporarySum = null;
        try
        {
            await using (File.Create(temporary))
            {
            }
            var (sha256, tooLarge) = await CopyHashedAsync(content, temporary, ct);
            if (tooLarge)
            {
                throw new ArgumentException($"{executable} is larger than {MaxBytes / 1024 / 1024} MB: no agent.");
            }
            var head = new byte[4096];
            int length;
            await using (var file = File.OpenRead(temporary))
            {
                length = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
            }
            if (HeaderProblem(executable, head.AsSpan(0, length)) is { } problem)
            {
                throw new ArgumentException($"The file {problem}: not {executable}.");
            }
            if (expectedSha256 is { Length: > 0 } wanted && !string.Equals(wanted.Trim(), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The file's SHA-256 is {sha256}, not {wanted.Trim()}: not kept.");
            }
            temporarySum = Path.Combine(folder, $".{executable}.sha256.{Guid.NewGuid():N}.part");
            await File.WriteAllTextAsync(temporarySum, $"{sha256}  {executable}\n", ct);
            File.Move(temporarySum, Path.Combine(folder, executable + ".sha256"), overwrite: true);
            temporarySum = null;
            File.Move(temporary, Path.Combine(folder, executable), overwrite: true);
            SetOrigin(version, executable, new AgentFileOrigin(AgentFileSource.Upload, Clock.UtcNow, by));
            failures.TryRemove(Key(version, executable), out _);
            fetched.TryRemove(Key(version, executable), out _);
            logger.LogInformation("Uploaded {File} of {Version} by {By}: SHA-256 {Sha256}", executable, version, by, sha256);
            return Check(version, executable).File ?? throw new ArgumentException($"{executable} could not be checked after the upload.");
        }
        finally
        {
            Delete(temporary, temporarySum);
        }
    }

    /// <summary>
    /// Why these first bytes are not the executable named (null: they are): Windows' a PE image (<c>MZ</c>, <c>PE\0\0</c>) for the
    /// architecture's machine (x64 0x8664, arm64 0xAA64); Linux' a 64-bit ELF for x86-64 (62) or AArch64 (183).
    /// </summary>
    public static string? HeaderProblem(string executable, ReadOnlySpan<byte> head)
    {
        var arm64 = ArchitectureOf(executable) == "arm64";
        if (AgentDownloadAssets.PlatformOf(executable) == AgentPlatform.Windows)
        {
            if (head.Length < 64 || head[0] != 'M' || head[1] != 'Z')
            {
                return "is not a Windows executable (no MZ header)";
            }
            var pe = BinaryPrimitives.ReadInt32LittleEndian(head[0x3C..]);
            if (pe < 64 || pe > head.Length - 6 || !head.Slice(pe, 4).SequenceEqual("PE\0\0"u8))
            {
                return "is not a Windows executable (no PE header)";
            }
            var machine = BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 4)..]);
            var expected = arm64 ? 0xAA64 : 0x8664;
            return machine == expected ? null : $"is for another architecture (machine 0x{machine:X4}; {ArchitectureOf(executable)} is 0x{expected:X4})";
        }
        if (head.Length < 20 || !head[..4].SequenceEqual("\u007fELF"u8))
        {
            return "is not a Linux executable (no ELF header)";
        }
        if (head[4] != 2)
        {
            return "is not a 64-bit Linux executable";
        }
        var elfMachine = BinaryPrimitives.ReadUInt16LittleEndian(head[18..]);
        var elfExpected = arm64 ? 183 : 62;
        return elfMachine == elfExpected ? null : $"is for another architecture (ELF machine {elfMachine}; {ArchitectureOf(executable)} is {elfExpected})";
    }

    // --- deleting and keeping (§3.3, §3.4) ---------------------------------------------------------------------------

    /// <summary>Deletes a version's folder with its files; the caller checks that no platform gets it.</summary>
    public void DeleteVersion(string version)
    {
        if (IsFetching(version))
        {
            throw new InvalidOperationException($"{version} is being fetched: try again when it is done.");
        }
        var folder = VersionFolder(version);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
        foreach (var key in failures.Keys.Concat(fetched.Keys).Where(k => k.StartsWith(version + "/", StringComparison.Ordinal)).ToList())
        {
            failures.TryRemove(key, out _);
            fetched.TryRemove(key, out _);
        }
        sums.TryRemove(version, out _);
        logger.LogInformation("Deleted the agent files of {Version}: {Folder}", version, folder);
    }

    /// <summary>Whether a version has a file someone uploaded: it can't be fetched again, so it stays until deleted.</summary>
    public bool HasUpload(string version) => Origins(version).Values.Any(o => o.Source == AgentFileSource.Upload);

    /// <summary>
    /// What is kept (§3.4): the chosen versions, PNMTD's own, <paramref name="also"/>, versions with an upload, and the newest
    /// <see cref="AgentOptions.KeepVersions"/> others. The other versions' folders are deleted: they can be fetched again. Answers
    /// the versions deleted.
    /// </summary>
    public IReadOnlyList<string> Retain(AgentChosenVersions versions, string? also = null)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal) { versions.Windows, versions.Linux, ServerInfo.Version };
        if (also is not null)
        {
            keep.Add(also);
        }
        var others = VersionsHere().Where(v => !keep.Contains(v) && !IsFetching(v) && !HasUpload(v)).Skip(Math.Max(0, options.CurrentValue.KeepVersions)).ToList();
        var deleted = new List<string>();
        foreach (var old in others)
        {
            try
            {
                DeleteVersion(old);
                deleted.Add(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                logger.LogWarning("The agent files of {Version} could not be deleted: {Message}", old, e.Message);
            }
        }
        return deleted;
    }

    // --- the manifest (§4.4; docs/agent-update-plan.md §5.1) -------------------------------------------------------

    /// <summary>
    /// A platform's files of a version that are here and verified, with where they came from, for <c>GET /agent/v1/download</c>.
    /// The update asks for its platform's file: those that aren't here yet are fetched first, waited for at most 30 seconds.
    /// </summary>
    public async Task<DownloadManifest> ManifestAsync(string version, AgentPlatform platform, CancellationToken ct)
    {
        var fetching = FetchAhead(version, Of(platform));
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
        var assets = Of(platform).Select(e => Check(version, e).File).OfType<VerifiedFile>()
            .Select(f => new DownloadAsset(f.Name, f.Length, f.Sha256, Describe(version, OriginOf(version, f)))).ToList();
        return new DownloadManifest(version, assets, platform, AgentVersionOrder.IsPreRelease(version));
    }

    // --- the links (GET /api/v1/agents/downloads) ---------------------------------------------------------------

    /// <summary>
    /// Every file's links, hash, size and state, of its platform's chosen version; Windows' links as before L3; the Linux install
    /// and update lines with PNMT's URL (<paramref name="pnmtUrl"/>) and <see cref="TokenPlaceholder"/>. <paramref name="wait"/>:
    /// fetch the releases' hashes first when they are stale, else answer with what is cached.
    /// </summary>
    public async Task<AgentDownloadLinks> GetAsync(AgentChosenVersions versions, string pnmtUrl, CancellationToken ct, bool wait)
    {
        Remember(versions);
        var settings = options.CurrentValue;
        var baseUrl = settings.AgentDownloadBaseUrl.TrimEnd('/');
        var releaseSums = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var platform in new[] { AgentPlatform.Windows, AgentPlatform.Linux })
        {
            var known = await ReleaseSumsAsync(baseUrl, versions.For(platform), wait, ct);
            foreach (var executable in Of(platform))
            {
                releaseSums[executable] = known.GetValueOrDefault(executable);
            }
        }
        string VersionOf(string executable) => versions.For(AgentDownloadAssets.PlatformOf(executable));
        pnmtUrl = pnmtUrl.TrimEnd('/');
        string? PnmtLink(string file) => settings.ServeDownloads && settings.Enabled ? $"{pnmtUrl}{AgentProtocol.DownloadPath}/{file}" : null;
        string GitHubLink(string file) => $"{baseUrl}/{ReleaseTag(VersionOf(file))}/{file}";

        var assets = AgentDownloadAssets.Executables.Select(executable =>
        {
            var version = VersionOf(executable);
            var (state, error, fetchedAt, file) = StateOf(version, executable);
            return new AgentDownloadFile(executable, AgentDownloadAssets.PlatformOf(executable), ArchitectureOf(executable), PnmtLink(executable),
                GitHubLink(executable), file?.Sha256 ?? releaseSums.GetValueOrDefault(executable), file?.Length, state, error,
                fetchedAt ?? (file is not null ? OriginOf(version, file).At : null), version);
        }).ToList();

        var windows = Architectures.Select(a =>
        {
            var name = AgentDownloadAssets.For(AgentPlatform.Windows, a);
            var asset = assets.Single(x => x.Name == name);
            return new AgentDownloadLink(a, name, asset.GitHubUrl, releaseSums.GetValueOrDefault(name) ?? asset.Sha256, asset.PnmtUrl);
        }).ToList();
        var linux = Architectures.Select(a =>
        {
            var name = AgentDownloadAssets.For(AgentPlatform.Linux, a);
            var asset = assets.Single(x => x.Name == name);
            // The hash PNMTD verified for its own link, the release's for GitHub's: the same unless the folder holds other files.
            var local = asset.State == AgentDownloadState.Here ? asset.Sha256 : releaseSums.GetValueOrDefault(name);
            var release = releaseSums.GetValueOrDefault(name) ?? local;
            return new LinuxInstallLines(a, name, local ?? release,
                asset.PnmtUrl is { } p && local is not null ? InstallLine(p, local, pnmtUrl, TokenPlaceholder, curl: false) : null,
                asset.PnmtUrl is { } q && local is not null ? InstallLine(q, local, pnmtUrl, TokenPlaceholder, curl: true) : null,
                release is not null ? InstallLine(asset.GitHubUrl, release, pnmtUrl, TokenPlaceholder, curl: false) : null,
                release is not null ? InstallLine(asset.GitHubUrl, release, pnmtUrl, TokenPlaceholder, curl: true) : null);
        }).ToList();

        var unknown = assets.Where(a => a.Sha256 is null).Select(a => a.Name).ToList();
        var note = unknown.Count == 0 ? null
            : $"The SHA-256 of {string.Join(", ", unknown)} could not be read from GitHub or the download folder: compare with the .sha256 files of the release.";
        return new AgentDownloadLinks(ServerInfo.Version, windows, ReleaseUrl(baseUrl, versions.Windows), note, assets, linux, UpdateLine,
            settings.ServeDownloads && settings.Enabled, pnmtUrl, TokenPlaceholder, versions.Windows, versions.Linux, ReleaseUrl(baseUrl, versions.Linux));
    }

    /// <summary>The release's page on GitHub, when the base is GitHub's <c>…/releases/download</c>.</summary>
    public static string? ReleaseUrl(string baseUrl, string version)
    {
        baseUrl = baseUrl.TrimEnd('/');
        return baseUrl.EndsWith("/releases/download", StringComparison.Ordinal) ? $"{baseUrl[..^"/download".Length]}/tag/{ReleaseTag(version)}" : null;
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

    /// <summary>
    /// A release's hashes (its <c>.sha256</c> files) by file: cached 6 hours, 10 minutes when one could not be read.
    /// <paramref name="wait"/>: read them first when stale, else answer with what is cached and read them in the background.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string?>> ReleaseSumsAsync(string baseUrl, string version, bool wait, CancellationToken ct)
    {
        var stale = !sums.TryGetValue(version, out var c) || c.Base != baseUrl
            || Clock.UtcNow - c.At > (c.Sums.Values.Any(v => v is null) ? FailedFor : TimeSpan.FromHours(6));
        if (stale)
        {
            if (wait)
            {
                await RefreshAsync(baseUrl, version, ct);
            }
            else
            {
                _ = refreshing.GetOrAdd(version, v => Task.Run(async () =>
                {
                    try
                    {
                        await RefreshAsync(baseUrl, v, CancellationToken.None);
                    }
                    finally
                    {
                        refreshing.TryRemove(v, out _);
                    }
                }));
            }
        }
        return sums.TryGetValue(version, out var cached) && cached.Base == baseUrl ? cached.Sums : new Dictionary<string, string?>();
    }

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
                    logger.LogDebug("The agent's checksum of {File} of {Version} could not be read: {Message}", executable, version, e.Message);
                    return (executable, null);
                }
            }
            var read = (await Task.WhenAll(AgentDownloadAssets.Executables.Select(ReadAsync))).ToDictionary(x => x.File, x => x.Sum, StringComparer.Ordinal);
            sums[version] = (Clock.UtcNow, baseUrl, read);
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
