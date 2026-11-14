using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Locations;

/// <summary>
/// <i>Admin → Location fields</i> (docs/locations-knowledge-plan.md §4.2–§4.4, §10.5): the definitions, their order,
/// archiving, restoring and deleting (archived fields only, audited), the type changes that keep values, the choices'
/// rules, and the typical fields. One set of fields for every location; nothing is required.
/// </summary>
public sealed partial class LocationFieldService(PnmtdDbContext db, LocationTree tree, IHttpContextAccessor accessor)
{
    public const int MaxChoices = 50;
    public const int MaxChoiceLength = 64;
    public const int MaxUnitLength = 16;
    public const int MaxDecimals = 6;

    /// <summary><i>Add typical fields…</i> (§4.4): each created as a normal field, changed as any other afterwards.</summary>
    public static readonly IReadOnlyList<LocationFieldRequest> Typical =
    [
        new() { Label = "Address", Type = LocationFieldType.Markdown, Inherited = true },
        new() { Label = "Access", Type = LocationFieldType.Markdown, Sensitive = true, Overview = true, Group = "Access" },
        new() { Label = "Opening hours", Type = LocationFieldType.Text, Inherited = true, Overview = true, Group = "Access" },
        new() { Label = "On-site contact", Type = LocationFieldType.User, Inherited = true, Overview = true, Group = "Contacts" },
        new() { Label = "Contacts", Type = LocationFieldType.Markdown, Sensitive = true, Inherited = true, Group = "Contacts" },
        new() { Label = "Network", Type = LocationFieldType.Markdown, Group = "Network" },
        new() { Label = "Uplink and circuit ID", Type = LocationFieldType.Text, Inherited = true, Overview = true, Group = "Network" },
        new() { Label = "Main firewall", Type = LocationFieldType.Host, Group = "Network" },
        new() { Label = "Emergency procedure", Type = LocationFieldType.Markdown, Inherited = true },
        new() { Label = "Floor plan", Type = LocationFieldType.Markdown, Help = "An image of the floor plan: paste it." },
    ];

    /// <summary>Every field, the typical ones, and with <paramref name="withUsers"/> the users a User field can name.</summary>
    public async Task<LocationFieldList> ListAsync(bool withUsers, CancellationToken ct)
    {
        var fields = await db.LocationFields.AsNoTracking().OrderBy(f => f.Position).ThenBy(f => f.Label).ToListAsync(ct);
        var filled = await FilledAsync(null, ct);
        var keys = fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var users = withUsers
            ? (await db.Users.AsNoTracking().Where(u => u.IsActive).Select(u => new { u.Id, u.DisplayName, u.Username }).ToListAsync(ct))
                .Select(u => new Ref(u.Id, string.IsNullOrEmpty(u.DisplayName) ? u.Username : u.DisplayName))
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : null;
        return new LocationFieldList(
            fields.Select(f => Item(f, filled.GetValueOrDefault(f.Id))).ToList(),
            Typical.Select(t => new LocationFieldTemplate(Slug(t.Label!), t.Label!, t.Type, t.Overview, t.Inherited, t.Sensitive, t.Help,
                keys.Contains(Slug(t.Label!)))).ToList(),
            users);
    }

    public async Task<LocationFieldItem> GetAsync(Guid id, CancellationToken ct)
    {
        var field = await db.LocationFields.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw ApiException.NotFound("Field");
        return Item(field, (await FilledAsync(id, ct)).GetValueOrDefault(id));
    }

