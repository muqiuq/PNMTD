using System.Security.Cryptography;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Linux.Platform;

namespace PNMTD.Sensor.Linux.Update;

/// <summary>
/// <c>pnmt-agent update [file]</c> (docs/linux-sensor-plan.md §4.1, §4.4). Without a file: PNMT's signed manifest
/// (<c>GET /agent/v1/download</c>), checked against the key pinned at the join, so a TLS-inspecting proxy can't hand over another
/// binary; this architecture's asset, only when newer (or with <c>--force</c>); downloaded and checked against the manifest's size and
/// SHA-256. With a file: that one. Either way the new binary must answer <c>version</c> before it replaces anything; then it installs
/// itself (<see cref="InstallSelf"/>): the service stops, <c>/usr/local/sbin/pnmt-agent</c> is replaced atomically, the unit rewritten,
/// and the service starts. The identity, keys, state and config.yaml stay.
/// </summary>
/// <summary>PNMT has nothing newer than this agent: <c>pnmt-agent update</c> says so and exits 0.</summary>
internal sealed class AlreadyUpToDateException(string message) : Exception(message);

internal static class Updater
{
    private const long MaxBytes = 300L * 1024 * 1024;

    public static async Task<string> UpdateAsync(string? file, bool force, IProgress<string> steps, CancellationToken ct)
    {
        var config = AgentFiles.ReadConfig();
        string candidate;
        string? temporary = null;
        if (file is not null)
        {
            candidate = Path.GetFullPath(file);
            if (!File.Exists(candidate))
            {
                throw new InvalidOperationException($"{file} does not exist.");
            }
        }
        else
        {
            if (config is null)
            {
                throw new InvalidOperationException("Not joined: there is no PNMT to update from (pnmt-agent update <file> uses a file).");
            }
            (candidate, var version) = await DownloadAsync(config, force, steps, ct);
            temporary = candidate;
            steps.Report($"Downloaded {version}, its SHA-256 matches PNMT's signed manifest");
        }
        try
        {
            steps.Report("Checking the new binary");
            var answer = Tool.Run(candidate, ["version"], TimeSpan.FromSeconds(30));
            if (!answer.Ok || !answer.Output.StartsWith(AgentInfo.Product + " ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{candidate} is no PNMT agent for this machine: "
                    + (answer.Reason.Length > 0 ? answer.Reason : $"\"{candidate} version\" doesn't answer as one."));
            }
            // "PNMT agent 1.3.0 (arm64)" → 1.3.0
            var newVersion = answer.Output.Trim()[(AgentInfo.Product.Length + 1)..].Split(' ')[0];
            // The new binary installs itself (hidden update-install), from where it is: this process runs from the file being
            // replaced, and a single-file binary loads its assemblies from its own file, by path, so it must touch nothing new after.
            steps.Report($"Handing over to {newVersion}");
            var install = Tool.Run(candidate, ["update-install", "--from", AgentInfo.Version], TimeSpan.FromMinutes(3));
            foreach (var line in (install.Output + install.Errors).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                steps.Report(line.TrimStart('…', ' '));
            }
            if (!install.Ok)
            {
                throw new InvalidOperationException($"{newVersion} could not install itself ({install.ExitCode}): {install.Reason}");
            }
            return newVersion;
        }
        finally
        {
            if (temporary is not null)
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>
    /// <c>pnmt-agent update-install --from &lt;version&gt;</c>, run by <c>update</c> from the new binary: stops the service, puts this
    /// binary in place of <c>/usr/local/sbin/pnmt-agent</c>, writes its unit, starts the service, and tells the journal.
    /// </summary>
    public static void InstallSelf(string from, IProgress<string> steps)
    {
        var installed = Systemd.IsInstalled;
        if (installed)
        {
            steps.Report("Stopping the service");
            Systemd.Systemctl("stop", AgentInfo.Unit);
        }
        steps.Report($"Replacing {AgentInfo.Program}");
        Systemd.Replace(AgentInfo.Program, AgentInfo.CurrentExecutable);
        if (installed || AgentFiles.ReadConfig() is not null)
        {
            steps.Report("Writing the unit");
            Systemd.WriteUnit();
            Systemd.Systemctl("daemon-reload");
            Systemd.Systemctl("enable", AgentInfo.Unit);
            steps.Report("Starting the service");
            Systemd.Systemctl("start", AgentInfo.Unit);
        }
        Journal.Write($"{AgentInfo.Product} updated from {from} to {AgentInfo.Version}.");
    }

    /// <summary>The manifest, verified; the asset for this architecture, downloaded into state/ and checked. Answers the file and the version.</summary>
    private static async Task<(string File, string Version)> DownloadAsync(AgentConfigFile config, bool force, IProgress<string> steps, CancellationToken ct)
    {
        using var pnmtdKey = AgentSigning.ImportPublicKey(config.PnmtdKey) ?? throw new InvalidOperationException("agent.json holds no valid PNMTD key: join again.");
        var baseUrl = new Uri(config.Url);
        if (baseUrl.Scheme != "https" && !config.PlainHttp)
        {
            throw new InvalidOperationException($"{config.Url} is not https://, and the join didn't confirm plain http://.");
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AgentInfo.Name}/{AgentInfo.Version}");

        steps.Report($"Asking {config.Url} for its version");
        using var response = await http.GetAsync(new Uri(baseUrl, AgentProtocol.DownloadPath), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PNMT answered {(int)response.StatusCode} {response.ReasonPhrase} for {AgentProtocol.DownloadPath}"
                + (response.StatusCode == System.Net.HttpStatusCode.NotFound ? ": it doesn't serve the agent's files (Agents__ServeDownloads)." : "."));
        }
        var answer = AgentJson.Deserialize<DownloadManifestResponse>(await response.Content.ReadAsStringAsync(ct))
            ?? throw new InvalidOperationException("PNMT's manifest is empty.");
        var manifest = Verify(answer, pnmtdKey);
        var asset = AgentDownloadAssets.For(AgentPlatform.Linux, AgentInfo.Architecture);
        var entry = manifest.Assets.FirstOrDefault(a => a.Name == asset)
            ?? throw new InvalidOperationException($"PNMT {manifest.Version} has no {asset} (yet): its Download tab says why.");
        if (!IsNewer(manifest.Version, AgentInfo.Version) && !force)
        {
            // Not a failure: unattended runs of update exit 0 (L4).
            throw new AlreadyUpToDateException(manifest.Version == AgentInfo.Version
                ? $"Already up to date: {AgentInfo.Version} (--force installs it anyway)."
                : $"Already up to date: this agent is {AgentInfo.Version}, newer than PNMT's {manifest.Version} (--force installs PNMT's anyway).");
        }
        if (entry.Size <= 0 || entry.Size > MaxBytes)
        {
            throw new InvalidOperationException($"The manifest's size for {asset} ({entry.Size} bytes) is not plausible.");
        }

        steps.Report($"Downloading {asset} ({entry.Size / (1024 * 1024)} MB)");
        var folder = Path.Combine(AgentInfo.StateFolder, "update");
        LinuxPlatform.SecureFolder(folder);
        var target = Path.Combine(folder, asset);
        using (var download = await http.GetAsync(new Uri(baseUrl, $"{AgentProtocol.DownloadPath}/{asset}"), HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!download.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"PNMT answered {(int)download.StatusCode} {download.ReasonPhrase} for {asset}.");
            }
            await using var source = await download.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(target, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            });
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > entry.Size)
                {
                    throw new InvalidOperationException($"{asset} is larger than the manifest says ({entry.Size} bytes): refused.");
                }
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (total != entry.Size || sha256 != entry.Sha256.ToLowerInvariant())
            {
                throw new InvalidOperationException($"{asset} doesn't match PNMT's signed manifest ({total} bytes, SHA-256 {sha256}): refused.");
            }
        }
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (target, manifest.Version);
    }

    /// <summary>The manifest's signature against the pinned key, its type and its (empty) agent id; refused otherwise.</summary>
    public static DownloadManifest Verify(DownloadManifestResponse answer, System.Security.Cryptography.ECDsa pinned)
    {
        var content = AgentSigning.Verify(pinned, answer.Signed);
        if (content is null || content.Type != AgentMessageTypes.DownloadManifest || content.AgentId != Guid.Empty)
        {
            throw new InvalidOperationException("PNMT's manifest is not signed with the key pinned at the join: refused.");
        }
        return AgentJson.Deserialize<DownloadManifest>(content.Body) ?? throw new InvalidOperationException("PNMT's manifest is empty.");
    }

    /// <summary>Whether <paramref name="candidate"/> is newer than <paramref name="current"/>: their numbers, a release above its pre-releases.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static (Version Number, bool Pre)? Parse(string text)
        {
            var main = text.Split('+')[0];
            var dash = main.IndexOf('-');
            return Version.TryParse(dash >= 0 ? main[..dash] : main, out var number) ? (number, dash >= 0) : null;
        }
        if (Parse(candidate) is not { } a || Parse(current) is not { } b)
        {
            return !string.Equals(candidate, current, StringComparison.Ordinal);
        }
        var compared = a.Number.CompareTo(b.Number);
        return compared > 0 || compared == 0 && b.Pre && !a.Pre;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
