using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Services.Domain;

namespace PNMTD.Agents;

/// <summary>
/// The agents' versions (docs/agent-update-plan.md §3, §4, §8.1, §9): the version each platform's agents get, kept in
/// <c>agent_settings</c> (null: PNMTD's own); the versions here and in GitHub's list, with the agents running each; fetching,
/// uploading (admins), downloading and deleting a version. <b>Nothing here reaches an agent</b> (§7): a choice changes what an
/// update on a machine installs, never when it runs.
/// </summary>
public sealed class AgentVersionService(
    PnmtdDbContext db,
    AgentKeyStore keys,
    AgentDownloads downloads,
    AgentReleaseCatalog catalog,
    IOptionsMonitor<AgentOptions> options,
    IHttpContextAccessor accessor)
{
    /// <summary>The versions the platforms' agents get, read from <c>agent_settings</c>; also given to <see cref="AgentDownloads.Remember"/>.</summary>
    public async Task<AgentChosenVersions> ChosenAsync(CancellationToken ct)
    {
        var settings = await keys.SettingsAsync(db, ct);
        var chosen = Resolve(settings.WindowsAgentVersion, settings.LinuxAgentVersion);
        downloads.Remember(chosen);
        return chosen;
    }

    /// <summary>The stored choices as versions: empty or null is PNMTD's own.</summary>
    public static AgentChosenVersions Resolve(string? windows, string? linux)
    {
        static string? Set(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return new AgentChosenVersions(Set(windows) ?? ServerInfo.Version, Set(linux) ?? ServerInfo.Version, Set(windows) is null, Set(linux) is null);
    }

    /// <summary>
    /// Whether an agent is outdated (docs/agent-update-plan.md §4): older than the version its platform's agents get. A version
    /// that can't be read is not.
    /// </summary>
    public static bool IsOutdated(string? agentVersion, string chosen) =>
        Readable(agentVersion) is { } agent && AgentVersionOrder.IsValid(chosen) && AgentVersionOrder.Compare(agent, chosen) < 0;

    /// <summary>Whether an agent is newer than the version its platform's agents get: after a version was held back.</summary>
    public static bool IsAhead(string? agentVersion, string chosen) =>
        Readable(agentVersion) is { } agent && AgentVersionOrder.IsValid(chosen) && AgentVersionOrder.Compare(agent, chosen) > 0;

    private static string? Readable(string? version) => version?.Split('+')[0].Trim() is { } v && AgentVersionOrder.IsValid(v) ? v : null;

    // --- the list ---------------------------------------------------------------------------------------------

    /// <summary>The <i>Versions</i> tab (§8.1). <paramref name="refresh"/>: GitHub's list read again.</summary>
    public async Task<AgentVersionList> ListAsync(bool refresh, CancellationToken ct)
    {
        var chosen = await ChosenAsync(ct);
        if (refresh)
        {
            catalog.Forget();
        }
        var (releases, state) = await catalog.GetAsync(ct);
        var byVersion = releases.ToDictionary(r => r.Version, StringComparer.Ordinal);
        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.RevokedAt == null && a.UninstalledAt == null && AgentPlatformInfo.SensorTypes.Contains(a.Sensor.Type))
            .Select(a => new { a.Platform, a.AgentVersion }).ToListAsync(ct);
        var running = agents.Select(a => (a.Platform, Version: Readable(a.AgentVersion))).ToList();
        int Running(AgentPlatform platform, string version) => running.Count(a => a.Platform == platform && a.Version == version);

        var versions = downloads.VersionsHere().Concat(releases.Select(r => r.Version)).Append(chosen.Windows).Append(chosen.Linux).Append(ServerInfo.Version)
            .Where(AgentVersionOrder.IsValid).Distinct(StringComparer.Ordinal).OrderByDescending(v => v, Comparer<string>.Create(AgentVersionOrder.Compare)).ToList();
        var baseUrl = options.CurrentValue.AgentDownloadBaseUrl;
        var items = versions.Select(version =>
        {
            var release = byVersion.GetValueOrDefault(version);
            var files = AgentDownloadAssets.Executables.Select(executable =>
            {
                var (fileState, error, fetchedAt, file) = downloads.StateOf(version, executable);
                var origin = file is not null ? downloads.OriginOf(version, file) : null;
                return new AgentVersionFile(executable, AgentDownloadAssets.PlatformOf(executable), AgentDownloads.ArchitectureOf(executable), fileState,
                    release is not null ? release.Assets.Contains(executable) : state.Available ? false : null, file?.Sha256, file?.Length, error,
                    origin?.Source, origin?.At ?? fetchedAt, origin?.By);
            }).ToList();
            var forWindows = chosen.Windows == version;
            var forLinux = chosen.Linux == version;
            var here = files.Any(f => f.State == AgentDownloadState.Here);
            var refused = forWindows || forLinux
                ? $"{(forWindows && forLinux ? "Windows and Linux" : forWindows ? "Windows" : "Linux")} agents get this version: choose another one first."
                : downloads.IsFetching(version) ? "It is being fetched." : !Directory.Exists(downloads.VersionFolder(version)) ? "None of its files are here." : null;
            return new AgentVersionItem(version, release?.PreRelease ?? AgentVersionOrder.IsPreRelease(version), release is not null, release?.PublishedAt,
                release?.Url ?? (release is not null ? AgentDownloads.ReleaseUrl(baseUrl, version) : null), here, files,
                Running(AgentPlatform.Windows, version), Running(AgentPlatform.Linux, version), forWindows, forLinux, version == ServerInfo.Version,
                AgentVersionOrder.Compare(version, ServerInfo.Version) > 0, downloads.HasUpload(version), refused);
        }).ToList();

        AgentChosenVersion Chosen(AgentPlatform platform)
        {
            var version = chosen.For(platform);
            var mine = agents.Where(a => a.Platform == platform).ToList();
            return new AgentChosenVersion(platform, version, platform == AgentPlatform.Linux ? chosen.LinuxFollows : chosen.WindowsFollows,
                AgentVersionOrder.Compare(version, ServerInfo.Version) > 0, AgentVersionOrder.IsPreRelease(version), mine.Count, Running(platform, version),
                mine.Count(a => IsOutdated(a.AgentVersion, version)), mine.Count(a => IsAhead(a.AgentVersion, version)));
        }
        var settings = options.CurrentValue;
        return new AgentVersionList(ServerInfo.Version, Chosen(AgentPlatform.Windows), Chosen(AgentPlatform.Linux), items, state, downloads.Folder,
            downloads.FolderBytes(), Math.Max(0, settings.KeepVersions), settings.ServeDownloads && settings.Enabled);
    }

    // --- choosing (§4) ------------------------------------------------------------------------------------------

    /// <summary>
    /// The versions the platforms' agents get. Each must be here (a file of its platform verified) or in GitHub's list; one newer
    /// than PNMTD needs <see cref="AgentVersionChooseRequest.ConfirmNewer"/>. A version not here yet is fetched in the background.
    /// Audited. Starts no update (§7).
    /// </summary>
    public async Task<AgentVersionList> ChooseAsync(AgentVersionChooseRequest request, CancellationToken ct)
    {
        static string? Set(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var windows = Set(request.Windows);
        var linux = Set(request.Linux);
        new ApiValidator()
            .Check(windows is null || AgentVersionOrder.IsValid(windows), "windows", "A version as 1.8.0 or 1.8.0-rc1, or empty for PNMT's own.")
            .Check(linux is null || AgentVersionOrder.IsValid(linux), "linux", "A version as 1.8.0 or 1.8.0-rc1, or empty for PNMT's own.")
            .ThrowIfInvalid();
        var (releases, _) = await catalog.GetAsync(ct);
        foreach (var (platform, version) in new[] { (AgentPlatform.Windows, windows), (AgentPlatform.Linux, linux) })
        {
            if (version is null || version == ServerInfo.Version)
            {
                continue;
            }
            var here = AgentDownloads.Of(platform).Any(e => downloads.Check(version, e).File is not null);
            if (!here && !releases.Any(r => r.Version == version))
            {
                throw ApiException.Invalid(platform == AgentPlatform.Linux ? "linux" : "windows",
                    $"{version} is neither here nor in GitHub's list: fetch or upload its {platform} files first.");
            }
        }
        var newer = new[] { windows, linux }.OfType<string>().Where(v => AgentVersionOrder.Compare(v, ServerInfo.Version) > 0).Distinct().ToList();
        if (newer.Count > 0 && !request.ConfirmNewer)
        {
            throw ApiException.Conflict("newer_than_pnmt", NewerWarning(newer[0]));
        }

        var settings = await keys.SettingsAsync(db, ct);
        var before = Resolve(settings.WindowsAgentVersion, settings.LinuxAgentVersion);
        settings.WindowsAgentVersion = windows;
        settings.LinuxAgentVersion = linux;
        var after = Resolve(windows, linux);
        UdpViews.Audit(db, accessor, AuditEvents.AgentVersionChosen,
            $"Windows agents get {Describe(after.Windows, after.WindowsFollows)} (were {Describe(before.Windows, before.WindowsFollows)}); "
            + $"Linux agents get {Describe(after.Linux, after.LinuxFollows)} (were {Describe(before.Linux, before.LinuxFollows)})"
            + (newer.Count > 0 ? $"; newer than PNMT {ServerInfo.Version}, confirmed" : ""));
        await db.SaveChangesAsync(ct);
        downloads.Remember(after);
        if (options.CurrentValue is { Enabled: true, ServeDownloads: true })
        {
            downloads.FetchAhead(after.Windows, AgentDownloads.Of(AgentPlatform.Windows));
            downloads.FetchAhead(after.Linux, AgentDownloads.Of(AgentPlatform.Linux));
        }
        return await ListAsync(false, ct);
    }

    /// <summary>The warning for a version newer than PNMTD (§4): asked before it is chosen.</summary>
    public static string NewerWarning(string version) =>
        $"{version} is newer than this PNMT ({ServerInfo.Version}). Its agents may report what this PNMT doesn't show yet, and if the release "
        + "changed the agents' protocol they can't connect until PNMT is updated. Update PNMT first if you can.";

    private static string Describe(string version, bool follows) => follows ? $"PNMT's version ({version})" : version;

    // --- fetching, uploading, downloading, deleting (§3.3, §3.5) --------------------------------------------------

    /// <summary>Fetches files of a version from its GitHub release: those named, or every one that isn't here and verified. Answers at once. Audited.</summary>
    public async Task<AgentVersionList> FetchAsync(string version, AgentVersionFetchRequest request, CancellationToken ct)
    {
        Validate(version);
        var names = (request.Files ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).Distinct().ToList();
        new ApiValidator()
            .Check(names.All(AgentDownloadAssets.Executables.Contains), "files", "Only the agents' files: " + string.Join(", ", AgentDownloadAssets.Executables) + ".")
            .ThrowIfInvalid();
        if (!options.CurrentValue.Enabled)
        {
            throw ApiException.Conflict("agents_off", "The agents are turned off (Agents__Enabled).");
        }
        await ChosenAsync(ct);
        var files = names.Count > 0 ? names : AgentDownloadAssets.Executables.Where(e => downloads.Check(version, e).File is null).ToList();
        downloads.FetchAhead(version, files, again: true);
        UdpViews.Audit(db, accessor, AuditEvents.AgentVersionFetched,
            files.Count == 0 ? $"{version}: every file is here and verified" : $"{version}: {string.Join(", ", files)} from {AgentDownloads.ReleaseTag(version)}");
        await db.SaveChangesAsync(ct);
        return await ListAsync(false, ct);
    }

    /// <summary>An admin's file for a version (§3.5): checked, kept, marked as uploaded by them. Audited with its hash.</summary>
    public async Task<AgentVersionList> UploadAsync(string version, IFormFile? file, string? name, string? sha256, CancellationToken ct)
    {
        Validate(version);
        if (file is null || file.Length == 0)
        {
            throw ApiException.Invalid("file", "The agent's executable.");
        }
        var executable = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(file.FileName) : name.Trim();
        new ApiValidator()
            .Check(AgentDownloadAssets.Executables.Contains(executable), "name",
                $"The file's name must be one of the agents' files: {string.Join(", ", AgentDownloadAssets.Executables)}.")
            .Check(file.Length <= AgentDownloads.MaxBytes, "file", $"At most {AgentDownloads.MaxBytes / 1024 / 1024} MB.")
            .Check(sha256 is null or "" || System.Text.RegularExpressions.Regex.IsMatch(sha256.Trim(), "^[0-9a-fA-F]{64}$"), "sha256", "64 hex digits, or empty.")
            .ThrowIfInvalid();
        var (_, by) = UdpViews.User(accessor);
        VerifiedFile stored;
        try
        {
            await using var content = file.OpenReadStream();
            stored = await downloads.StoreUploadAsync(version, executable, content, sha256, by, ct);
        }
        catch (ArgumentException e)
        {
            throw ApiException.Invalid("file", e.Message);
        }
        UdpViews.Audit(db, accessor, AuditEvents.AgentVersionUploaded,
            $"{executable} of {version} uploaded: {stored.Length / 1024 / 1024.0:0.0} MB, SHA-256 {stored.Sha256}");
        await db.SaveChangesAsync(ct);
        return await ListAsync(false, ct);
    }

    /// <summary>A file of a version that is here and verified, or its <c>.sha256</c>, for the browser.</summary>
    public (Stream Content, string Name) Open(string version, string file)
    {
        Validate(version);
        if (!AgentDownloadAssets.IsKnown(file))
        {
            throw ApiException.NotFound("File");
        }
        var sum = file.EndsWith(".sha256", StringComparison.Ordinal);
        var executable = sum ? file[..^".sha256".Length] : file;
        var verified = downloads.Check(version, executable).File ?? throw ApiException.NotFound("File");
        if (sum)
        {
            return (new MemoryStream(Encoding.UTF8.GetBytes($"{verified.Sha256}  {executable}\n")), file);
        }
        return (AgentDownloads.OpenVerified(verified) ?? throw ApiException.Conflict("file_changed", $"{file} changed while it was checked: try again."), file);
    }

    /// <summary>Deletes a version's folder: refused while a platform's agents get it (§3.3). Audited.</summary>
    public async Task DeleteAsync(string version, CancellationToken ct)
    {
        Validate(version);
        var chosen = await ChosenAsync(ct);
        if (chosen.Windows == version || chosen.Linux == version)
        {
            throw ApiException.Conflict("chosen_version", $"{(chosen.Windows == version ? "Windows" : "Linux")} agents get {version}: choose another version first.");
        }
        if (!Directory.Exists(downloads.VersionFolder(version)))
        {
            throw ApiException.NotFound("Version");
        }
        var uploaded = downloads.HasUpload(version);
        try
        {
            downloads.DeleteVersion(version);
        }
        catch (InvalidOperationException e)
        {
            throw ApiException.Conflict("being_fetched", e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw ApiException.Conflict("not_deleted", $"{version}'s folder could not be deleted: {e.Message}");
        }
        UdpViews.Audit(db, accessor, AuditEvents.AgentVersionDeleted, $"{version} deleted from the download folder{(uploaded ? " (with uploaded files)" : "")}");
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(string version)
    {
        if (!AgentVersionOrder.IsValid(version))
        {
            throw ApiException.Invalid("version", "A version as 1.8.0 or 1.8.0-rc1.");
        }
    }
}
