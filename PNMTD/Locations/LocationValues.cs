using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Files;
using PNMTD.Knowledge;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Locations;

/// <summary>
/// A field's value at a location as it is shown (docs/locations-knowledge-plan.md §3.3): its own, or for an inherited
/// field without one, the nearest ancestor's (<see cref="InheritedFrom"/>). <see cref="Value"/> is JSON by the field's type
/// (Contracts/Locations.cs), null when neither has one. <see cref="Own"/> is the location's own row, whose revision a save
/// sends back; it can exist with a null value once cleared.
/// </summary>
public sealed record EffectiveValue(LocationFieldEntity Field, string? Value, Guid? InheritedFrom, LocationValueEntity? Own)
{
    public bool HasValue => Value is not null;

    public int Revision => Own?.Revision ?? 0;
}

/// <summary>
/// The values of location fields (docs/locations-knowledge-plan.md §4.1, §3.3): per type, validated and normalised to the
/// JSON of Contracts/Locations.cs; stored with a revision per location and field (409 <c>edit_conflict</c>, §5.7) and a
/// history row (§3.5); inherited from the nearest ancestor where the field says so. Markdown is limited by
/// KnowledgeBase:MaxFieldBytes and records the files it references (§5.5).
/// <para>
/// <see cref="Resolve"/> and <see cref="ResolveAsync(Guid, CancellationToken)"/> are the one place inheritance is
/// worked out, for the location's page and for any other reader (the knowledge base's <c>MachineAccess</c>, §7.4, which
/// keeps only fields not marked sensitive).
/// </para>
/// </summary>
public sealed partial class LocationValues(PnmtdDbContext db, LocationTree tree, IOptionsMonitor<KnowledgeBaseOptions> options, IHttpContextAccessor accessor)
{
    public const int MaxTextLength = 512;
    public const int MaxUrlLength = 2048;
    public const int MaxLinkLabelLength = 128;
    public const int MaxEmailLength = 254;
    public const int MaxPhoneLength = 32;

    /// <summary>A deleted user's value is shown so (§4.1).</summary>
    public const string FormerUser = "former user";

    /// <summary>Values as stored: no escaping beyond JSON's own, so umlauts stay readable in the database.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A Link value (§4.1).</summary>
    public sealed record LinkValue(string Url, string? Label);

    // --- Inheritance (§3.3) ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The values of <paramref name="locationId"/> for <paramref name="fields"/>, in their order: its own where it has
    /// one; for an inherited field without one, the nearest ancestor's that has one; otherwise none. Pure: the caller
    /// reads the tree, the fields and the values of the location and its ancestors.
    /// </summary>
    public static IReadOnlyList<EffectiveValue> Resolve(LocationTreeSnapshot tree, IEnumerable<LocationFieldEntity> fields,
        IReadOnlyDictionary<(Guid LocationId, Guid FieldId), LocationValueEntity> values, Guid locationId)
    {
        var ancestors = tree.Ancestors(locationId);
        return fields.Select(field =>
        {
            var own = values.GetValueOrDefault((locationId, field.Id));
            if (own?.Value is not null || !field.Inherited)
            {
                return new EffectiveValue(field, own?.Value, null, own);
            }
            foreach (var ancestor in ancestors)
            {
                if (values.GetValueOrDefault((ancestor.Id, field.Id)) is { Value: { } inherited })
                {
                    return new EffectiveValue(field, inherited, ancestor.Id, own);
                }
            }
            return new EffectiveValue(field, null, null, own);
        }).ToList();
    }

    /// <summary>A location's values for every field not archived, inheritance included; empty for an unknown location.</summary>
    public async Task<IReadOnlyList<EffectiveValue>> ResolveAsync(Guid locationId, CancellationToken ct = default) =>
        (await ResolveAsync([locationId], ct)).GetValueOrDefault(locationId) ?? [];

