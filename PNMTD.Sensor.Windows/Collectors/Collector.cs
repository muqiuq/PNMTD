using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PNMTD.Lib.Agent;

namespace PNMTD.Sensor.Windows.Collectors;

/// <summary>A section's collector (docs/windows-sensor-plan.md §4.1): what it reads, how often, and which values change too often to count.</summary>
internal abstract class Collector
{
    public abstract string Name { get; }

    /// <summary>How often it runs besides the full update every 6 hours.</summary>
    public abstract TimeSpan Interval { get; }

    /// <summary>
    /// How long one collection may take before it counts as hung (each WMI query has its own 15 s). Longer where the first pass
    /// is slow by nature: the services' signatures, the licensing service.
    /// </summary>
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(30);

    /// <summary>Properties left out of the hash: values that change all the time (free space, counters).</summary>
    public virtual IReadOnlySet<string> Volatile => EmptySet;

    protected static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    /// <summary>Reads the section. Each part in its own try/catch: what fails goes to <paramref name="errors"/>, the rest is reported.</summary>
    public abstract object Collect(List<string> errors, CancellationToken ct);

    public SectionMessage ToMessage(object data, IReadOnlyList<string> errors)
    {
        var element = AgentJson.ToElement(data);
        return new SectionMessage(Name, SectionNames.SchemaVersion, Hash(element), DateTimeOffset.UtcNow, element, errors.Count == 0 ? null : errors.Take(50).ToList());
    }

    /// <summary>The section's hash (§4.9), without its volatile values: 16 bytes of SHA-256, hex.</summary>
    private string Hash(JsonElement element)
    {
        var node = JsonNode.Parse(element.GetRawText());
        Strip(node, Volatile);
        var text = node?.ToJsonString() ?? "null";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();
    }

    private static void Strip(JsonNode? node, IReadOnlySet<string> names)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var name in o.Select(p => p.Key).Where(names.Contains).ToList())
                {
                    o.Remove(name);
                }
                foreach (var (_, child) in o)
                {
                    Strip(child, names);
                }
                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    Strip(child, names);
                }
                break;
        }
    }
}
