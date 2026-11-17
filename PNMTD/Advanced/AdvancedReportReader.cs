using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PNMTD.Advanced;

/// <summary>
/// A report as read (docs/advanced-sensor-plan.md §4.2): valid or why not, the text to store, and the three keys PNMT reads
/// (<c>status</c>, <c>message</c>, <c>time</c>). Owns the parsed document.
/// </summary>
public sealed class AdvancedParsedReport : IDisposable
{
    public bool Valid { get; init; }

    public AdvancedReportError? Kind { get; init; }

    public string? Error { get; init; }

    public JsonDocument? Document { get; init; }

    /// <summary>The body as stored: UTF-8 without a BOM, or null for a report too large to keep.</summary>
    public string? Text { get; init; }

    /// <summary>The size as received (or as announced, for one too large to read).</summary>
    public int Bytes { get; init; }

    /// <summary>Hex SHA-256 of the body as received; empty for one too large to read.</summary>
    public string Sha256 { get; init; } = "";

    public string? Status { get; init; }

    public string? Message { get; init; }

    public DateTime? MeasuredAt { get; init; }

    public AdvancedReportInput Input => new(Valid, Kind, Error, Document?.RootElement);

    public void Dispose() => Document?.Dispose();
}

/// <summary>Reads a report's bytes within the limits of §4.2: UTF-8, a BOM skipped, depth 32, no key twice, no comments.</summary>
public static class AdvancedReportReader
{
    public static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = AdvancedLimits.MaxDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    private static readonly Regex Position = new(@"\s*LineNumber: \d+ \| BytePositionInLine: \d+\.?$", RegexOptions.CultureInvariant);

    private static readonly Regex Duplicate = new(@"Duplicate property '(.*)' encountered", RegexOptions.CultureInvariant);

    /// <summary>A report that was refused for its size before or while it was read.</summary>
    public static AdvancedParsedReport TooLarge(long? size, int maxBytes) => new()
    {
        Valid = false,
        Kind = AdvancedReportError.TooLarge,
        Error = size is { } s
            ? $"The report is {Size(s)}, more than {Size(maxBytes)}"
            : $"The report is more than {Size(maxBytes)}",
        Bytes = (int)Math.Min(int.MaxValue, size ?? maxBytes + 1L),
    };

    public static AdvancedParsedReport Read(ReadOnlySpan<byte> received)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(received));
        var body = received;
        if (body.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            body = body[3..];
        }
        var size = received.Length;
        var text = Encoding.UTF8.GetString(body).Replace('\0', '�');
        AdvancedParsedReport Invalid(AdvancedReportError kind, string error) => new()
        {
            Valid = false, Kind = kind, Error = AdvancedValues.Cut(error, AdvancedLimits.MaxError), Text = text, Bytes = size, Sha256 = sha,
        };
        if (body.Trim((ReadOnlySpan<byte>)[0x20, 0x09, 0x0A, 0x0D]).IsEmpty)
        {
            return Invalid(AdvancedReportError.NotJson, "The report is empty: send a JSON object, such as {\"status\":\"ok\"}");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray(), Options);
        }
        catch (JsonException e)
        {
            var duplicate = Duplicate.Match(e.Message);
            if (duplicate.Success)
            {
                return Invalid(AdvancedReportError.DuplicateKey, $"The report has the key \"{AdvancedValues.Cut(AdvancedValues.Clean(duplicate.Groups[1].Value), 100)}\" twice");
            }
            if (e.Message.Contains("maximum configured depth", StringComparison.Ordinal))
            {
                return Invalid(AdvancedReportError.TooDeep, $"The report is nested more than {AdvancedLimits.MaxDepth} levels deep");
            }
            var reason = Position.Replace(e.Message, "").Trim();
            var where = e.LineNumber is { } line ? $"line {line + 1}, position {(e.BytePositionInLine ?? 0) + 1}: " : "";
            return Invalid(AdvancedReportError.NotJson, $"The report isn't valid JSON: {where}{reason}");
        }
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            var kind = root.ValueKind switch
            {
                JsonValueKind.Array => "an array",
                JsonValueKind.String => "a string",
                JsonValueKind.Number => "a number",
                JsonValueKind.Null => "null",
                _ => "a boolean",
            };
            document.Dispose();
            return Invalid(AdvancedReportError.NotObject, $"The report must be a JSON object, not {kind}");
        }
        string? status = null;
        string? message = null;
        DateTime? measuredAt = null;
        if (root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
        {
            status = Nonempty(AdvancedValues.Cut(AdvancedValues.Clean(s.GetString() ?? ""), AdvancedLimits.MaxStatus));
        }
        if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
        {
            message = Nonempty(AdvancedValues.Cut(AdvancedValues.Clean(m.GetString() ?? ""), AdvancedLimits.MaxMessage));
        }
        if (root.TryGetProperty("time", out var t) && AdvancedValues.TryTime(t, out var time, out _))
        {
            measuredAt = time;
        }
        return new AdvancedParsedReport
        {
            Valid = true, Document = document, Text = text, Bytes = received.Length, Sha256 = sha, Status = status, Message = message, MeasuredAt = measuredAt,
        };
    }

    /// <summary>A stored report again, for the evaluator and the tabs.</summary>
    public static JsonDocument? Parse(string? body)
    {
        if (body is null)
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(body, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Nonempty(string s) => s.Length == 0 ? null : s;

    /// <summary><c>812 bytes</c>, <c>256 KiB</c>, <c>1.2 MiB</c>.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString(bytes % 1024 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture)} KiB",
        _ => $"{(bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture)} MiB",
    };
}