    /// <summary>A new field at the end of the order, its key a slug of the label, unique (<c>-2</c>, <c>-3</c>, … if taken).</summary>
    public async Task<LocationFieldItem> CreateAsync(LocationFieldRequest request, CancellationToken ct)
    {
        var (label, help, group, fieldOptions) = Validate(request);
        var slug = Slug(label);
        var keys = await db.LocationFields.Select(f => f.Key).Where(k => k.StartsWith(slug)).ToListAsync(ct);
        var key = slug;
        for (var n = 2; keys.Contains(key); n++)
        {
            var suffix = $"-{n}";
            key = slug[..Math.Min(slug.Length, 64 - suffix.Length)].TrimEnd('-') + suffix;
        }
        var position = await db.LocationFields.Select(f => (int?)f.Position).MaxAsync(ct) ?? -1;
        var field = new LocationFieldEntity
        {
            Id = Guid.NewGuid(), Key = key, Label = label, Type = request.Type, Help = help, Group = group, Position = position + 1,
            Overview = request.Overview, Inherited = request.Inherited, Sensitive = request.Sensitive,
            Options = LocationValues.WriteOptions(fieldOptions), CreatedAt = Clock.UtcNow,
        };
        db.LocationFields.Add(field);
        await db.SaveChangesAsync(ct);
        return Item(field, 0);
    }

