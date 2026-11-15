using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Agent;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Agents;

/// <summary>
/// The agents' versions (docs/agent-update-plan.md §3, §4, §5, §9): the versions here and in GitHub's list (a stub), the version
/// each platform's agents get and what follows from it (the manifest, the files, who is outdated, the links), one newer than PNMT
/// only when confirmed, deleting refused while agents get it, fetching a version, and admins' uploads with their checks.
/// </summary>
[TestClass]
public sealed class AgentVersionTests
{
    private const string ApiUrl = "http://github.test/repos/muqiuq/PNMTD/releases?per_page=100";

    private static DirectoryInfo folder = null!;
    private static TestApp app = null!;
    private static readonly GitHub github = new();

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        folder = Directory.CreateTempSubdirectory("pnmtd-versions-");
        app = new TestApp(settings: new Dictionary<string, string?>
        {
            ["Agents:DownloadFolder"] = folder.FullName,
            ["Agents:DownloadsPerHour"] = "1000",
            ["Agents:AgentDownloadBaseUrl"] = "http://release.test/releases/download",
            ["Agents:ReleasesApiUrl"] = ApiUrl,
        }, services: s => s.AddHttpClient(nameof(AgentDownloads)).ConfigurePrimaryHttpMessageHandler(() => new GitHubHandler(github)));
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
        foreach (var directory in folder.GetDirectories())
        {
            directory.Delete(recursive: true);
        }
        github.Releases = "[]";
        github.Files.Clear();
        app.Services.GetRequiredService<AgentReleaseCatalog>().Forget();
        app.Services.GetRequiredService<DownloadGuard>().Reset();
    }

    /// <summary>The database is every test class's: the agents get PNMTD's version again.</summary>
    [TestCleanup]
    public async Task ChooseNothing()
    {
        await using var db = app.NewDb();
        await db.AgentSettings.ExecuteUpdateAsync(u => u.SetProperty(s => s.WindowsAgentVersion, (string?)null).SetProperty(s => s.LinuxAgentVersion, (string?)null));
    }

    private static byte[] Put(string version, string name, byte[]? bytes = null)
    {
        var into = Directory.CreateDirectory(Path.Combine(folder.FullName, $"sensor-{version}"));
        bytes ??= RandomNumberGenerator.GetBytes(4096);
        File.WriteAllBytes(Path.Combine(into.FullName, name), bytes);
        File.WriteAllText(Path.Combine(into.FullName, name + ".sha256"), $"{Sha(bytes)}  {name}\n");
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The first bytes of a 64-bit ELF for x86-64 or AArch64, then random bytes.</summary>
    private static byte[] Elf(bool arm64 = false, int size = 8192)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        "\u007fELF"u8.CopyTo(bytes);
        bytes[4] = 2;
        bytes[5] = 1;
        BitConverter.TryWriteBytes(bytes.AsSpan(18), (ushort)(arm64 ? 183 : 62));
        return bytes;
    }

    /// <summary>The first bytes of a PE image (MZ, e_lfanew 0x80, PE\0\0, the machine), then random bytes.</summary>
    private static byte[] Pe(bool arm64 = false, int size = 8192)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.TryWriteBytes(bytes.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        BitConverter.TryWriteBytes(bytes.AsSpan(0x84), (ushort)(arm64 ? 0xAA64 : 0x8664));
        return bytes;
    }

    private static string Release(string version, bool pre = false, bool draft = false, string[]? assets = null) =>
        $$"""{"tag_name": "sensor-{{version}}", "prerelease": {{(pre ? "true" : "false")}}, "draft": {{(draft ? "true" : "false")}}, "published_at": "2026-11-01T12:00:00Z", "html_url": "https://github.com/muqiuq/PNMTD/releases/tag/sensor-{{version}}", "assets": [{{string.Join(", ", (assets ?? []).Select(a => $"{{\"name\": \"{a}\"}}"))}}]}""";

    private static async Task<AgentVersionList> ListAsync() => await (await app.Client(Roles.Admin).GetAsync("/api/v1/agents/versions")).ReadAsync<AgentVersionList>();

    private static async Task<HttpResponseMessage> ChooseAsync(string? windows, string? linux, bool confirm = false) =>
        await app.Client(Roles.Admin).PutJsonAsync("/api/v1/agents/versions/chosen", new AgentVersionChooseRequest(windows, linux, confirm));

    private static async Task<DownloadManifest> ManifestAsync(string query = "")
    {
        var response = AgentJson.Deserialize<DownloadManifestResponse>(await (await app.Client().GetAsync(AgentProtocol.DownloadPath + query)).Content.ReadAsStringAsync())!;
        var key = await app.Services.GetRequiredService<AgentKeyStore>().GetAsync(CancellationToken.None);
        using var pnmtd = AgentSigning.ImportPublicKey(key.PublicKey)!;
        return AgentJson.Deserialize<DownloadManifest>(AgentSigning.Verify(pnmtd, response.Signed)!.Body)!;
    }

    [TestMethod]
    public async Task TheListHasTheVersionsHereAndOnGitHub()
    {
        Put("1.0.0", AgentDownloadAssets.LinuxX64);
        github.Releases = "[" + string.Join(", ",
            Release("1.1.0-rc1", pre: true, assets: [AgentDownloadAssets.WindowsX64, AgentDownloadAssets.WindowsX64 + ".sha256"]),
            Release("1.0.0", assets: [AgentDownloadAssets.LinuxX64, AgentDownloadAssets.LinuxX64 + ".sha256"]),
            Release("9.9.9", draft: true),
            """{"tag_name": "v1.0.0", "assets": []}""",
            """{"tag_name": "sensor-not-a-version", "assets": []}""") + "]";

        var list = await ListAsync();
        Assert.AreEqual(ServerInfo.Version, list.PnmtVersion);
        Assert.IsTrue(list.Catalog.Configured);
        Assert.IsTrue(list.Catalog.Available, list.Catalog.Error);
        Assert.IsTrue(list.Windows.FollowsPnmt && list.Linux.FollowsPnmt);
        Assert.AreEqual(ServerInfo.Version, list.Linux.Version);
        CollectionAssert.AreEqual(new[] { ServerInfo.Version, "1.1.0-rc1", "1.0.0" }, list.Versions.Select(v => v.Version).ToList(), "newest first; no drafts, no other tags");

        var pnmt = list.Versions[0];
        Assert.IsTrue(pnmt.IsPnmtVersion && pnmt.ChosenForWindows && pnmt.ChosenForLinux);
        Assert.IsNotNull(pnmt.DeleteRefused);
        var rc = list.Versions[1];
        Assert.IsTrue(rc.PreRelease && rc.OnGitHub && !rc.Here);
        Assert.AreEqual("https://github.com/muqiuq/PNMTD/releases/tag/sensor-1.1.0-rc1", rc.ReleaseUrl);
        Assert.IsTrue(rc.Files.Single(f => f.Name == AgentDownloadAssets.WindowsX64).InRelease);
        Assert.IsFalse(rc.Files.Single(f => f.Name == AgentDownloadAssets.LinuxX64).InRelease);
        var old = list.Versions[2];
        Assert.IsTrue(old.Here && old.OnGitHub && !old.PreRelease);
        var file = old.Files.Single(f => f.Name == AgentDownloadAssets.LinuxX64);
        Assert.AreEqual(AgentDownloadState.Here, file.State);
        Assert.AreEqual(AgentFileSource.GitHub, file.Source, "a folder from before 1.8 has no origins: fetched from GitHub");
        Assert.IsNull(old.DeleteRefused);

        // GitHub's list failing: the versions here remain, with why.
        github.Releases = null;
        var failing = await (await app.Client(Roles.Admin).GetAsync("/api/v1/agents/versions?refresh=true")).ReadAsync<AgentVersionList>();
        Assert.IsFalse(failing.Catalog.Available);
        StringAssert.Contains(failing.Catalog.Error, "503");
        Assert.IsTrue(failing.Versions.Any(v => v.Version == "1.0.0" && v.Here));

        Assert.AreEqual(HttpStatusCode.Forbidden, (await app.Client(Roles.User).GetAsync("/api/v1/agents/versions")).StatusCode);
    }

    [TestMethod]
    public async Task TheChosenVersionIsWhatAgentsDownloadAndWhatMakesThemOutdated()
    {
        var linuxBytes = Put("1.0.0", AgentDownloadAssets.LinuxX64);
        Put("1.0.0", AgentDownloadAssets.WindowsX64);
        var admin = app.Client(Roles.Admin);
        var hostId = await AgentHarness.CreateHostAsync(admin, "versions");
        var linuxAgent = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, "lin", AgentPlatform.Linux)).Token, "lin01", AgentPlatform.Linux);
        var windowsAgent = await AgentHarness.JoinAsync(app, (await AgentHarness.TokenAsync(admin, hostId, "win")).Token, "win01");
        async Task<AgentServerItem> ServerAsync(Guid id) =>
            (await (await admin.GetAsync($"/api/v1/agents/servers?hostId={hostId}")).ReadAsync<Paged<AgentServerItem>>()).Items.Single(i => i.SensorId == id);
        try
        {
            // The harness' agents report 1.2.0: older than PNMT's version, which agents get by default.
            Assert.IsTrue((await ServerAsync(linuxAgent.Grant.SensorId)).Outdated);

            var list = await (await ChooseAsync(null, "1.0.0")).ReadAsync<AgentVersionList>();
            Assert.AreEqual("1.0.0", list.Linux.Version);
            Assert.IsFalse(list.Linux.FollowsPnmt);
            Assert.IsTrue(list.Windows.FollowsPnmt);
            Assert.IsTrue(list.Linux.Newer >= 1, "this test's agent runs 1.2.0");

            // What a Linux agent's update finds: 1.0.0, its file, from here.
            var manifest = await ManifestAsync();
            Assert.AreEqual("1.0.0", manifest.Version);
            Assert.AreEqual(Sha(linuxBytes), manifest.Assets.Single().Sha256);
            Assert.AreEqual("GitHub's release sensor-1.0.0", manifest.Assets.Single().Source);
            CollectionAssert.AreEqual(linuxBytes, await app.Client().GetByteArrayAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxX64}"));
            Assert.AreEqual(ServerInfo.Version, (await ManifestAsync("?platform=Windows")).Version, "Windows agents still get PNMT's");

            var linux = await ServerAsync(linuxAgent.Grant.SensorId);
            Assert.IsFalse(linux.Outdated, "1.2.0 is newer than 1.0.0");
            Assert.IsTrue(linux.Ahead);
            Assert.AreEqual("1.0.0", linux.ChosenVersion);
            Assert.IsTrue((await ServerAsync(windowsAgent.Grant.SensorId)).Outdated);
            var outdated = await (await admin.GetAsync($"/api/v1/agents/servers?hostId={hostId}&outdated=true")).ReadAsync<Paged<AgentServerItem>>();
            CollectionAssert.AreEqual(new[] { windowsAgent.Grant.SensorId }, outdated.Items.Select(i => i.SensorId).ToList());

            var links = await (await admin.GetAsync("/api/v1/agents/downloads")).ReadAsync<AgentDownloadLinks>();
            Assert.AreEqual(ServerInfo.Version, links.Version);
            Assert.AreEqual("1.0.0", links.LinuxVersion);
            Assert.AreEqual(ServerInfo.Version, links.WindowsVersion);
            var asset = links.Assets.Single(a => a.Name == AgentDownloadAssets.LinuxX64);
            Assert.AreEqual("1.0.0", asset.Version);
            Assert.AreEqual(AgentDownloadState.Here, asset.State);
            Assert.AreEqual($"http://release.test/releases/download/sensor-1.0.0/{AgentDownloadAssets.LinuxX64}", asset.GitHubUrl);
            StringAssert.Contains(links.Linux.Single(l => l.Architecture == "x64").Pnmt, Sha(linuxBytes));

            await using (var db = app.NewDb())
            {
                Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.AgentVersionChosen && l.Detail!.Contains("Linux agents get 1.0.0")));
            }

            // Neither here nor on GitHub, or not a version: refused.
            Assert.AreEqual(HttpStatusCode.BadRequest, (await ChooseAsync("3.0.0", null)).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await ChooseAsync(null, "1.0")).StatusCode);
            Assert.AreEqual(HttpStatusCode.BadRequest, (await ChooseAsync(null, "../1.0.0")).StatusCode);

            // Newer than PNMT: a warning to confirm first.
            Put("99.0.0", AgentDownloadAssets.LinuxArm64);
            using (var unconfirmed = await ChooseAsync(null, "99.0.0"))
            {
                Assert.AreEqual(HttpStatusCode.Conflict, unconfirmed.StatusCode);
                var problem = await unconfirmed.ProblemAsync();
                Assert.AreEqual("newer_than_pnmt", problem.GetProperty("code").GetString());
                StringAssert.Contains(problem.GetProperty("detail").GetString(), $"newer than this PNMT ({ServerInfo.Version})");
            }
            Assert.AreEqual("1.0.0", (await ListAsync()).Linux.Version, "not chosen");
            var newer = await (await ChooseAsync(null, "99.0.0", confirm: true)).ReadAsync<AgentVersionList>();
            Assert.IsTrue(newer.Linux.NewerThanPnmt);
            Assert.IsTrue(newer.Versions.Single(v => v.Version == "99.0.0").NewerThanPnmt);

            // Back to PNMT's own.
            var back = await (await ChooseAsync("", null)).ReadAsync<AgentVersionList>();
            Assert.IsTrue(back.Windows.FollowsPnmt && back.Linux.FollowsPnmt);
        }
        finally
        {
            await using var db = app.NewDb();
            await db.Hosts.Where(h => h.Id == hostId).ExecuteUpdateAsync(u => u.SetProperty(h => h.Enabled, false));
        }
    }

    [TestMethod]
    public async Task AVersionAgentsGetCannotBeDeleted()
    {
        Put("1.0.0", AgentDownloadAssets.WindowsX64);
        await (await ChooseAsync("1.0.0", null)).ReadAsync<AgentVersionList>();
        var admin = app.Client(Roles.Admin);
        using (var refused = await admin.DeleteAsync("/api/v1/agents/versions/1.0.0"))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.AreEqual("chosen_version", (await refused.ProblemAsync()).GetProperty("code").GetString());
        }
        Assert.IsNotNull((await ListAsync()).Versions.Single(v => v.Version == "1.0.0").DeleteRefused);

        await (await ChooseAsync(null, null)).ReadAsync<AgentVersionList>();
        Assert.AreEqual(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/v1/agents/versions/1.0.0")).StatusCode);
        Assert.IsFalse(Directory.Exists(Path.Combine(folder.FullName, "sensor-1.0.0")));
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/v1/agents/versions/1.0.0")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await admin.DeleteAsync("/api/v1/agents/versions/1.0")).StatusCode);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.AgentVersionDeleted && l.Detail!.StartsWith("1.0.0 deleted")));
    }

    [TestMethod]
    public async Task AVersionIsFetchedFromItsRelease()
    {
        var binary = Elf();
        github.Files[$"sensor-1.0.0/{AgentDownloadAssets.LinuxArm64}"] = binary;
        github.Files[$"sensor-1.0.0/{AgentDownloadAssets.LinuxArm64}.sha256"] = Encoding.UTF8.GetBytes($"{Sha(binary)}  {AgentDownloadAssets.LinuxArm64}\n");
        var admin = app.Client(Roles.Admin);
        await (await admin.PostJsonAsync("/api/v1/agents/versions/1.0.0/fetch", new AgentVersionFetchRequest([AgentDownloadAssets.LinuxArm64]))).ReadAsync<AgentVersionList>();
        var list = await AgentHarness.EventuallyAsync(ListAsync,
            l => l.Versions.SingleOrDefault(v => v.Version == "1.0.0")?.Files.Single(f => f.Name == AgentDownloadAssets.LinuxArm64).State == AgentDownloadState.Here);
        var file = list.Versions.Single(v => v.Version == "1.0.0").Files.Single(f => f.Name == AgentDownloadAssets.LinuxArm64);
        Assert.AreEqual(AgentFileSource.GitHub, file.Source);
        Assert.AreEqual(Sha(binary), file.Sha256);
        CollectionAssert.AreEqual(binary, await admin.GetByteArrayAsync($"/api/v1/agents/versions/1.0.0/files/{AgentDownloadAssets.LinuxArm64}"));
        Assert.AreEqual($"{Sha(binary)}  {AgentDownloadAssets.LinuxArm64}\n",
            await admin.GetStringAsync($"/api/v1/agents/versions/1.0.0/files/{AgentDownloadAssets.LinuxArm64}.sha256"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/v1/agents/versions/1.0.0/files/{AgentDownloads.OriginsFile}")).StatusCode);

        // A file the release doesn't have fails, with why.
        await (await admin.PostJsonAsync("/api/v1/agents/versions/1.0.0/fetch", new AgentVersionFetchRequest([AgentDownloadAssets.WindowsArm64]))).ReadAsync<AgentVersionList>();
        list = await AgentHarness.EventuallyAsync(ListAsync,
            l => l.Versions.Single(v => v.Version == "1.0.0").Files.Single(f => f.Name == AgentDownloadAssets.WindowsArm64).State == AgentDownloadState.Failed);
        StringAssert.Contains(list.Versions.Single(v => v.Version == "1.0.0").Files.Single(f => f.Name == AgentDownloadAssets.WindowsArm64).Error, "404");
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await admin.PostJsonAsync("/api/v1/agents/versions/1.0.0/fetch", new AgentVersionFetchRequest(["notes.txt"]))).StatusCode);
        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.AgentVersionFetched && l.Detail!.Contains("1.0.0: " + AgentDownloadAssets.LinuxArm64)));
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string version, string name, byte[] bytes, string? sha256 = null)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", name);
        if (sha256 is not null)
        {
            form.Add(new StringContent(sha256), "sha256");
        }
        return await client.PostAsync($"/api/v1/agents/versions/{version}/files", form);
    }

    [TestMethod]
    public async Task OnlyAdminsUploadAndTheFileIsChecked()
    {
        var elf = Elf();
        var manager = app.ClientWithRights("Servers:Manage");
        Assert.AreEqual(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/agents/versions")).StatusCode, "the list is the right's");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await UploadAsync(manager, "1.5.0", AgentDownloadAssets.LinuxX64, elf)).StatusCode, "an upload is an admin's");

        var admin = app.Client(Roles.Admin);
        async Task<string> RefusedAsync(string version, string name, byte[] bytes, string? sha256 = null)
        {
            using var response = await UploadAsync(admin, version, name, bytes, sha256);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"{name}: {await response.Content.ReadAsStringAsync()}");
            return await response.Content.ReadAsStringAsync();
        }
        StringAssert.Contains(await RefusedAsync("1.5.0", AgentDownloadAssets.LinuxX64, RandomNumberGenerator.GetBytes(5000)), "no ELF header");
        StringAssert.Contains(await RefusedAsync("1.5.0", AgentDownloadAssets.LinuxX64, Elf(arm64: true)), "another architecture");
        StringAssert.Contains(await RefusedAsync("1.5.0", AgentDownloadAssets.WindowsX64, elf), "no MZ header");
        StringAssert.Contains(await RefusedAsync("1.5.0", AgentDownloadAssets.WindowsArm64, Pe()), "another architecture");
        await RefusedAsync("1.5.0", "evil.sh", elf);
        StringAssert.Contains(await RefusedAsync("1.5.0", AgentDownloadAssets.LinuxX64, elf, new string('0', 64)), "not kept");
        await RefusedAsync("1.5", AgentDownloadAssets.LinuxX64, elf);
        Assert.IsFalse(Directory.Exists(Path.Combine(folder.FullName, "sensor-1.5.0")) && Directory.GetFiles(Path.Combine(folder.FullName, "sensor-1.5.0")).Length > 0,
            "nothing kept, no temporary files");

        var list = await (await UploadAsync(admin, "1.5.0", AgentDownloadAssets.LinuxX64, elf, Sha(elf).ToUpperInvariant())).ReadAsync<AgentVersionList>();
        await (await UploadAsync(admin, "1.5.0", AgentDownloadAssets.WindowsX64, Pe())).ReadAsync<AgentVersionList>();
        var version = list.Versions.Single(v => v.Version == "1.5.0");
        Assert.IsTrue(version.KeptUntilDeleted);
        var file = version.Files.Single(f => f.Name == AgentDownloadAssets.LinuxX64);
        Assert.AreEqual(AgentFileSource.Upload, file.Source);
        Assert.AreEqual(Sha(elf), file.Sha256);
        Assert.AreEqual(TestUsers.NameOf(Roles.Admin), file.AddedBy);

        // Chosen, its manifest says who uploaded it: the machine's update prints it.
        await (await ChooseAsync(null, "1.5.0")).ReadAsync<AgentVersionList>();
        var asset = (await ManifestAsync("?platform=linux")).Assets.Single();
        Assert.AreEqual($"uploaded to PNMT by {TestUsers.NameOf(Roles.Admin)} on {DateTime.UtcNow:yyyy-MM-dd}", asset.Source);
        CollectionAssert.AreEqual(elf, await app.Client().GetByteArrayAsync($"{AgentProtocol.DownloadPath}/{AgentDownloadAssets.LinuxX64}"));

        // Kept by the retention, whatever Agents:KeepVersions says.
        await ChooseNothing();
        var downloads = app.Services.GetRequiredService<AgentDownloads>();
        Put("0.1.0", AgentDownloadAssets.LinuxX64);
        Put("0.2.0", AgentDownloadAssets.LinuxX64);
        Put("0.3.0", AgentDownloadAssets.LinuxX64);
        CollectionAssert.AreEqual(new[] { "0.1.0" }, downloads.Retain(AgentChosenVersions.Pnmt).ToList());
        Assert.IsTrue(Directory.Exists(Path.Combine(folder.FullName, "sensor-1.5.0")));

        await using var db = app.NewDb();
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.EventType == AuditEvents.AgentVersionUploaded && l.Detail!.Contains(Sha(elf))));
    }

    [TestMethod]
    public void VersionsAreOrderedWithReleasesAboveTheirPreReleases()
    {
        Assert.IsTrue(AgentVersionOrder.Compare("1.8.0", "1.8.0-rc1") > 0);
        Assert.IsTrue(AgentVersionOrder.Compare("1.10.0", "1.9.9") > 0);
        Assert.IsTrue(AgentVersionOrder.Compare("1.8.0-rc2", "1.8.0-rc1") > 0);
        Assert.AreEqual(0, AgentVersionOrder.Compare("1.8.0", "1.8.0"));
        Assert.IsTrue(AgentVersionOrder.IsNewer("1.8.0", "1.7.3"));
        Assert.IsFalse(AgentVersionOrder.IsNewer("1.7.3", "1.7.3"));
        Assert.IsTrue(AgentVersionOrder.IsPreRelease("1.8.0-rc1"));
        foreach (var bad in new[] { "", "1.8", "1.8.0/..", "../1.8.0", "1.8.0-", "1.8.0-rc..1", "v1.8.0", "1.8.0 " })
        {
            Assert.IsFalse(AgentVersionOrder.IsValid(bad), bad);
        }
        Assert.IsTrue(AgentVersionOrder.IsValid("1.8.0-rc.1"));
        Assert.IsFalse(AgentVersionService.IsOutdated("1.8.0", "1.8.0-rc1"));
        Assert.IsTrue(AgentVersionService.IsOutdated("1.8.0-rc1", "1.8.0"));
        Assert.IsTrue(AgentVersionService.IsAhead("1.8.0", "1.8.0-rc1"));
        Assert.IsFalse(AgentVersionService.IsOutdated(null, "1.8.0"));
        Assert.IsFalse(AgentVersionService.IsOutdated("dev", "1.8.0"));
    }

    [TestMethod]
    public void TheHeaderNamesThePlatformAndTheArchitecture()
    {
        Assert.IsNull(AgentDownloads.HeaderProblem(AgentDownloadAssets.LinuxX64, Elf()));
        Assert.IsNull(AgentDownloads.HeaderProblem(AgentDownloadAssets.LinuxArm64, Elf(arm64: true)));
        Assert.IsNull(AgentDownloads.HeaderProblem(AgentDownloadAssets.WindowsX64, Pe()));
        Assert.IsNull(AgentDownloads.HeaderProblem(AgentDownloadAssets.WindowsArm64, Pe(arm64: true)));
        var thirtyTwo = Elf();
        thirtyTwo[4] = 1;
        StringAssert.Contains(AgentDownloads.HeaderProblem(AgentDownloadAssets.LinuxX64, thirtyTwo), "64-bit");
        var noPe = Pe();
        BitConverter.TryWriteBytes(noPe.AsSpan(0x3C), 100_000);
        StringAssert.Contains(AgentDownloads.HeaderProblem(AgentDownloadAssets.WindowsX64, noPe), "no PE header");
        StringAssert.Contains(AgentDownloads.HeaderProblem(AgentDownloadAssets.WindowsX64, [(byte)'M', (byte)'Z']), "no MZ header");
    }

    /// <summary>"GitHub": the list of releases (null: 503) and the releases' files by "{tag}/{name}".</summary>
    private sealed class GitHub
    {
        public volatile string? Releases = "[]";
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    }

    private sealed class GitHubHandler(GitHub github) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "github.test")
            {
                Assert.IsTrue(request.Headers.UserAgent.Count > 0, "GitHub's API needs a User-Agent");
                return Task.FromResult(github.Releases is { } json
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { ReasonPhrase = "Service Unavailable" });
            }
            var path = uri.AbsolutePath;
            const string prefix = "/releases/download/";
            return Task.FromResult(path.StartsWith(prefix, StringComparison.Ordinal) && github.Files.TryGetValue(path[prefix.Length..], out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });
        }
    }
}
