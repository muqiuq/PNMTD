using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PNMTD.Auth.Rights;
using PNMTD.Data;

namespace PNMTD.Tools;

/// <summary>
/// What a tool does (docs/mcp-plan.md §6.1). The read-only token gets <see cref="Read"/> and <see cref="Ask"/>; Cookie only
/// <see cref="Read"/>; <see cref="Delete"/> also needs the read-write token's window for deleting (§7.5).
/// </summary>
public enum ToolKind { Read, Write, Action, Delete, Ask }

/// <summary>
/// One tool of the catalog that MCP and Cookie share (docs/mcp-plan.md §6.1, docs/cookie-plan.md §3.5): its name, what it
/// does, its parameters as JSON Schema, the right it needs, its kind, and for a tool that changes something the audit
/// events of the endpoint that does the same. The runner checks the right in code, whatever a model asks.
/// </summary>
public sealed record ToolSpec(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    Area? Area,
    Level Level,
    ToolKind Kind,
    Func<JsonElement, ToolContext, PnmtdDbContext, CancellationToken, Task<object>> Run)
{
    /// <summary>
    /// Cookie gets it too (docs/cookie-plan.md §3.5): only tools that read, and only those Cookie was built with. A new tool
    /// is MCP's until someone decides Cookie should have it, so Cookie's prompt doesn't grow by itself.
    /// </summary>
    public bool ForCookie { get; init; }

    /// <summary>
    /// Only Cookie gets it, never an MCP token (docs/cookie-access-plan.md §9): PNMT's own sign-ins need <i>Audit log: View</i>,
    /// which MCP's ceiling doesn't have.
    /// </summary>
    public bool CookieOnly { get; init; }

    /// <summary>A second right it needs, as attaching a Zabbix host needs Zabbix View besides Monitoring Edit.</summary>
    public IReadOnlyList<(Area Area, Level Level)> Requires { get; init; } = [];

    /// <summary>A right only MCP callers need: Cookie's cases are Cookie's own, a person needs <i>Cookie: Use</i> for them.</summary>
    public IReadOnlyList<(Area Area, Level Level)> McpRequires { get; init; } = [];

    /// <summary>The events it may write, as <c>[Audit]</c> names them on the endpoint: required for writes, actions and deletes.</summary>
    public IReadOnlyList<string> AuditEvents { get; init; } = [];

    /// <summary>
    /// A write whose endpoint is <c>[NotAudited]</c>, with the endpoint's reason, as a like is "a reaction, not an operation"
    /// (docs/mcp-extensions-plan.md §6): it writes no audit entry; the MCP log has the call.
    /// </summary>
    public string? NotAudited { get; init; }

    /// <summary>It affects a device or removes something: MCP's <c>destructiveHint</c>, so clients ask the person first.</summary>
    public bool Destructive { get; init; }

    public bool Idempotent { get; init; }

    /// <summary>It reaches Redmine or Zabbix.</summary>
    public bool OpenWorld { get; init; }

    /// <summary>Whether it is offered at all right now: <c>cookie_ask</c> only while Jev is configured, failing closed.</summary>
    public Func<IServiceProvider, bool>? Available { get; init; }

    /// <summary>It changes something in PNMT or outside.</summary>
    public bool Writes => Kind is ToolKind.Write or ToolKind.Action or ToolKind.Delete;

    public bool Allows(UserRights rights, ToolCaller caller) =>
        (Area is not { } area || rights.Has(area, Level)) && Requires.All(r => rights.Has(r.Area, r.Level))
        && (caller != ToolCaller.Mcp || McpRequires.All(r => rights.Has(r.Area, r.Level)));

    /// <summary>What MCP asks for: "Monitoring: View", "Monitoring: Edit and Zabbix: View"; "none" for tools every token has.</summary>
    public string RightText
    {
        get
        {
            var all = (Area is { } a ? new[] { (a, Level) } : []).Concat(Requires).Concat(McpRequires).ToList();
            return all.Count == 0 ? "none" : string.Join(" and ", all.Select(r => $"{RightsCatalog.Of(r.Item1).Label}: {r.Item2}"));
        }
    }
}

