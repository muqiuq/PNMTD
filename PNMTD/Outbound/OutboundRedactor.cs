using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PNMTD.Outbound;

/// <summary>
/// PNMT's own secrets out of the outbound logs (docs/redmine-queue-outbound-plan.md §6.5), before a record leaves memory: the
/// Redmine keys and other credentials in headers, Pushover's app token and user key, and Redmine's private notes while PNMT
/// doesn't show them. URLs are stored as they are, webhook URLs included (D18).
/// </summary>
public static class OutboundRedactor
{
    public const string Masked = "•••";

    public const string PrivateNote = "(private note, not stored: Redmine__ShowPrivateNotes)";

    /// <summary>Readable JSON: "•••" and umlauts as they are, not as <c>\u</c> escapes.</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> SecretHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Redmine-API-Key", "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
    };

    /// <summary>"•••" and the last 4 characters, which tell two keys apart without giving either away.</summary>
    public static string Mask(string? value) => string.IsNullOrEmpty(value) ? Masked : value.Length <= 8 ? Masked : Masked + value[^4..];

    public static bool IsSecretHeader(string name) => SecretHeaders.Contains(name);

    /// <summary>A request's or a response's headers as <c>[[name, value], …]</c>, the secret ones masked.</summary>
    public static List<string[]> Headers(HttpHeaders? headers, HttpHeaders? contentHeaders = null)
    {
        var list = new List<string[]>();
        foreach (var source in new[] { headers, contentHeaders })
        {
            if (source is null)
            {
                continue;
            }
            foreach (var (name, values) in source)
            {
                foreach (var value in values)
                {
                    list.Add([name, IsSecretHeader(name) ? Mask(value) : value]);
                }
            }
        }
        return list;
    }

    public static string Json(List<string[]> headers) => JsonSerializer.Serialize(headers, Readable);

    /// <summary><c>pushover://</c> and the user key, masked but for its last 4 characters.</summary>
    public static string PushoverRecipient(string recipient) =>
        recipient.StartsWith(Notifications.PushoverNotification.Prefix, StringComparison.Ordinal)
            ? Notifications.PushoverNotification.Prefix + Mask(Notifications.PushoverNotification.UserKey(recipient))
            : recipient;

    /// <summary>Pushover's body: the app token masked, the user key but for its last 4 characters. Not JSON: as it is.</summary>
    public static string? PushoverBody(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }
        try
        {
            if (JsonNode.Parse(body) is not JsonObject json)
            {
                return body;
            }
            foreach (var field in new[] { "token", "user" })
            {
                if (json[field] is JsonValue value && value.TryGetValue<string>(out var secret))
                {
                    json[field] = Mask(secret);
                }
            }
            return json.ToJsonString(Readable);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    /// <summary>
    /// Redmine's answer with the notes of every private journal replaced (<c>issue</c> or <c>issues</c>). Not JSON, or nothing
    /// private in it: as it is.
    /// </summary>
    public static string? WithoutPrivateNotes(string? body)
    {
        if (string.IsNullOrEmpty(body) || !body.Contains("\"private_notes\"", StringComparison.Ordinal))
        {
            return body;
        }
        try
        {
            if (JsonNode.Parse(body) is not JsonObject json)
            {
                return body;
            }
            var issues = new List<JsonObject>();
            if (json["issue"] is JsonObject one)
            {
                issues.Add(one);
            }
            if (json["issues"] is JsonArray many)
            {
                issues.AddRange(many.OfType<JsonObject>());
            }
            foreach (var issue in issues)
            {
                foreach (var journal in (issue["journals"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (journal["private_notes"] is JsonValue p && p.TryGetValue<bool>(out var isPrivate) && isPrivate && journal["notes"] is not null)
                    {
                        journal["notes"] = PrivateNote;
                    }
                }
            }
            return json.ToJsonString(Readable);
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
