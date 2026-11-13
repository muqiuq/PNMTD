using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace PNMTD.Ai;

/// <summary>
/// The JSON a model reads and writes (docs/ai-operations-plan.md §3.3): camelCase properties, kebab-case enums, and
/// a strict schema generated from the output record: every property required (nullable where a value may be absent),
/// no additional properties.
/// </summary>
public static class AiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        RespectNullableAnnotations = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // The web defaults read numbers from strings, which makes every number "string or number" with a pattern in the schema.
        NumberHandling = JsonNumberHandling.Strict,
        // Model input, not HTML: placeholders such as <ip:1> stay readable instead of \u003Cip:1\u003E.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>The strict schema of <typeparamref name="T"/>, as structured outputs take it.</summary>
    public static JsonObject SchemaFor<T>() => SchemaFor(typeof(T));

    public static JsonObject SchemaFor(Type type)
    {
        var node = Options.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        var schema = node as JsonObject ?? throw new InvalidOperationException($"No object schema for {type.Name}");
        Strict(schema);
        return schema;
    }

    /// <summary>additionalProperties false and every property required, on every object, as strict mode demands.</summary>
    private static void Strict(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject properties)
                {
                    obj["additionalProperties"] = false;
                    obj["required"] = new JsonArray(properties.Select(p => (JsonNode)JsonValue.Create(p.Key)!).ToArray());
                }
                foreach (var (key, child) in obj.ToList())
                {
                    // A default makes some providers refuse the schema.
                    if (key == "default")
                    {
                        obj.Remove(key);
                        continue;
                    }
                    Strict(child);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Strict(item);
                }
                break;
        }
    }
}
