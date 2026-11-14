using System.Text.Json;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Locations;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Locations;

/// <summary>
/// Each type's rules (docs/locations-knowledge-plan.md §4.1), as the JSON that is stored, and inheritance (§3.3): a
/// sub-location without a value of its own shows its nearest ancestor's, only where the field says so.
/// </summary>
[TestClass]
public sealed class LocationValuesTests
{
    private static LocationFieldEntity Field(LocationFieldType type, LocationFieldOptions? options = null, bool inherited = false) => new()
    {
        Id = Guid.NewGuid(), Key = type.ToString().ToLowerInvariant(), Label = type.ToString(), Type = type, Inherited = inherited,
        Options = LocationValues.WriteOptions(options),
    };

    private static (string? Json, string? Error) Normalize(LocationFieldEntity field, string json, int maxBytes = 262144)
    {
        var errors = new ApiValidator();
        var result = LocationValues.Normalize(field, JsonDocument.Parse(json).RootElement, maxBytes, errors);
        try
        {
            errors.ThrowIfInvalid();
            return (result, null);
        }
        catch (ApiException ex)
        {
            return (null, ex.Errors!["value"][0]);
        }
    }

    private static void Valid(LocationFieldEntity field, string json, string? stored) =>
        Assert.AreEqual(stored, Normalize(field, json).Json, json);

    private static void Invalid(LocationFieldEntity field, string json) =>
        Assert.IsNotNull(Normalize(field, json).Error, $"{field.Type}: {json} should be refused");

    [TestMethod]
    public void MarkdownIsStoredAsWrittenUpToTheLimit()
    {
        var field = Field(LocationFieldType.Markdown);
        Valid(field, "\"  # Cage B\\n\\nRack 4–6.  \\n\"", "\"  # Cage B\\n\\nRack 4–6.  \\n\"");
        Valid(field, "\"   \\n \"", null);
        Valid(field, "null", null);
        Invalid(field, "42");
        Assert.IsNotNull(Normalize(field, JsonSerializer.Serialize(new string('ä', 600)), 1024).Error, "1,200 bytes over 1 KB");
        Assert.IsNull(Normalize(field, JsonSerializer.Serialize(new string('a', 1024)), 1024).Error);
    }

    [TestMethod]
    public void TextIsOneTrimmedLine()
    {
        var field = Field(LocationFieldType.Text);
        Valid(field, "\"  Init7 · 12-3456 \"", "\"Init7 · 12-3456\"");
        Valid(field, "\"\"", null);
        Invalid(field, "\"two\\nlines\"");
        Invalid(field, JsonSerializer.Serialize(new string('x', 513)));
        Invalid(field, "true");
    }

    [TestMethod]
    public void NumbersKeepToTheirRangeAndDecimals()
    {
        var field = Field(LocationFieldType.Number, new LocationFieldOptions { Unit = "kW", Min = 0, Max = 100, Decimals = 1 });
        Valid(field, "12.50", "12.5");
        Valid(field, "0", "0");
        Valid(field, "100", "100");
        Invalid(field, "-1");
        Invalid(field, "100.5");
        Invalid(field, "12.25");
        Invalid(field, "\"12\"");
        var whole = Field(LocationFieldType.Number, new LocationFieldOptions { Decimals = 0 });
        Valid(whole, "42.0", "42");
        Invalid(whole, "4.2");
    }

    [TestMethod]
    public void YesNoAndDates()
    {
        var yesNo = Field(LocationFieldType.YesNo);
        Valid(yesNo, "true", "true");
        Valid(yesNo, "false", "false");
        Invalid(yesNo, "\"yes\"");

        var date = Field(LocationFieldType.Date);
        Valid(date, "\"2026-11-14\"", "\"2026-11-14\"");
        Invalid(date, "\"14.11.2026\"");
        Invalid(date, "\"2026-02-30\"");
    }

    [TestMethod]
    public void ChoicesComeFromTheList()
    {
        var options = new LocationFieldOptions { Choices = ["Colocation", "Office", "Warehouse"] };
        var choice = Field(LocationFieldType.Choice, options);
        Valid(choice, "\"Office\"", "\"Office\"");
        Valid(choice, "\"office\"", "\"Office\"");
        Invalid(choice, "\"Garage\"");

        var choices = Field(LocationFieldType.Choices, options);
        Valid(choices, "[\"Warehouse\", \"colocation\", \"Warehouse\"]", "[\"Colocation\",\"Warehouse\"]");
        Valid(choices, "[]", null);
        Invalid(choices, "[\"Garage\"]");
        Invalid(choices, "\"Office\"");
    }

