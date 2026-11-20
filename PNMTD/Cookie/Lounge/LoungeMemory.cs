using PNMTD.Models.Db;

namespace PNMTD.Cookie.Lounge;

/// <summary>
/// The voices' memory (docs/lounge-plan.md §7), its arithmetic pure: a memory's strength fades with a half-life, is refreshed when the
/// conversation touches it, and is forgotten when weak; three keepsakes per voice never fade.
/// </summary>
public static class LoungeMemory
{
    /// <summary>Below this a memory is forgotten in the sweep: after about 19 days untouched at a half-life of 7.</summary>
    public const double ForgetBelow = 0.15;

    /// <summary>What touching a memory adds (§7.1), up to 1.</summary>
    public const double Refresh = 0.5;

    public const int MaxPerVoice = 100;

    public const int Keepsakes = 3;

    public const int MaxChars = 200;

    public static readonly IReadOnlyList<string> Kinds = ["moment", "person", "joke", "lesson"];

    /// <summary><c>s = s₀ × 0.5^(Δt / HalfLife)</c>; a keepsake is always 1.</summary>
    public static double Strength(LoungeMemoryEntity m, DateTime now, double halfLifeDays) =>
        m.Keepsake ? 1 : Strength(m.Strength0, m.RefreshedAt, now, halfLifeDays);

    public static double Strength(double strength0, DateTime refreshedAt, DateTime now, double halfLifeDays)
    {
        var days = Math.Max(0, (now - refreshedAt).TotalDays);
        return strength0 * Math.Pow(0.5, days / Math.Max(0.01, halfLifeDays));
    }

    /// <summary>Touched by the conversation (§7.1): <c>s₀ = min(1, s + 0.5)</c>, refreshed now, one more recall.</summary>
    public static void Touch(LoungeMemoryEntity m, DateTime now, double halfLifeDays)
    {
        if (m.Keepsake)
        {
            m.Recalls++;
            return;
        }
        m.Strength0 = Math.Min(1, Strength(m, now, halfLifeDays) + Refresh);
        m.RefreshedAt = now;
        m.Recalls++;
    }

    /// <summary>The sweep (§7.3): what is forgotten, the weak ones first, and beyond <see cref="MaxPerVoice"/> the weakest. Never a keepsake.</summary>
    public static IReadOnlyList<LoungeMemoryEntity> Swept(IReadOnlyList<LoungeMemoryEntity> memories, DateTime now, double halfLifeDays)
    {
        var ordinary = memories.Where(m => !m.Keepsake).Select(m => (Memory: m, Strength: Strength(m, now, halfLifeDays))).ToList();
        var weak = ordinary.Where(x => x.Strength < ForgetBelow).Select(x => x.Memory).ToList();
        var kept = ordinary.Where(x => x.Strength >= ForgetBelow).OrderByDescending(x => x.Strength).ThenByDescending(x => x.Memory.Id).ToList();
        var room = Math.Max(0, MaxPerVoice - memories.Count(m => m.Keepsake));
        return [.. weak, .. kept.Skip(room).Select(x => x.Memory)];
    }

    /// <summary>
    /// A keepsake swap (§7.4): keep a memory; with three kept already, only with a valid <paramref name="letGo"/>, which becomes an ordinary
    /// memory at strength 1. Returns whether anything changed.
    /// </summary>
    public static bool Swap(IReadOnlyList<LoungeMemoryEntity> voiceMemories, LoungeMemoryEntity keep, LoungeMemoryEntity? letGo, DateTime now)
    {
        if (keep.Keepsake)
        {
            return false;
        }
        var kept = voiceMemories.Where(m => m.Keepsake).ToList();
        if (kept.Count >= Keepsakes)
        {
            if (letGo is null || !letGo.Keepsake || !kept.Contains(letGo))
            {
                return false;
            }
            letGo.Keepsake = false;
            letGo.Strength0 = 1;
            letGo.RefreshedAt = now;
        }
        keep.Keepsake = true;
        keep.Strength0 = 1;
        keep.RefreshedAt = now;
        return true;
    }

    /// <summary>The short id a voice sees (§7.1): <c>m41</c>, or <c>k2</c> for a keepsake.</summary>
    public static string Ref(LoungeMemoryEntity m) => $"{(m.Keepsake ? "k" : "m")}{m.Id}";

    /// <summary>A memory by its short id, whichever letter the voice used.</summary>
    public static LoungeMemoryEntity? Find(IEnumerable<LoungeMemoryEntity> memories, string? reference)
    {
        var text = reference?.Trim().ToLowerInvariant() ?? "";
        if (text.Length < 2 || text[0] is not ('m' or 'k') || !long.TryParse(text.AsSpan(1), out var id))
        {
            return null;
        }
        return memories.FirstOrDefault(m => m.Id == id);
    }
}

/// <summary>
/// The convictions' positions (docs/lounge-plan.md §6.2), pure: a night moves one by at most the day's drift, within −1 to +1, and the owner
/// never below 0.5 on its own side (D9). The other voice may cross 0.
/// </summary>
public static class LoungeOpinions
{
    public const double OwnerFloor = 0.5;

    /// <summary>The position after a move of <paramref name="shift"/>.</summary>
    public static double Move(double before, double shift, double maxDrift, bool owner, double ownerStart)
    {
        var bounded = Math.Clamp(shift, -maxDrift, maxDrift);
        var after = Math.Clamp(before + bounded, -1, 1);
        if (owner)
        {
            after = ownerStart >= 0 ? Math.Max(after, OwnerFloor) : Math.Min(after, -OwnerFloor);
        }
        return Math.Round(after, 4);
    }

    /// <summary>The position in words (§6.1), for the prompt: certain, fairly sure, leaning or torn, and towards which pole.</summary>
    public static string Words(Conviction c, double position)
    {
        var strength = Math.Abs(position) switch
        {
            >= 0.8 => "certain",
            >= 0.5 => "fairly sure",
            >= 0.2 => "leaning",
            _ => "torn",
        };
        var pole = position >= 0 ? c.Plus : c.Minus;
        return strength == "torn" ? FormattableString.Invariant($"torn ({position:+0.00;-0.00})") : FormattableString.Invariant($"{strength} ({position:+0.00;-0.00}): {pole}");
    }

    /// <summary>The positions now: the runtime's, or the convictions' start positions where there are none.</summary>
    public static Dictionary<string, Dictionary<string, double>> Current(string? json, IReadOnlyList<Conviction> convictions)
    {
        Dictionary<string, Dictionary<string, double>>? stored = null;
        try
        {
            stored = string.IsNullOrWhiteSpace(json) ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(json);
        }
        catch (System.Text.Json.JsonException)
        {
        }
        var result = new Dictionary<string, Dictionary<string, double>>();
        foreach (var c in convictions)
        {
            var positions = new Dictionary<string, double>();
            foreach (var voice in LoungeVoices.Both)
            {
                positions[voice] = stored is not null && stored.TryGetValue(c.Key, out var v) && v.TryGetValue(voice, out var p) ? Math.Clamp(p, -1, 1) : c.StartOf(voice);
            }
            result[c.Key] = positions;
        }
        return result;
    }

    public static string Json(Dictionary<string, Dictionary<string, double>> positions) => System.Text.Json.JsonSerializer.Serialize(positions);
}