/// <summary>A tool's failure the caller can read and act on: a code (<c>not_found</c>, <c>invalid_argument</c>, …) and a sentence.</summary>
public sealed class ToolException(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyDictionary<string, object?>? Extensions { get; } = extensions;

    public static ToolException NotFound(string what) => new("not_found", $"No such {what}.");

    public static ToolException Invalid(string message) => new("invalid_argument", message);
}

/// <summary>JSON Schema for the tools' parameters, and reading them back, strictly.</summary>
public static class ToolArgs
{
    public sealed record Field(string Name, string Type, string Description, bool Required = false)
    {
        public JsonObject? Extra { get; init; }
    }

    public static Field Text(string name, string description, bool required = false) => new(name, "string", description, required);

    public static Field Int(string name, string description, bool required = false) => new(name, "integer", description, required);

    public static Field Bool(string name, string description, bool required = false) => new(name, "boolean", description, required);

    public static Field Id(string name, string description, bool required = true) => new(name, "string", description, required)
    {
        Extra = new JsonObject { ["format"] = "uuid" },
    };

    public static Field Choice(string name, string description, IEnumerable<string> values, bool required = false) => new(name, "string", description, required)
    {
        Extra = new JsonObject { ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) },
    };

    public static Field Ids(string name, string description, bool required = true) => new(name, "array", description, required)
    {
        Extra = new JsonObject { ["items"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } },
    };

    /// <summary>A list of texts.</summary>
    public static Field Texts(string name, string description, bool required = false) => new(name, "array", description, required)
    {
        Extra = new JsonObject { ["items"] = new JsonObject { ["type"] = "string" } },
    };

    /// <summary>A list of texts, or null when left out; each trimmed, empty ones dropped.</summary>
    public static IReadOnlyList<string>? OptionalTexts(JsonElement args, string name, int maxItems, int maxChars)
    {
        if (!Has(args, name))
        {
            return null;
        }
        if (args.GetProperty(name) is not { ValueKind: JsonValueKind.Array } list || list.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String))
        {
            throw ToolException.Invalid($"{name} must be a list of texts.");
        }
        var texts = list.EnumerateArray().Select(i => i.GetString()!.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        return texts.Count > maxItems ? throw ToolException.Invalid($"{name} has at most {maxItems} items.")
            : texts.Any(t => t.Length > maxChars) ? throw ToolException.Invalid($"{name}'s items have at most {maxChars} characters.")
            : texts;
    }

    /// <summary>A list of texts, each one of <paramref name="values"/>.</summary>
    public static Field Choices(string name, string description, IEnumerable<string> values, bool required = false) => new(name, "array", description, required)
    {
        Extra = new JsonObject { ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) } },
    };

    /// <summary>A nested object with fields of its own, as a sensor type's settings (docs/mcp-extensions-plan.md §3).</summary>
    public static Field Nested(string name, string description, params Field[] fields) => new(name, "object", description)
    {
        Extra = Schema(fields),
    };

    /// <summary>A nested object's arguments, or null when left out.</summary>
    public static JsonElement? ObjectOf(JsonElement args, string name) =>
        !Has(args, name) ? null
            : args.GetProperty(name) is { ValueKind: JsonValueKind.Object } o ? o
            : throw ToolException.Invalid($"{name} must be an object.");

    /// <summary>A list of ids, or null when left out; an empty list is allowed.</summary>
    public static IReadOnlyList<Guid>? OptionalIds(JsonElement args, string name, int max)
    {
        if (!Has(args, name))
        {
            return null;
        }
        if (args.GetProperty(name).ValueKind != JsonValueKind.Array)
        {
            throw ToolException.Invalid($"{name} must be a list of ids.");
        }
        var ids = args.GetProperty(name).EnumerateArray()
            .Select(i => i.ValueKind == JsonValueKind.String && Guid.TryParse(i.GetString(), out var id) ? id : throw ToolException.Invalid($"{name} must be a list of ids."))
            .Distinct().ToList();
        return ids.Count <= max ? ids : throw ToolException.Invalid($"{name} has at most {max} ids.");
    }

    /// <summary>A list of enum names, or null when left out.</summary>
    public static IReadOnlyList<T>? OptionalEnums<T>(JsonElement args, string name) where T : struct, Enum
    {
        if (!Has(args, name))
        {
            return null;
        }
        if (args.GetProperty(name).ValueKind != JsonValueKind.Array)
        {
            throw ToolException.Invalid($"{name} must be a list.");
        }
        return [.. args.GetProperty(name).EnumerateArray().Select(i =>
            i.ValueKind == JsonValueKind.String && Enum.TryParse<T>(i.GetString(), ignoreCase: true, out var value) && Enum.IsDefined(value) ? value
                : throw ToolException.Invalid($"{name} holds names of {string.Join(", ", Enum.GetNames<T>())}.")).Distinct()];
    }

    /// <summary>The paging pair every list takes (§6.3).</summary>
    public static readonly Field[] PageArgs =
    [
        Int("limit", "How many, 1 to 100; 20 when left out"),
        Text("cursor", "From the previous page's nextCursor, for the next page"),
    ];

    public static JsonObject Schema(params Field[] fields)
    {
        var properties = new JsonObject();
        foreach (var f in fields)
        {
            var property = new JsonObject { ["type"] = f.Type, ["description"] = f.Description };
            if (f.Extra is { } extra)
            {
                foreach (var (key, value) in extra)
                {
                    property[key] = value?.DeepClone();
                }
            }
            properties[f.Name] = property;
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. fields.Where(f => f.Required).Select(f => (JsonNode)f.Name)]),
            ["additionalProperties"] = false,
        };
    }

    public static bool Has(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public static Guid IdOf(JsonElement args, string name = "id") =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id)
            ? id
            : throw ToolException.Invalid($"{name} must be an id from your data.");

    public static Guid? OptionalId(JsonElement args, string name) => Has(args, name) ? IdOf(args, name) : null;

    public static IReadOnlyList<Guid> IdsOf(JsonElement args, string name, int max)
    {
        if (!Has(args, name) || args.GetProperty(name).ValueKind != JsonValueKind.Array)
        {
            throw ToolException.Invalid($"{name} must be a list of ids.");
        }
        var ids = new List<Guid>();
        foreach (var item in args.GetProperty(name).EnumerateArray())
        {
            ids.Add(item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id) ? id
                : throw ToolException.Invalid($"{name} must be a list of ids."));
        }
        return ids.Count is > 0 && ids.Count <= max ? ids.Distinct().ToList() : throw ToolException.Invalid($"{name} needs 1 to {max} ids.");
    }

    public static int IntOf(JsonElement args, string name, int? fallback = null) =>
        Has(args, name) ? args.GetProperty(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : throw ToolException.Invalid($"{name} must be a number.")
            : fallback ?? throw ToolException.Invalid($"{name} must be a number.");

    public static long? OptionalLong(JsonElement args, string name) =>
        Has(args, name) ? args.GetProperty(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : throw ToolException.Invalid($"{name} must be a number.")
            : null;

    public static bool? OptionalBool(JsonElement args, string name) =>
        Has(args, name) ? args.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw ToolException.Invalid($"{name} must be true or false."),
        } : null;

    /// <summary>A text of at least <paramref name="min"/> characters, trimmed.</summary>
    public static string TextOf(JsonElement args, string name, int min = 2, int max = 4000) =>
        OptionalText(args, name, max) is { } text && text.Length >= min ? text : throw ToolException.Invalid($"{name} needs at least {min} characters.");

    public static string? OptionalText(JsonElement args, string name, int max = 4000)
    {
        if (!Has(args, name))
        {
            return null;
        }
        var v = args.GetProperty(name);
        if (v.ValueKind != JsonValueKind.String)
        {
            throw ToolException.Invalid($"{name} must be a text.");
        }
        var text = v.GetString()!.Trim();
        return text.Length <= max ? text : throw ToolException.Invalid($"{name} has at most {max} characters.");
    }

    public static T? OptionalEnum<T>(JsonElement args, string name) where T : struct, Enum =>
        OptionalText(args, name, 64) is { } text
            ? Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) ? value
                : throw ToolException.Invalid($"{name} is one of {string.Join(", ", Enum.GetNames<T>())}.")
            : null;

    public static DateTime? OptionalTime(JsonElement args, string name) =>
        OptionalText(args, name, 64) is { } text
            ? DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.UtcDateTime
                : throw ToolException.Invalid($"{name} must be a time, as 2026-11-15T09:30:00+01:00.")
            : null;
}
