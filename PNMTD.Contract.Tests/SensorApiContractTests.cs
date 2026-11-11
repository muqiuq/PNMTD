using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PNMTD.Contract.Tests.Harness;

namespace PNMTD.Contract.Tests;

/// <summary>
/// The sensor API must answer exactly as v0.12.1 does (docs/webui-migration-plan.md §3.3, §8.1).
/// Driven by tools/contract.sh, which starts the image under test with a fresh copy of the fixture
/// database and sets these variables:
/// <list type="bullet">
/// <item><c>CONTRACT_BASE_URL</c>: the running app. Without it the test is skipped.</item>
/// <item><c>CONTRACT_MODE</c>: <c>verify</c> (default), <c>record</c> (writes the golden file),
///   <c>make-fixture</c> (creates the fixture sensors through the v0.12 API) or
///   <c>make-import-fixture</c> (the importer test's database: the cases' events plus rules and a zone).</item>
/// <item><c>CONTRACT_DB_CONNECTION</c>: the 1.0 database, to read the stored state from. Without it,
///   v0.12's management API (the baseline).</item>
/// <item><c>CONTRACT_GOLDEN</c>, <c>CONTRACT_ACTUAL</c>: file paths; defaults below.</item>
/// </list>
/// </summary>
[TestClass]
public sealed class SensorApiContractTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PNMTD.Contract.Tests", "PNMTD.Contract.Tests.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    private static string GoldenPath => Environment.GetEnvironmentVariable("CONTRACT_GOLDEN")
        ?? Path.Combine(RepoRoot(), "PNMTD.Contract.Tests", "golden", "sensor-api.json");

    private static string ActualPath => Environment.GetEnvironmentVariable("CONTRACT_ACTUAL")
        ?? Path.Combine(RepoRoot(), "artifacts", "contract", "actual.json");

    private static HttpClient CreateClient(string baseUrl) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
        })
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };

    [TestMethod]
    public async Task SensorApiMatchesGoldenContract()
    {
        var baseUrl = Environment.GetEnvironmentVariable("CONTRACT_BASE_URL");
        if (string.IsNullOrEmpty(baseUrl))
        {
            Assert.Inconclusive("CONTRACT_BASE_URL is not set: run tools/contract.sh verify");
        }
        var mode = Environment.GetEnvironmentVariable("CONTRACT_MODE") ?? "verify";

        using var client = CreateClient(baseUrl);
        using var apiClient = CreateClient(baseUrl);
        var legacy = new LegacyApi(apiClient);

        if (mode == "make-fixture")
        {
            await legacy.CreateFixtureAsync();
            return;
        }

        var normalizer = new Normalizer();
        var cases = await new Recorder(client).RunAsync(Cases.All, normalizer);

        if (mode == "make-import-fixture")
        {
            // The contract's events plus rules, a zone and a key value; then the tasks get a moment
            // to write their rows (notifications every 10 s, DNS every 15 s).
            await ImportFixture.CreateExtrasAsync(legacy);
            await Task.Delay(TimeSpan.FromSeconds(40));
            return;
        }
        // The background tasks run every 10–15 s; none of them touches these sensors (see Fixture).
        // 1.0 has no management API at the root any more: its state comes from the database.
        var databaseConnection = Environment.GetEnvironmentVariable("CONTRACT_DB_CONNECTION");
        IStateProbe probe = string.IsNullOrEmpty(databaseConnection)
            ? new LegacyApiProbe(legacy)
            : new DatabaseProbe(databaseConnection);
        var recording = new ContractRecording
        {
            RecordedFrom = Environment.GetEnvironmentVariable("CONTRACT_IMAGE") ?? baseUrl,
            Cases = cases,
            State = await probe.CaptureAsync(),
        };

        if (mode == "record")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            await File.WriteAllTextAsync(GoldenPath, JsonSerializer.Serialize(recording, Json) + "\n");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ActualPath)!);
        await File.WriteAllTextAsync(ActualPath, JsonSerializer.Serialize(recording, Json) + "\n");

        var golden = JsonSerializer.Deserialize<ContractRecording>(await File.ReadAllTextAsync(GoldenPath), Json)
            ?? throw new InvalidOperationException("Empty golden file");
        var report = Compare(golden, recording);
        if (report.Length > 0)
        {
            Assert.Fail($"The sensor API differs from the golden contract ({GoldenPath}).\n"
                + $"Actual recording: {ActualPath}\n\n{report}");
        }
    }

    private static string Compare(ContractRecording expected, ContractRecording actual)
    {
        var sb = new StringBuilder();
        var actualCases = actual.Cases.ToDictionary(c => c.Id);
        foreach (var e in expected.Cases)
        {
            if (!actualCases.TryGetValue(e.Id, out var a))
            {
                sb.AppendLine($"- {e.Id}: not run (the case list changed?)");
                continue;
            }
            var expectedRequest = JsonSerializer.Serialize(e.Request, Json);
            if (expectedRequest != JsonSerializer.Serialize(a.Request, Json))
            {
                sb.AppendLine($"- {e.Id}: the request differs from the recorded one; re-record the baseline instead of editing a case");
                continue;
            }
            var expectedResponse = JsonSerializer.Serialize(e.Response, Json);
            var actualResponse = JsonSerializer.Serialize(a.Response, Json);
            if (expectedResponse != actualResponse)
            {
                sb.AppendLine($"- {e.Id} ({e.Request.Method} {e.Request.Path})");
                sb.AppendLine($"  expected: {OneLine(expectedResponse)}");
                sb.AppendLine($"  actual:   {OneLine(actualResponse)}");
            }
        }
        foreach (var extra in actual.Cases.Where(c => expected.Cases.All(e => e.Id != c.Id)))
        {
            sb.AppendLine($"- {extra.Id}: not in the golden file; record the baseline again");
        }
        foreach (var (alias, e) in expected.State)
        {
            var expectedState = JsonSerializer.Serialize(e, Json);
            var actualState = actual.State.TryGetValue(alias, out var a) ? JsonSerializer.Serialize(a, Json) : "<missing>";
            if (expectedState != actualState)
            {
                sb.AppendLine($"- state of sensor {alias}");
                sb.AppendLine($"  expected: {OneLine(expectedState)}");
                sb.AppendLine($"  actual:   {OneLine(actualState)}");
            }
        }
        return sb.ToString();
    }

    private static string OneLine(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
