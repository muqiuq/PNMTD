using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PNMTD.Tools;

/// <summary>
/// A tool's result as JSON within a budget of characters (docs/mcp-plan.md §6.3), and never cut in the middle: a list
/// gives up its last items, a long text its middle, until the result fits, and then says so with <c>truncated</c>.
/// </summary>
public static class ToolResults
{
    /// <summary>
    /// Web JSON, nulls left out, and umlauts and "…" as they are: a result is JSON inside JSON-RPC or a model's message,
    /// never HTML, and escaped text costs a model tokens and reads worse.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The note a shortened result carries.</summary>
    public const string TruncatedNote = "Shortened to fit: ask for fewer (limit), the next page (cursor), or one object at a time.";

    public static (string Text, JsonNode? Node, bool Truncated) Serialize(object? result, int maxChars)
    {
        var node = JsonSerializer.SerializeToNode(result, Json);
        var text = node?.ToJsonString(Json) ?? "null";
        if (text.Length <= maxChars || node is null)
        {
            return (text, node, false);
        }
        for (var round = 0; round < 400 && text.Length > maxChars; round++)
        {
            var over = text.Length - maxChars;
            if (!ShortenList(node, over) && !ShortenText(node))
            {
                break;
            }
            text = node.ToJsonString(Json);
        }
        node = Marked(node);
        text = node.ToJsonString(Json);
        return (text, node, true);
    }

    private static JsonNode Marked(JsonNode node)
    {
        if (node is JsonObject o)
        {
            o["truncated"] = true;
            o["note"] = TruncatedNote;
            return o;
        }
        var wrapped = new JsonObject { ["items"] = node.DeepClone(), ["truncated"] = true, ["note"] = TruncatedNote };
        return wrapped;
    }

    /// <summary>The longest list (by its JSON) with more than one item loses items in proportion to what is too much.</summary>
    private static bool ShortenList(JsonNode root, int over)
    {
        JsonArray? longest = null;
        var longestLength = 0;
        foreach (var array in Walk(root).OfType<JsonArray>())
        {
            if (array.Count < 2)
            {
                continue;
            }
            var length = array.ToJsonString(Json).Length;
            if (length > longestLength)
            {
                (longest, longestLength) = (array, length);
            }
        }
        if (longest is null)
        {
            return false;
        }
        var perItem = Math.Max(1, longestLength / longest.Count);
        var remove = Math.Clamp((over / perItem) + 1, 1, longest.Count - 1);
        for (var i = 0; i < remove; i++)
        {
            longest.RemoveAt(longest.Count - 1);
        }
        return true;
    }

    /// <summary>The longest text above 200 characters keeps its head and tail, half as long.</summary>
    private static bool ShortenText(JsonNode root)
    {
        JsonValue? longest = null;
        var longestLength = 200;
        foreach (var value in Walk(root).OfType<JsonValue>())
        {
            if (value.TryGetValue<string>(out var s) && s.Length > longestLength)
            {
                (longest, longestLength) = (value, s.Length);
            }
        }
        if (longest is null)
        {
            return false;
        }
        var text = longest.GetValue<string>();
        var cut = Ai.InputSanitizer.HeadAndTail(text, text.Length / 4);
        ReplaceValue(longest, JsonValue.Create(cut));
        return true;
    }

    private static void ReplaceValue(JsonNode old, JsonNode replacement)
    {
        switch (old.Parent)
        {
            case JsonObject o:
                o[old.GetPropertyName()] = replacement;
                break;
            case JsonArray a:
                a[old.GetElementIndex()] = replacement;
                break;
        }
    }

    private static IEnumerable<JsonNode> Walk(JsonNode node)
    {
        yield return node;
        switch (node)
        {
            case JsonObject o:
                foreach (var (_, child) in o.ToList())
                {
                    if (child is not null)
                    {
                        foreach (var n in Walk(child))
                        {
                            yield return n;
                        }
                    }
                }
                break;
            case JsonArray a:
                foreach (var child in a.ToList())
                {
                    if (child is not null)
                    {
                        foreach (var n in Walk(child))
                        {
                            yield return n;
                        }
                    }
                }
                break;
        }
    }
}