    /// <summary>Several locations' values at once (one query for the values of them and their ancestors).</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<EffectiveValue>>> ResolveAsync(IReadOnlyCollection<Guid> locationIds,
        CancellationToken ct = default)
    {
        var snapshot = await tree.GetAsync(ct);
        var known = locationIds.Where(snapshot.Contains).Distinct().ToList();
        if (known.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<EffectiveValue>>();
        }
        var fields = await FieldsAsync(ct);
        var ids = known.Concat(known.SelectMany(id => snapshot.Ancestors(id).Select(a => a.Id))).Distinct().ToList();
        var values = await db.LocationValues.AsNoTracking()
            .Where(v => ids.Contains(v.LocationId) && v.Field!.ArchivedAt == null)
            .ToDictionaryAsync(v => (v.LocationId, v.FieldId), ct);
        return known.ToDictionary(id => id, id => Resolve(snapshot, fields, values, id));
    }

    /// <summary>The fields not archived, in their order (§4.2).</summary>
    public Task<List<LocationFieldEntity>> FieldsAsync(CancellationToken ct = default) =>
        db.LocationFields.AsNoTracking().Where(f => f.ArchivedAt == null).OrderBy(f => f.Position).ThenBy(f => f.Label).ToListAsync(ct);

    /// <summary>
    /// Per location, which fields have a value with inheritance, without reading the values themselves (the list's
    /// <i>6 of 9</i>, §3.6): own ones, and those inherited.
    /// </summary>
    public static (int Filled, IReadOnlyList<LocationFieldEntity> Inherited) Filled(LocationTreeSnapshot tree, IReadOnlyList<LocationFieldEntity> fields,
        IReadOnlySet<(Guid LocationId, Guid FieldId)> filled, Guid locationId)
    {
        var ancestors = tree.Ancestors(locationId);
        var count = 0;
        var inherited = new List<LocationFieldEntity>();
        foreach (var field in fields)
        {
            if (filled.Contains((locationId, field.Id)))
            {
                count++;
            }
            else if (field.Inherited && ancestors.Any(a => filled.Contains((a.Id, field.Id))))
            {
                count++;
                inherited.Add(field);
            }
        }
        return (count, inherited);
    }

    // --- The API's items ---------------------------------------------------------------------------------------------

    /// <summary>The values as the API shows them, with where an inherited one comes from and whom a User or Host value names.</summary>
    public async Task<IReadOnlyList<LocationValueItem>> ItemsAsync(IReadOnlyList<EffectiveValue> values, CancellationToken ct = default)
    {
        var snapshot = await tree.GetAsync(ct);
        var userIds = values.Where(v => v.Field.Type == LocationFieldType.User).Select(v => ReadId(v.Value)).OfType<Guid>().Distinct().ToList();
        var hostIds = values.Where(v => v.Field.Type == LocationFieldType.Host).Select(v => ReadId(v.Value)).OfType<Guid>().Distinct().ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var hosts = hostIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Hosts.AsNoTracking().Where(h => hostIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
        return values.Select(v =>
        {
            Ref? target = null;
            if (ReadId(v.Value) is { } id)
            {
                target = v.Field.Type switch
                {
                    LocationFieldType.User => new Ref(id, users.GetValueOrDefault(id) is { Length: > 0 } name ? name : FormerUser),
                    LocationFieldType.Host => hosts.TryGetValue(id, out var host) ? new Ref(id, host) : null,
                    _ => null,
                };
            }
            var from = v.InheritedFrom is { } a && snapshot.Find(a) is { } node ? new Ref(node.Id, node.Name) : null;
            return new LocationValueItem(v.Field.Id, Element(v.Value), from, v.Revision, v.Own?.UpdatedAt, v.Own?.UpdatedByName, target);
        }).ToList();
    }

    // --- Saving (§4.1, §5.7) -----------------------------------------------------------------------------------------

    /// <summary>
    /// Sets a location's own value of a field, or clears it with null (an inherited field then shows its ancestor's again).
    /// A save that started from an older revision is refused with 409 <c>edit_conflict</c>, unless it sets what is
    /// stored already. Every change raises the revision, writes a history row, and for Markdown records its files.
    /// </summary>
    public async Task<LocationValueItem> SetAsync(Guid locationId, Guid fieldId, LocationValueRequest request, CancellationToken ct)
    {
        var location = await db.Locations.SingleOrDefaultAsync(l => l.Id == locationId, ct) ?? throw ApiException.NotFound("Location");
        var field = await db.LocationFields.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fieldId && f.ArchivedAt == null, ct)
            ?? throw ApiException.NotFound("Field");
        var json = await NormalizeAsync(field, request.Value, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // One save of a location's field at a time, so two saves from the same revision can't both pass the check.
        var key = BitConverter.ToInt64(locationId.ToByteArray(), 0) ^ BitConverter.ToInt64(fieldId.ToByteArray(), 8);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [key], ct);
        var own = await db.LocationValues.SingleOrDefaultAsync(v => v.LocationId == locationId && v.FieldId == fieldId, ct);
        if (!SameJson(own?.Value, json))
        {
            if (request.Revision != (own?.Revision ?? 0))
            {
                throw ApiException.EditConflict(own!.Revision, own.UpdatedByName, own.UpdatedAt,
                    $"{own.UpdatedByName ?? "Someone"} saved {field.Label} in the meantime.");
            }
            if (!request.AcknowledgeCredentials && field.Type is LocationFieldType.Markdown or LocationFieldType.Text)
            {
                CheckCredentials(ReadString(json), ReadString(own?.Value));
            }
            var by = LocationActor.Of(accessor);
            var now = Clock.UtcNow;
            var old = own?.Value;
            if (own is null)
            {
                own = new LocationValueEntity { LocationId = locationId, FieldId = fieldId };
                db.LocationValues.Add(own);
            }
            own.Value = json;
            own.Revision++;
            own.UpdatedAt = now;
            own.UpdatedBy = by.Id;
            own.UpdatedByName = by.Name;
            location.UpdatedAt = now;
            location.UpdatedBy = by.Id;
            location.UpdatedByName = by.Name;
            db.LocationHistory.Add(new LocationHistoryEntity
            {
                Id = Guid.NewGuid(), LocationId = locationId, At = now, ByUserId = by.Id, ByName = by.Name,
                Kind = LocationHistoryKind.ValueChanged, FieldId = fieldId, OldValue = old, NewValue = json,
            });
            if (field.Type == LocationFieldType.Markdown && json is not null)
            {
                await FileUses.RecordAsync(db, MarkdownText.Read(ReadString(json) ?? "").FileIds, null, locationId, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        var resolved = (await ResolveAsync(locationId, ct)).Single(v => v.Field.Id == fieldId);
        return (await ItemsAsync([resolved], ct))[0];
    }

    /// <summary>
    /// The credentials warning (§5.7): lines that look like a password, a key or a token, and weren't in the value before,
    /// answer 422 <c>looks_like_credential</c> with their numbers and kinds, never the text. It doesn't block: the save
    /// that acknowledges it goes through.
    /// </summary>
    private static void CheckCredentials(string? text, string? before)
    {
        var findings = SecretScanner.Scan(text);
        if (findings.Count == 0)
        {
            return;
        }
        var lines = text!.Replace("\r\n", "\n").Split('\n');
        var oldLines = (before ?? "").Replace("\r\n", "\n").Split('\n');
        var known = SecretScanner.Scan(before).Select(f => oldLines[f.Line - 1].Trim()).ToHashSet(StringComparer.Ordinal);
        var fresh = findings.Where(f => !known.Contains(lines[f.Line - 1].Trim())).ToList();
        if (fresh.Count > 0)
        {
            throw KbPageService.LooksLikeCredential(fresh, "Some lines look like credentials. PNMT is not a password manager.");
        }
    }

    // --- Validating and normalising (§4.1) ---------------------------------------------------------------------------

    /// <summary>The value as stored (JSON, or null to clear), or 400 <c>validation_failed</c> on <c>value</c>.</summary>
    public async Task<string?> NormalizeAsync(LocationFieldEntity field, JsonElement? value, CancellationToken ct = default)
    {
        var errors = new ApiValidator();
        var json = Normalize(field, value, options.CurrentValue.MaxFieldBytes, errors);
        errors.ThrowIfInvalid();
        if (json is not null && ReadId(json) is { } id)
        {
            var exists = field.Type switch
            {
                LocationFieldType.User => await db.Users.AnyAsync(u => u.Id == id, ct),
                LocationFieldType.Host => await db.Hosts.AnyAsync(h => h.Id == id, ct),
                _ => true,
            };
            if (!exists)
            {
                throw ApiException.Invalid("value", field.Type == LocationFieldType.User ? "No such user." : "No such host.");
            }
        }
        return json;
    }

    /// <summary>
    /// The type's rules without the database: the value as stored, or null with the reason in <paramref name="errors"/> (on
    /// <c>value</c>). Null, an empty string and an empty list clear the value.
    /// </summary>
    public static string? Normalize(LocationFieldEntity field, JsonElement? value, int maxFieldBytes, ApiValidator errors)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        var fieldOptions = ReadOptions(field.Options);
        switch (field.Type)
        {
            case LocationFieldType.Markdown:
                {
                    if (!IsString(v, errors, "Markdown text."))
                    {
                        return null;
                    }
                    // Stored exactly as written (§5.1); PostgreSQL's JSON can't hold U+0000.
                    var markdown = v.GetString()!.Replace("\0", "");
                    if (string.IsNullOrWhiteSpace(markdown))
                    {
                        return null;
                    }
                    if (Encoding.UTF8.GetByteCount(markdown) > maxFieldBytes)
                    {
                        errors.Add("value", $"At most {maxFieldBytes / 1024} KB.");
                        return null;
                    }
                    return StringValue(markdown);
                }
            case LocationFieldType.Text:
                {
                    if (!IsString(v, errors, "Text."))
                    {
                        return null;
                    }
                    var text = v.GetString()!.Replace('\t', ' ').Trim();
                    if (text.Length == 0)
                    {
                        return null;
                    }
                    if (text.Any(char.IsControl))
                    {
                        errors.Add("value", "One line, without line breaks.");
                        return null;
                    }
                    if (text.Length > MaxTextLength)
                    {
                        errors.Add("value", $"At most {MaxTextLength} characters.");
                        return null;
                    }
                    return StringValue(text);
                }
            case LocationFieldType.Number:
                {
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var number))
                    {
                        errors.Add("value", "A number.");
                        return null;
                    }
                    number = TrimZeros(number);
                    if (fieldOptions.Min is { } min && number < min)
                    {
                        errors.Add("value", $"At least {Number(min)}.");
                    }
                    if (fieldOptions.Max is { } max && number > max)
                    {
                        errors.Add("value", $"At most {Number(max)}.");
                    }
                    var decimals = fieldOptions.Decimals ?? 6;
                    if (Scale(number) > decimals)
                    {
                        errors.Add("value", decimals == 0 ? "A whole number." : $"At most {decimals} decimals.");
                    }
                    return errors.HasErrors ? null : Number(number);
                }
            case LocationFieldType.YesNo:
                if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add("value", "true or false.");
                    return null;
                }
                return v.GetBoolean() ? "true" : "false";
            case LocationFieldType.Date:
                {
                    if (!IsString(v, errors, "A date, yyyy-MM-dd."))
                    {
                        return null;
                    }
                    var text = v.GetString()!.Trim();
                    if (text.Length == 0)
                    {
                        return null;
                    }
                    if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    {
                        errors.Add("value", "A date, yyyy-MM-dd.");
                        return null;
                    }
                    return StringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
            case LocationFieldType.Choice:
                {
                    if (!IsString(v, errors, "One of the choices."))
                    {
                        return null;
                    }
                    var text = v.GetString()!.Trim();
                    if (text.Length == 0)
                    {
                        return null;
                    }
                    if (Choice(fieldOptions, text) is not { } choice)
                    {
                        errors.Add("value", $"One of: {string.Join(", ", fieldOptions.Choices ?? [])}.");
                        return null;
                    }
                    return StringValue(choice);
                }
            case LocationFieldType.Choices:
                {
                    if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                    {
                        errors.Add("value", "A list of the choices.");
                        return null;
                    }
                    var picked = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in v.EnumerateArray().Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0))
                    {
                        if (Choice(fieldOptions, item) is { } choice)
                        {
                            picked.Add(choice);
                        }
                        else
                        {
                            errors.Add("value", $"Not a choice: {item}.");
                        }
                    }
                    if (errors.HasErrors || picked.Count == 0)
                    {
                        return null;
                    }
                    return JsonSerializer.Serialize((fieldOptions.Choices ?? []).Where(picked.Contains).ToList(), Json);
                }
            case LocationFieldType.Link:
                {
                    string? url;
                    string? label = null;
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        url = v.GetString();
                    }
                    else if (v.ValueKind == JsonValueKind.Object)
                    {
                        url = v.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                        label = v.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
                    }
                    else
                    {
                        errors.Add("value", "A link: { url, label }.");
                        return null;
                    }
                    url = url?.Trim();
                    label = Text(label);
                    if (string.IsNullOrEmpty(url))
                    {
                        if (label is not null)
                        {
                            errors.Add("value", "A link needs its URL.");
                        }
                        return null;
                    }
                    if (url.Length > MaxUrlLength || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
                    {
                        errors.Add("value", $"An http(s) address, at most {MaxUrlLength} characters.");
                        return null;
                    }
                    if (label is { Length: > MaxLinkLabelLength })
                    {
                        errors.Add("value", $"A label of at most {MaxLinkLabelLength} characters.");
                        return null;
                    }
                    return JsonSerializer.Serialize(new LinkValue(url, label), Json);
                }
            case LocationFieldType.Email:
                {
                    if (!IsString(v, errors, "An e-mail address."))
                    {
                        return null;
                    }
                    var text = v.GetString()!.Trim();
                    if (text.Length == 0)
                    {
                        return null;
                    }
                    if (text.Length > MaxEmailLength || !EmailPattern().IsMatch(text))
                    {
                        errors.Add("value", "An e-mail address, x@y.z.");
                        return null;
                    }
                    return StringValue(text);
                }
            case LocationFieldType.Phone:
                {
                    if (!IsString(v, errors, "A phone number."))
                    {
                        return null;
                    }
                    var text = v.GetString()!.Trim();
                    if (text.Length == 0)
                    {
                        return null;
                    }
                    if (text.Length > MaxPhoneLength || !PhonePattern().IsMatch(text) || !text.Any(char.IsAsciiDigit))
                    {
                        errors.Add("value", $"At most {MaxPhoneLength} characters: digits, +, spaces, ( ) - /.");
                        return null;
                    }
                    return StringValue(text);
                }
            case LocationFieldType.User:
            case LocationFieldType.Host:
                {
                    var what = field.Type == LocationFieldType.User ? "A user's id." : "A host's id.";
                    if (!IsString(v, errors, what))
                    {
                        return null;
                    }
                    var text = v.GetString()!.Trim();
                    if (text.Length == 0)
                    {
                        return null;
                    }
                    if (!Guid.TryParse(text, out var id))
                    {
                        errors.Add("value", what);
                        return null;
                    }
                    return StringValue(id.ToString());
                }
            default:
                errors.Add("value", "Unknown type.");
                return null;
        }
    }

    private static bool IsString(JsonElement value, ApiValidator errors, string message)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return true;
        }
        errors.Add("value", message);
        return false;
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The field's choice this text names: exactly, else ignoring case (stored in the choice's spelling).</summary>
    private static string? Choice(LocationFieldOptions options, string text) =>
        options.Choices?.FirstOrDefault(c => c == text) ?? options.Choices?.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase));

    private static decimal TrimZeros(decimal value) => value / 1.000000000000000000000000000000000m;

    private static int Scale(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    private static string Number(decimal value) => TrimZeros(value).ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[0-9+ ()\-/]+$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex PhonePattern();

    // --- JSON helpers --------------------------------------------------------------------------------------------------

    /// <summary>A string as a JSON value.</summary>
    public static string StringValue(string value) => JsonSerializer.Serialize(value, Json);

    /// <summary>A JSON string value's text; null for anything else.</summary>
    public static string? ReadString(string? json)
    {
        if (json is null)
        {
            return null;
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
    }

    /// <summary>A User or Host value's id.</summary>
    public static Guid? ReadId(string? json) => Guid.TryParse(ReadString(json), out var id) ? id : null;

    /// <summary>The same JSON, whatever the spacing and order PostgreSQL gives it.</summary>
    public static bool SameJson(string? a, string? b) =>
        a is null || b is null ? a is null && b is null : JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));

    public static JsonElement? Element(string? json)
    {
        if (json is null)
        {
            return null;
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A field's type options; none for a field without.</summary>
    public static LocationFieldOptions ReadOptions(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new LocationFieldOptions() : JsonSerializer.Deserialize<LocationFieldOptions>(json, Json) ?? new LocationFieldOptions();

    public static string? WriteOptions(LocationFieldOptions? fieldOptions) =>
        fieldOptions is null || fieldOptions == new LocationFieldOptions() ? null : JsonSerializer.Serialize(fieldOptions, Json);
}

/// <summary>Who changes a location (§3.5): the user's id and name at the time, as histories keep them (§8.2).</summary>
public readonly record struct LocationActor(Guid? Id, string Name)
{
    public static LocationActor Of(IHttpContextAccessor accessor)
    {
        var user = accessor.HttpContext?.User;
        var name = user?.FindFirst(SessionClaims.DisplayName)?.Value is { Length: > 0 } display ? display : user?.Identity?.Name;
        return new LocationActor(user?.UserId(), string.IsNullOrEmpty(name) ? "PNMT" : name.Length > 200 ? name[..200] : name);
    }
}
