using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PNMTD.Mikrotik;

/// <summary>
/// What the router has that its join script would change (docs/mikrotik-plan.md §3.3), keyed (docs/mikrotik-join-options-plan.md
/// §3.2): <see cref="Seen"/> is the router's side, <see cref="Expected"/> PNMT's. An acknowledgement covers a difference exactly as it
/// was seen, so either side changing makes it a new one.
/// </summary>
public sealed record ScriptDifference(string Key, string Text, string Seen, string Expected)
{
    /// <summary>The key and the first 16 hex digits of SHA-256 over the key and both sides: "endpoint-port:3f2a…".</summary>
    public string Id => $"{Key}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Key}\n{Seen}\n{Expected}")))[..16]}";

    /// <summary>About a setting only an Admin may change, so only an Admin may acknowledge it (§3.1).</summary>
    public bool Admin => ScriptDifferences.IsAdmin(Key);
}

/// <summary>An acknowledged difference (§3.3), as <c>mikrotik_routers.AcknowledgedDifferences</c> stores it.</summary>
public sealed record DifferenceAcknowledgement(string Id, DateTime At, Guid? ById, string? ByName, string Reason);

public static class ScriptDifferences
{
    public const int MaxReason = 500;

    /// <summary>
    /// The keys of the settings that widen what PNMT may do on the router (secrets, diagnostics, management access) and of
    /// PNMTD's retired keys: the same people who may change those may acknowledge them, as <c>MikrotikRouterService.Validate</c> says.
    /// </summary>
    private static readonly HashSet<string> AdminKeys = ["management-rule", "policy-sensitive", "policy-test", "retired-key"];

    /// <summary>For a difference's key, or an id ("policy-test:3f2a…").</summary>
    public static bool IsAdmin(string keyOrId) => AdminKeys.Contains(keyOrId.Split(':')[0]);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<DifferenceAcknowledgement> Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<DifferenceAcknowledgement>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? Write(IReadOnlyCollection<DifferenceAcknowledgement> acknowledgements) =>
        acknowledgements.Count == 0 ? null : JsonSerializer.Serialize(acknowledgements, Json);

    /// <summary>Each difference with its acknowledgement, if one covers it as it is now. Acknowledgements of what is gone do nothing.</summary>
    public static IReadOnlyList<(ScriptDifference Difference, DifferenceAcknowledgement? Acknowledgement)> Match(
        IReadOnlyList<ScriptDifference> differences, IReadOnlyList<DifferenceAcknowledgement> acknowledgements)
    {
        var byId = acknowledgements.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.Last());
        return differences.Select(d => (d, byId.GetValueOrDefault(d.Id))).ToList();
    }

    /// <summary>The differences no acknowledgement covers: what "script outdated" means (§3.4).</summary>
    public static IReadOnlyList<ScriptDifference> Open(IReadOnlyList<ScriptDifference> differences, string? acknowledgements) =>
        Match(differences, Read(acknowledgements)).Where(m => m.Acknowledgement is null).Select(m => m.Difference).ToList();
}