    /// <summary>
    /// The definition; the key never changes. A type change keeps the values only along Text → Markdown, Choice → Choices
    /// and Choice → Text, otherwise only while no location has a value (409 <c>type_change</c>, §4.2). A choice still
    /// used can't go (409 <c>choice_in_use</c>, naming the locations); a renamed one is renamed in every value (§4.3).
    /// </summary>
    public async Task<LocationFieldItem> UpdateAsync(Guid id, LocationFieldRequest request, CancellationToken ct)
    {
        var field = await db.LocationFields.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw ApiException.NotFound("Field");
        var (label, help, group, fieldOptions) = Validate(request);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var stored = await db.LocationValues.Where(v => v.FieldId == id && v.Value != null).ToListAsync(ct);
        var now = Clock.UtcNow;

        if (field.Type != request.Type && stored.Count > 0 && !SafeTypeChange(field.Type, request.Type))
        {
            throw ApiException.Conflict(LocationErrors.TypeChange,
                $"{stored.Count} {(stored.Count == 1 ? "location has" : "locations have")} a value: the type can only change from Text to Markdown, from Choice to Choices, or from Choice to Text.");
        }
        var wasChoice = field.Type is LocationFieldType.Choice or LocationFieldType.Choices;
        var isChoice = request.Type is LocationFieldType.Choice or LocationFieldType.Choices;
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wasChoice && isChoice && stored.Count > 0)
        {
            var before = LocationValues.ReadOptions(field.Options).Choices ?? [];
            var after = fieldOptions!.Choices!;
            var v = new ApiValidator();
            foreach (var rename in request.Renames ?? [])
            {
                var from = rename.From?.Trim();
                var to = after.FirstOrDefault(c => c == rename.To?.Trim());
                if (from is null || !before.Contains(from) || after.Contains(from) || to is null || !renames.TryAdd(from, to))
                {
                    v.Add("renames", $"{rename.From} → {rename.To}: rename a choice there was to one there is.");
                }
            }
            v.ThrowIfInvalid();
            var removed = before.Except(after).Where(c => !renames.ContainsKey(c)).ToHashSet(StringComparer.Ordinal);
            var users = stored.Where(s => Picked(s.Value).Any(removed.Contains)).Select(s => s.LocationId).ToList();
            if (users.Count > 0)
            {
                var snapshot = await tree.GetAsync(ct);
                var paths = users.Select(snapshot.Path).Order(StringComparer.OrdinalIgnoreCase).ToList();
                throw new ApiException(StatusCodes.Status409Conflict, LocationErrors.ChoiceInUse,
                    $"{string.Join(", ", removed.Where(c => stored.Any(s => Picked(s.Value).Contains(c))))} still in use: {string.Join(", ", paths.Take(20))}{(paths.Count > 20 ? ", …" : "")}.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["locations"] = users.Select(u => new LocationRef(u, snapshot.Find(u)?.Name ?? "?", snapshot.Path(u))).ToList(),
                    });
            }
        }
        // The values follow the definition: renamed choices, and a Choice turned into Choices.
        foreach (var value in stored)
        {
            var json = value.Value;
            if (renames.Count > 0)
            {
                json = Renamed(json, renames, field.Type);
            }
            if (field.Type == LocationFieldType.Choice && request.Type == LocationFieldType.Choices && LocationValues.ReadString(json) is { } single)
            {
                json = JsonSerializer.Serialize(new[] { single }, LocationValues.Json);
            }
            if (!LocationValues.SameJson(json, value.Value))
            {
                value.Value = json;
                value.Revision++;
                value.UpdatedAt = now;
            }
        }
        field.Label = label;
        field.Type = request.Type;
        field.Help = help;
        field.Group = group;
        field.Overview = request.Overview;
        field.Inherited = request.Inherited;
        field.Sensitive = request.Sensitive;
        field.Options = LocationValues.WriteOptions(fieldOptions);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Item(field, stored.Count);
    }

    /// <summary><c>PUT /location-fields/order</c>: the fields named first, in that order; the others after them, in theirs.</summary>
    public async Task<LocationFieldList> OrderAsync(LocationFieldOrderRequest request, CancellationToken ct)
    {
        var fields = await db.LocationFields.OrderBy(f => f.Position).ThenBy(f => f.Label).ToListAsync(ct);
        var ids = request.Ids.Distinct().ToList();
        new ApiValidator()
            .Check(ids.Count >= 1 && ids.All(id => fields.Any(f => f.Id == id)), "ids", "The fields, each once.")
            .ThrowIfInvalid();
        var ordered = ids.Select(id => fields.First(f => f.Id == id)).Concat(fields.Where(f => !ids.Contains(f.Id))).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = i;
        }
        await db.SaveChangesAsync(ct);
        return await ListAsync(true, ct);
    }

    /// <summary>Hidden everywhere, its values kept (§4.3).</summary>
    public async Task<LocationFieldItem> ArchiveAsync(Guid id, CancellationToken ct)
    {
        var field = await db.LocationFields.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw ApiException.NotFound("Field");
        field.ArchivedAt ??= Clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Back with its values (§4.3).</summary>
    public async Task<LocationFieldItem> RestoreAsync(Guid id, CancellationToken ct)
    {
        var field = await db.LocationFields.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw ApiException.NotFound("Field");
        field.ArchivedAt = null;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// An archived field only (409 <c>not_archived</c>): it goes with its values and their history rows, and the auth log
    /// keeps the label and how many locations had a value (<c>LocationFieldDeleted</c>, §4.3, §8.7).
    /// </summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var field = await db.LocationFields.SingleOrDefaultAsync(f => f.Id == id, ct) ?? throw ApiException.NotFound("Field");
        if (field.ArchivedAt is null)
        {
            throw ApiException.Conflict(LocationErrors.NotArchived, "Archive the field first: only an archived field can be deleted.");
        }
        var filled = (await FilledAsync(id, ct)).GetValueOrDefault(id);
        UdpViews.Audit(db, accessor, AuthEvents.LocationFieldDeleted,
            Cuts.Cut($"{field.Label} ({field.Key}, {field.Type}): {filled} {(filled == 1 ? "value" : "values")} deleted", 1000));
        db.LocationFields.Remove(field);
        await db.SaveChangesAsync(ct);
    }

    // --- Rules ---------------------------------------------------------------------------------------------------------

    /// <summary>The type changes that keep every value as it is, or turn a choice into a list of one (§4.2).</summary>
    public static bool SafeTypeChange(LocationFieldType from, LocationFieldType to) => (from, to) is
        (LocationFieldType.Text, LocationFieldType.Markdown) or
        (LocationFieldType.Choice, LocationFieldType.Choices) or
        (LocationFieldType.Choice, LocationFieldType.Text);

    /// <summary>
    /// The key made from the first label (§4.2): lower case, umlauts spelled out, other accents dropped, anything else a
    /// dash; at most 64 characters. <i>On-site contact</i> gives <c>on-site-contact</c>.
    /// </summary>
    public static string Slug(string label)
    {
        var text = label.Trim().ToLowerInvariant().Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        var plain = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(c);
            }
        }
        var slug = NotSlug().Replace(plain.ToString(), "-").Trim('-');
        slug = slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
        return slug.Length == 0 ? "field" : slug;
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant, 250)]
    private static partial Regex NotSlug();

    private static (string Label, string? Help, string? Group, LocationFieldOptions? Options) Validate(LocationFieldRequest request)
    {
        var label = Text.Trimmed(request.Label) ?? "";
        var help = Text.Trimmed(request.Help);
        var group = Text.Trimmed(request.Group);
        var v = new ApiValidator()
            .Check(label.Length is >= 1 and <= 64 && !label.Any(char.IsControl), "label", "1 to 64 characters.")
            .Check(Enum.IsDefined(request.Type), "type", "One of the field types.")
            .Check(help is null || help.Length <= 256, "help", "At most 256 characters.")
            .Check(group is null || (group.Length <= 64 && !group.Any(char.IsControl)), "group", "At most 64 characters.");
        LocationFieldOptions? fieldOptions = null;
        switch (request.Type)
        {
            case LocationFieldType.Choice:
            case LocationFieldType.Choices:
                {
                    var choices = (request.Options?.Choices ?? []).Select(c => c?.Trim() ?? "").ToList();
                    v.Check(choices.Count is >= 1 and <= MaxChoices, "options.choices", $"1 to {MaxChoices} choices.")
                        .Check(choices.All(c => c.Length is >= 1 and <= MaxChoiceLength && !c.Any(char.IsControl)), "options.choices",
                            $"Each choice 1 to {MaxChoiceLength} characters.")
                        .Check(choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() == choices.Count, "options.choices", "Each choice once.");
                    fieldOptions = new LocationFieldOptions { Choices = choices };
                    break;
                }
            case LocationFieldType.Number:
                {
                    var o = request.Options ?? new LocationFieldOptions();
                    var unit = Text.Trimmed(o.Unit);
                    v.Check(unit is null || unit.Length <= MaxUnitLength, "options.unit", $"At most {MaxUnitLength} characters.")
                        .Check(o.Min is null || o.Max is null || o.Min <= o.Max, "options.max", "At least the minimum.")
                        .Check(o.Decimals is null or >= 0 and <= MaxDecimals, "options.decimals", $"0 to {MaxDecimals}.");
                    fieldOptions = new LocationFieldOptions { Unit = unit, Min = o.Min, Max = o.Max, Decimals = o.Decimals };
                    break;
                }
        }
        v.ThrowIfInvalid();
        return (label, help, group, fieldOptions);
    }

    /// <summary>The choices a Choice or Choices value picks.</summary>
    private static IReadOnlyList<string> Picked(string? json)
    {
        if (json is null)
        {
            return [];
        }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.ValueKind switch
        {
            JsonValueKind.String => [root.GetString()!],
            JsonValueKind.Array => root.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList(),
            _ => [],
        };
    }

    private static string? Renamed(string? json, IReadOnlyDictionary<string, string> renames, LocationFieldType type)
    {
        var picked = Picked(json).Select(c => renames.GetValueOrDefault(c, c)).Distinct(StringComparer.Ordinal).ToList();
        return type == LocationFieldType.Choice
            ? picked.Count == 0 ? json : LocationValues.StringValue(picked[0])
            : JsonSerializer.Serialize(picked, LocationValues.Json);
    }

    /// <summary>Per field, how many locations have a value of their own.</summary>
    private async Task<Dictionary<Guid, int>> FilledAsync(Guid? fieldId, CancellationToken ct) =>
        await db.LocationValues.AsNoTracking()
            .Where(v => v.Value != null && (fieldId == null || v.FieldId == fieldId))
            .GroupBy(v => v.FieldId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    private static LocationFieldItem Item(LocationFieldEntity f, int filled) =>
        new(f.Id, f.Key, f.Label, f.Type, f.Help, f.Group, f.Position, f.Overview, f.Inherited, f.Sensitive,
            f.Options is null ? null : LocationValues.ReadOptions(f.Options), f.ArchivedAt, f.CreatedAt, filled);
}