    [TestMethod]
    public void LinksAreAbsoluteHttpWithAnOptionalLabel()
    {
        var field = Field(LocationFieldType.Link);
        Valid(field, "{\"url\": \" https://vault.example.com/zrh \", \"label\": \" Door codes \"}",
            "{\"url\":\"https://vault.example.com/zrh\",\"label\":\"Door codes\"}");
        Valid(field, "\"http://intranet/zrh\"", "{\"url\":\"http://intranet/zrh\"}");
        Valid(field, "{\"url\": \"\"}", null);
        Invalid(field, "\"javascript:alert(1)\"");
        Invalid(field, "\"/relative/path\"");
        Invalid(field, "\"ftp://files.example.com\"");
        Invalid(field, JsonSerializer.Serialize("https://example.com/" + new string('a', 2048)));
    }

    [TestMethod]
    public void EmailsAndPhones()
    {
        var email = Field(LocationFieldType.Email);
        Valid(email, "\" noc@example.ch \"", "\"noc@example.ch\"");
        Invalid(email, "\"noc@example\"");
        Invalid(email, "\"two words@example.ch\"");

        var phone = Field(LocationFieldType.Phone);
        Valid(phone, "\"+41 (44) 123-45/67\"", "\"+41 (44) 123-45/67\"");
        Invalid(phone, "\"+41 44 123 45 67 ext. 3\"");
        Invalid(phone, "\"+-/\"");
        Invalid(phone, JsonSerializer.Serialize(new string('1', 33)));
    }

    [TestMethod]
    public void UsersAndHostsAreIds()
    {
        var id = Guid.NewGuid();
        Valid(Field(LocationFieldType.User), $"\"{id.ToString().ToUpperInvariant()}\"", $"\"{id}\"");
        Valid(Field(LocationFieldType.Host), $"\"{id}\"", $"\"{id}\"");
        Invalid(Field(LocationFieldType.Host), "\"fw-zrh-01\"");
    }

    [TestMethod]
    public void InheritedValuesComeFromTheNearestAncestorWithOne()
    {
        Guid site = Guid.NewGuid(), building = Guid.NewGuid(), room = Guid.NewGuid();
        var tree = LocationTreeSnapshot.From([new LocationNode(site, null, "Site"), new LocationNode(building, site, "Building"),
            new LocationNode(room, building, "Room")]);
        var address = Field(LocationFieldType.Markdown, inherited: true);
        var notes = Field(LocationFieldType.Markdown);
        var hours = Field(LocationFieldType.Text, inherited: true);
        var values = new Dictionary<(Guid, Guid), LocationValueEntity>
        {
            [(site, address.Id)] = new() { LocationId = site, FieldId = address.Id, Value = "\"Site street 1\"", Revision = 1 },
            [(site, notes.Id)] = new() { LocationId = site, FieldId = notes.Id, Value = "\"Only the site's\"", Revision = 1 },
            [(building, hours.Id)] = new() { LocationId = building, FieldId = hours.Id, Value = "\"8–17\"", Revision = 2 },
            // Cleared: a row without a value inherits again, keeping its revision for the conflict check.
            [(room, hours.Id)] = new() { LocationId = room, FieldId = hours.Id, Value = null, Revision = 3 },
        };

        var resolved = LocationValues.Resolve(tree, [address, notes, hours], values, room);

        Assert.AreEqual("\"Site street 1\"", resolved[0].Value);
        Assert.AreEqual(site, resolved[0].InheritedFrom);
        Assert.AreEqual(0, resolved[0].Revision);
        Assert.IsNull(resolved[1].Value, "not inherited");
        Assert.AreEqual("\"8–17\"", resolved[2].Value);
        Assert.AreEqual(building, resolved[2].InheritedFrom);
        Assert.AreEqual(3, resolved[2].Revision);

        // Overriding: its own value wins.
        values[(room, address.Id)] = new LocationValueEntity { LocationId = room, FieldId = address.Id, Value = "\"Room 2.13\"", Revision = 1 };
        var overridden = LocationValues.Resolve(tree, [address], values, room).Single();
        Assert.AreEqual("\"Room 2.13\"", overridden.Value);
        Assert.IsNull(overridden.InheritedFrom);

        var filled = values.Where(v => v.Value.Value is not null).Select(v => v.Key).ToHashSet();
        var (count, inherited) = LocationValues.Filled(tree, [address, notes, hours], filled, room);
        Assert.AreEqual(2, count);
        CollectionAssert.AreEqual(new[] { hours.Id }, inherited.Select(f => f.Id).ToArray());
    }
}
