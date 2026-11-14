using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Lib.Agent;

namespace PNMTD.Tests.Agents;

/// <summary>
/// The agents' files from PNMTD itself (docs/linux-sensor-plan.md §4.4, §9.7, §14): a file in the download folder is served only
/// while it matches its .sha256; unknown names are 404; the limit per address; turned off is 404; the manifest verifies with
/// PNMTD's agent key; a missing file is fetched once from the release (a stub here, never GitHub), checked, kept, and a failure
/// remembered; the Admin's links, states and Linux install lines.
/// </summary>
[TestClass]
public sealed class AgentDownloadTests
{
    private static readonly string Tag = AgentDownloads.ReleaseTag(ServerInfo.Version);

    private static DirectoryInfo folder = null!;
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        folder = Directory.CreateTempSubdirectory("pnmtd-downloads-");
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Agents:DownloadFolder"] = folder.FullName,
            ["Agents:DownloadsPerHour"] = "1000",
        });
    }

    [ClassCleanup]
    public static void Stop()
    {
        app.Dispose();
        folder.Delete(recursive: true);
    }

    [TestInitialize]
    public void Empty()
    {
        foreach (var file in folder.GetFiles())
        {
            file.Delete();
        }
        app.Services.GetRequiredService<DownloadGuard>().Reset();
    }

    /// <summary>A file of random bytes and its sha256sum line; <paramref name="sum"/> replaces the line.</summary>
    private static byte[] Put(DirectoryInfo into, string name, int size = 65536, string? sum = null)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        File.WriteAllBytes(Path.Combine(into.FullName, name), bytes);
        File.WriteAllText(Path.Combine(into.FullName, name + ".sha256"), sum ?? $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {name}\n");
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [TestMethod]
    public async Task AFileInTheFolderIsServedOnlyWhileItMatchesItsSha256()
    {
        var bytes = Put(folder, AgentDownloadAssets.LinuxX64);
        var client = app.Client();
        using var response = await client.GetAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxX64}");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(bytes.Length, response.Content.Headers.ContentLength);
        CollectionAssert.AreEqual(bytes, await response.Content.ReadAsByteArrayAsync());
        var sum = await (await client.GetAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxX64}.sha256")).Content.ReadAsStringAsync();
        Assert.AreEqual($"{Sha(bytes)}  {AgentDownloadAssets.LinuxX64}\n", sum);

        // Changed after its .sha256 was written: not served, and no fetch replaces it.
        await File.AppendAllTextAsync(Path.Combine(folder.FullName, AgentDownloadAssets.LinuxX64), "x");
        using var changed = await client.GetAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxX64}");
        Assert.AreEqual(HttpStatusCode.NotFound, changed.StatusCode);
        StringAssert.Contains(await changed.Content.ReadAsStringAsync(), "does not match its .sha256");
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxX64}.sha256")).StatusCode);

        // A .sha256 for another file, or one that isn't sha256sum's: not served either.
        var other = Put(folder, AgentDownloadAssets.WindowsX64);
        File.WriteAllText(Path.Combine(folder.FullName, AgentDownloadAssets.WindowsX64 + ".sha256"), $"{Sha(other)}  pnmt-sensor-arm64.exe\n");
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.WindowsX64}")).StatusCode);
        File.WriteAllText(Path.Combine(folder.FullName, AgentDownloadAssets.WindowsX64 + ".sha256"), "not a checksum\n");
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.WindowsX64}")).StatusCode);
        // sha256sum's binary mode and a path to it are its own.
        File.WriteAllText(Path.Combine(folder.FullName, AgentDownloadAssets.WindowsX64 + ".sha256"), $"{Sha(other).ToUpperInvariant()} *dist/{AgentDownloadAssets.WindowsX64}\n");
        CollectionAssert.AreEqual(other, await client.GetByteArrayAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.WindowsX64}"));
    }

    [TestMethod]
    public async Task UnknownNamesAre404()
    {
        File.WriteAllText(Path.Combine(folder.FullName, "notes.txt"), "secret");
        var client = app.Client();
        foreach (var name in new[] { "notes.txt", "pnmt-agent-linux-x86", "..%2Fappsettings.json", "PNMT-AGENT-LINUX-X64", "pnmt-agent-linux-x64.sha512" })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{AgentProtocol.DownloadPath}/{name}")).StatusCode, name);
        }
    }

    [TestMethod]
    public async Task DownloadsAreLimitedPerAddressAndTurnedOffAre404()
    {
        var limitedFolder = Directory.CreateTempSubdirectory("pnmtd-downloads-limited-");
        try
        {
            Put(limitedFolder, AgentDownloadAssets.LinuxArm64);
            using (var limited = new TestApp(settings: new Dictionary<string, string?>
            {
                ["Agents:DownloadFolder"] = limitedFolder.FullName,
                ["Agents:DownloadsPerHour"] = "2",
            }))
            {
                var client = limited.Client();
                var path = $"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxArm64}";
                Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(path + ".sha256")).StatusCode);
                using var refused = await client.GetAsync(path);
                Assert.AreEqual(HttpStatusCode.TooManyRequests, refused.StatusCode);
                Assert.IsTrue(refused.Headers.RetryAfter?.Delta > TimeSpan.FromMinutes(59), refused.Headers.RetryAfter?.ToString());
                // The manifest is not a download.
                Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(AgentProtocol.DownloadPath)).StatusCode);
            }

            foreach (var off in new[] { "Agents:ServeDownloads", "Agents:Enabled" })
            {
                using var disabled = new TestApp(settings: new Dictionary<string, string?> { ["Agents:DownloadFolder"] = limitedFolder.FullName, [off] = "false" });
                var client = disabled.Client();
                Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(AgentProtocol.DownloadPath)).StatusCode, off);
                Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxArm64}")).StatusCode, off);
                if (off == "Agents:ServeDownloads")
                {
                    // The dialogs offer GitHub only.
                    var links = await (await disabled.Client(Roles.Admin).GetAsync("/api/v1/agents/downloads")).ReadAsync<AgentDownloadLinks>();
                    Assert.IsFalse(links.ServeDownloads);
                    Assert.IsTrue(links.Assets.All(a => a.PnmtUrl is null));
                    var arm64 = links.Linux.Single(l => l.Architecture == "arm64");
                    Assert.IsNull(arm64.Pnmt);
                    StringAssert.StartsWith(arm64.GitHub, $"wget -qO /tmp/pnmt-agent http://127.0.0.1:9/releases/download/{Tag}/{AgentDownloadAssets.LinuxArm64} && ");
                }
            }
        }
        finally
        {
            limitedFolder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task TheManifestListsTheVerifiedFilesSignedWithPnmtdsAgentKey()
    {
        var linux = Put(folder, AgentDownloadAssets.LinuxX64, 4096);
        var windows = Put(folder, AgentDownloadAssets.WindowsArm64, 2048);
        Put(folder, AgentDownloadAssets.LinuxArm64, 1024, sum: new string('0', 64) + "  " + AgentDownloadAssets.LinuxArm64);
        var answer = await app.Client().GetAsync(AgentProtocol.DownloadPath);
        var response = AgentJson.Deserialize<DownloadManifestResponse>(await answer.Content.ReadAsStringAsync())!;
        var key = await app.Services.GetRequiredService<AgentKeyStore>().GetAsync(CancellationToken.None);
        Assert.AreEqual(key.Fingerprint, response.SigningKeyFingerprint);

        using var pnmtd = AgentSigning.ImportPublicKey(key.PublicKey)!;
        var content = AgentSigning.Verify(pnmtd, response.Signed);
        Assert.IsNotNull(content, "signed with PNMTD's agent key");
        Assert.AreEqual(AgentMessageTypes.DownloadManifest, content.Type);
        Assert.AreEqual(Guid.Empty, content.AgentId);
        var manifest = AgentJson.Deserialize<DownloadManifest>(content.Body)!;
        Assert.AreEqual(ServerInfo.Version, manifest.Version);
        CollectionAssert.AreEquivalent(new[]
        {
            new DownloadAsset(AgentDownloadAssets.LinuxX64, 4096, Sha(linux)),
            new DownloadAsset(AgentDownloadAssets.WindowsArm64, 2048, Sha(windows)),
        }, manifest.Assets.ToList(), "only the files that match their .sha256");

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.IsNull(AgentSigning.Verify(other, response.Signed), "another key");
        Assert.IsNull(AgentSigning.Verify(pnmtd, response.Signed with { P = response.Signed.P.Replace(Sha(linux), new string('1', 64)) }), "another hash");
    }

    [TestMethod]
    public async Task TheAdminSeesEachFilesStateAndTheLinuxInstallLines()
    {
        var bytes = Put(folder, AgentDownloadAssets.LinuxX64);
        var admin = app.Client(Roles.Admin);
        var links = await (await admin.GetAsync("/api/v1/agents/downloads")).ReadAsync<AgentDownloadLinks>();
        Assert.AreEqual(ServerInfo.Version, links.Version);
        Assert.IsTrue(links.ServeDownloads);
        Assert.AreEqual("pnmt-agent update", links.UpdateLine);
        Assert.AreEqual(AgentDownloads.TokenPlaceholder, links.TokenPlaceholder);
        CollectionAssert.AreEquivalent(AgentDownloadAssets.Executables.ToList(), links.Assets.Select(a => a.Name).ToList());
        var x64 = links.Assets.Single(a => a.Name == AgentDownloadAssets.LinuxX64);
        Assert.AreEqual(AgentDownloadState.Here, x64.State);
        Assert.AreEqual(Sha(bytes), x64.Sha256);
        Assert.AreEqual(bytes.Length, x64.Size);
        Assert.AreEqual($"http://localhost/agent/v1/download/{AgentDownloadAssets.LinuxX64}", x64.PnmtUrl);
        Assert.AreEqual($"http://127.0.0.1:9/releases/download/{Tag}/{AgentDownloadAssets.LinuxX64}", x64.GitHubUrl);
        Assert.AreEqual(AgentPlatform.Linux, x64.Platform);
        Assert.AreNotEqual(AgentDownloadState.Here, links.Assets.Single(a => a.Name == AgentDownloadAssets.LinuxArm64).State);
        // Windows' links as before, with PNMT's next to GitHub's.
        CollectionAssert.AreEqual(new[] { "x64", "arm64" }, links.Files.Select(f => f.Architecture).ToList());
        Assert.AreEqual($"http://localhost/agent/v1/download/{AgentDownloadAssets.WindowsX64}", links.Files[0].PnmtUrl);

        // §4.4's line: the hash checked before anything runs; --allow-http for this plain http:// PNMT.
        var line = links.Linux.Single(l => l.Architecture == "x64");
        Assert.AreEqual(
            $"wget -qO /tmp/pnmt-agent http://localhost/agent/v1/download/{AgentDownloadAssets.LinuxX64} && echo \"{Sha(bytes)}  /tmp/pnmt-agent\" | sha256sum -c --quiet"
            + " && install -m 0755 /tmp/pnmt-agent /usr/local/sbin/pnmt-agent && rm /tmp/pnmt-agent && pnmt-agent join --url http://localhost --token xxxx-xxxx --allow-http",
            line.Pnmt);
        StringAssert.StartsWith(line.PnmtCurl, $"curl -fsSLo /tmp/pnmt-agent http://localhost/agent/v1/download/{AgentDownloadAssets.LinuxX64} && echo \"{Sha(bytes)}  /tmp/pnmt-agent\"");
        StringAssert.StartsWith(line.GitHub, $"wget -qO /tmp/pnmt-agent http://127.0.0.1:9/releases/download/{Tag}/{AgentDownloadAssets.LinuxX64} && echo \"{Sha(bytes)}");
        StringAssert.EndsWith(line.GitHubCurl, "--token xxxx-xxxx --allow-http");
        Assert.IsNull(links.Linux.Single(l => l.Architecture == "arm64").Pnmt, "no hash known: no line");
        Assert.IsNotNull(links.Note);

        // A Linux token's command line is the line, with the token in it.
        var hostId = await AgentHarness.CreateHostAsync(admin, "download-tests");
        var created = await AgentHarness.TokenAsync(admin, hostId, null, AgentPlatform.Linux);
        Assert.AreEqual(line.Pnmt!.Replace("xxxx-xxxx", created.Token), created.CommandLine);
        var windows = await AgentHarness.TokenAsync(admin, hostId, null);
        StringAssert.StartsWith(windows.CommandLine, "pnmt-sensor-x64.exe join --url http://localhost --token ");

        // Fetch again: audited; answers at once.
        var fetched = await (await admin.PostJsonAsync("/api/v1/agents/downloads/fetch", new AgentDownloadFetchRequest([AgentDownloadAssets.LinuxArm64])))
            .ReadAsync<AgentDownloadLinks>();
        Assert.AreEqual(AgentDownloadState.Here, fetched.Assets.Single(a => a.Name == AgentDownloadAssets.LinuxX64).State);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/v1/agents/downloads/fetch", new AgentDownloadFetchRequest(["notes.txt"]))).StatusCode);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuthLog.AnyAsync(l => l.EventType == AuthEvents.AgentDownloadsFetched && l.Detail!.Contains(AgentDownloadAssets.LinuxArm64)));
        await db.Hosts.Where(h => h.Id == hostId).ExecuteUpdateAsync(u => u.SetProperty(h => h.Enabled, false));
    }

    /// <summary>The release on "GitHub": its files by name, a gate that holds the executables back, and what was asked for.</summary>
    private sealed class Release
    {
        public readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);
        public readonly List<string> Requests = [];
        public TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ReleaseHandler(Release release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var name = path[(path.LastIndexOf('/') + 1)..];
            lock (release.Requests)
            {
                release.Requests.Add(path);
            }
            if (!name.EndsWith(".sha256", StringComparison.Ordinal))
            {
                await release.Gate.Task.WaitAsync(ct);
            }
            return path.StartsWith($"/releases/download/{Tag}/", StringComparison.Ordinal) && release.Files.TryGetValue(name, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" };
        }
    }

    [TestMethod]
    public async Task AMissingFileIsFetchedFromTheReleaseOnceCheckedAndKept()
    {
        var into = Directory.CreateTempSubdirectory("pnmtd-downloads-fetched-");
        // What an older version fetched into the same folder (a volume, after an update).
        Put(Directory.CreateDirectory(Path.Combine(into.FullName, "sensor-0.9.0")), AgentDownloadAssets.LinuxArm64);
        var release = new Release();
        var binary = RandomNumberGenerator.GetBytes(200_000);
        release.Files[AgentDownloadAssets.LinuxArm64] = binary;
        release.Files[AgentDownloadAssets.LinuxArm64 + ".sha256"] = Encoding.UTF8.GetBytes($"{Sha(binary)}  {AgentDownloadAssets.LinuxArm64}\n");
        // x64: the release's file doesn't match its .sha256 (a tampered download).
        release.Files[AgentDownloadAssets.LinuxX64] = RandomNumberGenerator.GetBytes(1000);
        release.Files[AgentDownloadAssets.LinuxX64 + ".sha256"] = Encoding.UTF8.GetBytes($"{new string('a', 64)}  {AgentDownloadAssets.LinuxX64}\n");
        try
        {
            using var fetching = new TestApp(settings: new Dictionary<string, string?>
            {
                ["Agents:DownloadFolder"] = into.FullName,
                ["Agents:AgentDownloadBaseUrl"] = "http://release.test/releases/download",
            }, services: s => s.AddHttpClient(nameof(AgentDownloads)).ConfigurePrimaryHttpMessageHandler(() => new ReleaseHandler(release)));
            var client = fetching.Client();
            var path = $"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxArm64}";

            // Three downloads while the release is slow: they wait for one fetch.
            var downloads = Enumerable.Range(0, 3).Select(_ => client.GetAsync(path)).ToList();
            await AgentHarness.EventuallyAsync(() => Task.FromResult(release.Requests.Any(r => r.EndsWith("/" + AgentDownloadAssets.LinuxArm64, StringComparison.Ordinal))));
            var links = await (await fetching.Client(Roles.Admin).GetAsync("/api/v1/agents/downloads")).ReadAsync<AgentDownloadLinks>();
            Assert.AreEqual(AgentDownloadState.Fetching, links.Assets.Single(a => a.Name == AgentDownloadAssets.LinuxArm64).State);
            release.Gate.SetResult();
            foreach (var response in await Task.WhenAll(downloads))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                CollectionAssert.AreEqual(binary, await response.Content.ReadAsByteArrayAsync());
            }
            Assert.AreEqual(1, release.Requests.Count(r => r == $"/releases/download/{Tag}/{AgentDownloadAssets.LinuxArm64}"), "one fetch");
            // Kept in the version's own folder (the compose deployment's volume outlives updates); an older version's is deleted.
            var kept = Path.Combine(into.FullName, Tag);
            CollectionAssert.AreEqual(binary, File.ReadAllBytes(Path.Combine(kept, AgentDownloadAssets.LinuxArm64)), "kept");
            Assert.AreEqual($"{Sha(binary)}  {AgentDownloadAssets.LinuxArm64}\n", File.ReadAllText(Path.Combine(kept, AgentDownloadAssets.LinuxArm64 + ".sha256")));
            Assert.AreEqual(2, new DirectoryInfo(kept).GetFiles().Length, "no temporary files left");
            Assert.IsFalse(Directory.Exists(Path.Combine(into.FullName, "sensor-0.9.0")), "the older version's files are gone");

            // A file that doesn't match the release's .sha256 is not kept, and the failure is remembered.
            using var tampered = await client.GetAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxX64}");
            Assert.AreEqual(HttpStatusCode.NotFound, tampered.StatusCode);
            StringAssert.Contains(await tampered.Content.ReadAsStringAsync(), "does not match its .sha256");
            Assert.IsFalse(File.Exists(Path.Combine(into.FullName, Tag, AgentDownloadAssets.LinuxX64)));
            var asked = release.Requests.Count;
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxX64}")).StatusCode);
            Assert.AreEqual(asked, release.Requests.Count, "not fetched again for a while");
            links = await (await fetching.Client(Roles.Admin).GetAsync("/api/v1/agents/downloads")).ReadAsync<AgentDownloadLinks>();
            var x64 = links.Assets.Single(a => a.Name == AgentDownloadAssets.LinuxX64);
            Assert.AreEqual(AgentDownloadState.Failed, x64.State);
            StringAssert.Contains(x64.Error, "does not match its .sha256");
            Assert.AreEqual(AgentDownloadState.Here, links.Assets.Single(a => a.Name == AgentDownloadAssets.LinuxArm64).State);

            // The release fixed: Fetch again doesn't wait for the 10 minutes.
            var fixedBinary = RandomNumberGenerator.GetBytes(3000);
            release.Files[AgentDownloadAssets.LinuxX64] = fixedBinary;
            release.Files[AgentDownloadAssets.LinuxX64 + ".sha256"] = Encoding.UTF8.GetBytes($"{Sha(fixedBinary)}  {AgentDownloadAssets.LinuxX64}\n");
            await (await fetching.Client(Roles.Admin).PostJsonAsync("/api/v1/agents/downloads/fetch", new AgentDownloadFetchRequest([AgentDownloadAssets.LinuxX64])))
                .ReadAsync<AgentDownloadLinks>();
            await AgentHarness.EventuallyAsync(async () =>
                (await (await fetching.Client(Roles.Admin).GetAsync("/api/v1/agents/downloads")).ReadAsync<AgentDownloadLinks>())
                .Assets.Single(a => a.Name == AgentDownloadAssets.LinuxX64).State == AgentDownloadState.Here);
            CollectionAssert.AreEqual(fixedBinary, await client.GetByteArrayAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxX64}"));
        }
        finally
        {
            into.Delete(recursive: true);
        }
    }
}
