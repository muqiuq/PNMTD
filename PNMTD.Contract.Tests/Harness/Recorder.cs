using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PNMTD.Contract.Tests.Harness;

public sealed class RecordedRequest
{
    public required string Method { get; init; }
    /// <summary>The path template, with token placeholders rather than tokens.</summary>
    public required string Path { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ContentType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Accept { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Body { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BodyBase64 { get; init; }
}

public sealed class RecordedResponse
{
    public required int Status { get; init; }
    /// <summary>Every response header except Date, names lower-case, sorted.</summary>
    public required SortedDictionary<string, string> Headers { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Body { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BodyBase64 { get; init; }
}

public sealed class CaseResult
{
    public required string Id { get; init; }
    public required RecordedRequest Request { get; init; }
    public required RecordedResponse Response { get; init; }
}

public sealed class RecordedEvent
{
    public required int Code { get; init; }
    public string? Message { get; init; }
    public required string Source { get; init; }
}

public sealed class RecordedSibling
{
    public string? Name { get; init; }
    public required int Type { get; init; }
    public required bool Enabled { get; init; }
    public required bool Ignore { get; init; }
    public required int Interval { get; init; }
    public required int GracePeriod { get; init; }
    public string? Parameters { get; init; }
    public required List<RecordedEvent> Events { get; init; }
}

public sealed class RecordedSensorState
{
    public required List<RecordedEvent> Events { get; init; }
    public required List<RecordedSibling> Siblings { get; init; }
}

public sealed class ContractRecording
{
    public required string RecordedFrom { get; init; }
    public required List<CaseResult> Cases { get; init; }
    /// <summary>What the cases left in the database, per fixture sensor alias.</summary>
    public required SortedDictionary<string, RecordedSensorState> State { get; init; }
}

/// <summary>
/// Makes recordings comparable across runs: trace ids and generated GUIDs differ every time.
/// GUIDs become <c>&lt;guid#n&gt;</c> numbered by first appearance, so "the same id as before" survives,
/// which is how the dedupe rule shows up in a response.
/// </summary>
public sealed partial class Normalizer
{
    private readonly Dictionary<string, int> guids = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("00-[0-9a-f]{32}-[0-9a-f]{16}-0[0-9a-f]")]
    private static partial Regex TraceId();

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();

    [GeneratedRegex(@"^(.+):(\d+)$")]
    private static partial Regex IpAndPort();

    public string Text(string text)
    {
        text = TraceId().Replace(text, "<traceId>");
        return Guid().Replace(text, m =>
        {
            if (!guids.TryGetValue(m.Value, out var n))
            {
                n = guids.Count + 1;
                guids[m.Value] = n;
            }
            return $"<guid#{n}>";
        });
    }

    /// <summary>The event's Source is the client's "ip:port"; only its shape is part of the contract.</summary>
    public static string Source(string? source) =>
        source is null ? "<null>" : IpAndPort().Replace(source, "<ip>:<port>");
}

public sealed class Recorder(HttpClient client)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string ResolvePath(string template) =>
        Regex.Replace(template, @"\{([wr]):([A-Za-z0-9]+)\}", m =>
        {
            var sensor = Fixture.Get(m.Groups[2].Value);
            return m.Groups[1].Value == "w" ? sensor.WriteToken : sensor.ReadToken;
        });

    public async Task<List<CaseResult>> RunAsync(IEnumerable<ContractCase> cases, Normalizer normalizer)
    {
        var results = new List<CaseResult>();
        foreach (var c in cases)
        {
            if (c.DelayBeforeMs > 0)
            {
                await Task.Delay(c.DelayBeforeMs);
            }
            results.Add(await RunOneAsync(c, normalizer));
        }
        return results;
    }

    private async Task<CaseResult> RunOneAsync(ContractCase c, Normalizer normalizer)
    {
        using var request = new HttpRequestMessage(new HttpMethod(c.Method), new Uri(client.BaseAddress!, ResolvePath(c.Path)));
        var bodyBytes = c.RawBody ?? (c.Body is null ? null : Encoding.UTF8.GetBytes(c.Body));
        if (bodyBytes is not null)
        {
            request.Content = new ByteArrayContent(bodyBytes);
            if (c.ContentType is not null)
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(c.ContentType);
            }
        }
        if (c.Accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", c.Accept);
        }

        using var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            var lower = name.ToLowerInvariant();
            if (lower == "date") continue;
            headers[lower] = string.Join(", ", values);
        }

        string? text = null, base64 = null;
        if (bytes.Length > 0)
        {
            try { text = normalizer.Text(StrictUtf8.GetString(bytes)); }
            catch (DecoderFallbackException) { base64 = Convert.ToBase64String(bytes); }
        }

        return new CaseResult
        {
            Id = c.Id,
            Request = new RecordedRequest
            {
                Method = c.Method,
                Path = c.Path,
                ContentType = c.ContentType,
                Accept = c.Accept,
                Body = c.RawBody is null ? c.Body : null,
                BodyBase64 = c.RawBody is null ? null : Convert.ToBase64String(c.RawBody),
            },
            Response = new RecordedResponse { Status = (int)response.StatusCode, Headers = headers, Body = text, BodyBase64 = base64 },
        };
    }
}
