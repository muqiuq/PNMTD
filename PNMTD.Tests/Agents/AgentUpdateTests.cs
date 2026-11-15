using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PNMTD.Agents;
using PNMTD.Lib.Agent;
using PNMTD.Sensor.Core.State;
using PNMTD.Sensor.Core.Update;

namespace PNMTD.Tests.Agents;

/// <summary>
/// The agents' update, their platform-neutral half (docs/agent-update-plan.md §6.1, §6.2), against PNMTD itself: the offer for a
/// platform and architecture, newer or older; the download checked against the signed manifest; a manifest signed with another
/// key, of another type or expired refused; a file that doesn't match refused; what a new binary's <c>version</c> must answer.
/// </summary>
[TestClass]
public sealed class AgentUpdateTests
{
    private static DirectoryInfo folder = null!;
    private static DirectoryInfo into = null!;
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        folder = Directory.CreateTempSubdirectory("pnmtd-update-");
        into = Directory.CreateTempSubdirectory("pnmtd-update-agent-");
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
        into.Delete(recursive: true);
    }

    private static readonly IProgress<string> Quiet = new Progress<string>();

    private static async Task<AgentConfigFile> ConfigAsync()
    {
        var key = await app.Services.GetRequiredService<AgentKeyStore>().GetAsync(CancellationToken.None);
        return new AgentConfigFile("http://localhost", Guid.NewGuid(), Guid.NewGuid(), "s", "h", "k", "f", key.PublicKey, key.Fingerprint, DateTimeOffset.UtcNow,
            null, PlainHttp: true);
    }

    private static byte[] Put(string name, int size = 50_000)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        File.WriteAllBytes(Path.Combine(folder.FullName, name), bytes);
        File.WriteAllText(Path.Combine(folder.FullName, name + ".sha256"), $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {name}\n");
        return bytes;
    }

    [TestMethod]
    public async Task TheOfferIsThePlatformsVersionAndTheFileIsCheckedAgainstTheManifest()
    {
        var linux = Put(AgentDownloadAssets.LinuxArm64);
        var windows = Put(AgentDownloadAssets.WindowsX64);
        var config = await ConfigAsync();
        using var handler = app.Server.CreateHandler();

        var offer = await AgentUpdate.OfferAsync(config, AgentPlatform.Linux, "arm64", "1.0.0", Quiet, CancellationToken.None, handler);
        Assert.AreEqual(ServerInfo.Version, offer.Version);
        Assert.IsTrue(offer.Newer);
        Assert.IsFalse(offer.Older);
        Assert.AreEqual(AgentDownloadAssets.LinuxArm64, offer.AssetName);
        Assert.AreEqual("PNMT's download folder", offer.Asset!.Source);
        var candidate = await AgentUpdate.DownloadAsync(config, offer, Path.Combine(into.FullName, "linux"), f => Directory.CreateDirectory(f), Quiet,
            CancellationToken.None, handler);
        Assert.AreEqual(ServerInfo.Version, candidate.Version);
        CollectionAssert.AreEqual(linux, File.ReadAllBytes(candidate.File));

        // Windows asks for Windows' files: its own version, its own file.
        var forWindows = await AgentUpdate.OfferAsync(config, AgentPlatform.Windows, "x64", ServerInfo.Version, Quiet, CancellationToken.None, handler);
        Assert.IsFalse(forWindows.Newer, "the same version");
        Assert.IsFalse(forWindows.Older);
        var file = await AgentUpdate.DownloadAsync(config, forWindows, Path.Combine(into.FullName, "windows"), f => Directory.CreateDirectory(f), Quiet,
            CancellationToken.None, handler);
        CollectionAssert.AreEqual(windows, File.ReadAllBytes(file.File));

        // An agent newer than what its platform's agents get.
        Assert.IsTrue((await AgentUpdate.OfferAsync(config, AgentPlatform.Linux, "arm64", "99.0.0", Quiet, CancellationToken.None, handler)).Older);
        // An architecture PNMT has no file of: offered, but nothing to download.
        var x64 = await AgentUpdate.OfferAsync(config, AgentPlatform.Linux, "x64", "1.0.0", Quiet, CancellationToken.None, handler);
        Assert.IsNull(x64.Asset);
        var missing = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            AgentUpdate.DownloadAsync(config, x64, into.FullName, f => Directory.CreateDirectory(f), Quiet, CancellationToken.None, handler));
        StringAssert.Contains(missing.Message, $"has no {AgentDownloadAssets.LinuxX64}");

        // Plain http:// only when the join confirmed it.
        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            AgentUpdate.OfferAsync(config with { PlainHttp = false }, AgentPlatform.Linux, "arm64", "1.0.0", Quiet, CancellationToken.None, handler));
        StringAssert.Contains(refused.Message, "is not https://");
    }

    [TestMethod]
    public async Task AFileThatDoesntMatchTheManifestIsRefused()
    {
        var config = await ConfigAsync();
        var offer = new UpdateOffer("1.8.0", false, AgentDownloadAssets.LinuxX64,
            new DownloadAsset(AgentDownloadAssets.LinuxX64, 1000, new string('a', 64)), true, false);
        using var other = new Answer(RandomNumberGenerator.GetBytes(1000));
        var mismatch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            AgentUpdate.DownloadAsync(config, offer, into.FullName, f => Directory.CreateDirectory(f), Quiet, CancellationToken.None, other));
        StringAssert.Contains(mismatch.Message, "doesn't match PNMT's signed manifest");
        using var larger = new Answer(RandomNumberGenerator.GetBytes(2000));
        var tooLarge = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            AgentUpdate.DownloadAsync(config, offer, into.FullName, f => Directory.CreateDirectory(f), Quiet, CancellationToken.None, larger));
        StringAssert.Contains(tooLarge.Message, "larger than the manifest says");
    }

    [TestMethod]
    public void AManifestIsTrustedOnlyWithThePinnedKeyItsTypeAndBeforeItExpires()
    {
        using var pnmtd = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = new DownloadManifest("1.8.0", [new DownloadAsset(AgentDownloadAssets.LinuxX64, 10, new string('b', 64), "GitHub's release sensor-1.8.0")],
            AgentPlatform.Linux);
        DownloadManifestResponse Signed(ECDsa key, string type = AgentMessageTypes.DownloadManifest, Guid? agent = null, TimeSpan? lifetime = null) =>
            new("f", AgentSigning.Sign(key, type, agent ?? Guid.Empty, manifest, lifetime ?? AgentProtocol.ManifestLifetime));

        var now = DateTimeOffset.UtcNow;
        Assert.AreEqual("1.8.0", AgentUpdate.Verify(Signed(pnmtd), pnmtd, now).Version);
        Assert.AreEqual(AgentPlatform.Linux, AgentUpdate.Verify(Signed(pnmtd), pnmtd, now).Platform);
        Assert.ThrowsExactly<InvalidOperationException>(() => AgentUpdate.Verify(Signed(other), pnmtd, now));
        Assert.ThrowsExactly<InvalidOperationException>(() => AgentUpdate.Verify(Signed(pnmtd, AgentMessageTypes.Config), pnmtd, now));
        Assert.ThrowsExactly<InvalidOperationException>(() => AgentUpdate.Verify(Signed(pnmtd, agent: Guid.NewGuid()), pnmtd, now));
        var expired = Assert.ThrowsExactly<InvalidOperationException>(() => AgentUpdate.Verify(Signed(pnmtd), pnmtd, now.AddMinutes(11)));
        StringAssert.Contains(expired.Message, "expired");
        // A PNMT before 1.8 signs no expiry: still trusted.
        var old = new DownloadManifestResponse("f", AgentSigning.Sign(pnmtd, AgentMessageTypes.DownloadManifest, Guid.Empty, new DownloadManifest("1.7.0", [])));
        Assert.AreEqual("1.7.0", AgentUpdate.Verify(old, pnmtd, now.AddDays(30)).Version);
    }

    [TestMethod]
    public void ANewBinaryMustAnswerAsThisAgentForThisArchitectureAndTheManifestsVersion()
    {
        Assert.IsNull(AgentUpdate.VersionProblem("PNMT agent 1.8.0 (arm64)\n", "PNMT agent", "arm64", "1.8.0"));
        Assert.IsNull(AgentUpdate.VersionProblem("PNMT agent 1.8.0 (arm64)", "PNMT agent", "arm64", null));
        Assert.IsNull(AgentUpdate.VersionProblem("PNMT Sensor 1.8.0-rc1 (x64)", "PNMT Sensor", "x64", "1.8.0-rc1"));
        StringAssert.Contains(AgentUpdate.VersionProblem("PNMT agent 1.7.0 (arm64)", "PNMT agent", "arm64", "1.8.0"), "manifest says 1.8.0");
        StringAssert.Contains(AgentUpdate.VersionProblem("PNMT agent 1.8.0 (x64)", "PNMT agent", "arm64", "1.8.0"), "for x64");
        StringAssert.Contains(AgentUpdate.VersionProblem("PNMT Sensor 1.8.0 (x64)", "PNMT agent", "x64", null), "doesn't answer as a PNMT agent");
        StringAssert.Contains(AgentUpdate.VersionProblem("PNMT agent dev (x64)", "PNMT agent", "x64", null), "can't be read");
        Assert.AreEqual("1.8.0", AgentUpdate.VersionOf("PNMT agent 1.8.0 (arm64)", "PNMT agent"));
        Assert.IsNull(AgentUpdate.VersionOf("bash: not found", "PNMT agent"));
    }

    /// <summary>Answers every request with these bytes.</summary>
    private sealed class Answer(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
}
