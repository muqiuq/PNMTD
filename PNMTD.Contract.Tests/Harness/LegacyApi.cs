using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace PNMTD.Contract.Tests.Harness;

/// <summary>What the cases left behind, read back through whatever API the version under test has.</summary>
public interface IStateProbe
{
    Task<SortedDictionary<string, RecordedSensorState>> CaptureAsync();
}

/// <summary>
/// The v0.12 management API with the WebApp's JWT, fetched from /trust/create-web-app-token
/// (anonymous for the first caller of a fresh data folder). Used to build the fixture and to read
/// state back from v0.12.1 and from builds that still have these routes (phases 0–1).
/// </summary>
public sealed class LegacyApi(HttpClient client)
{
    private string? token;

    private async Task<HttpClient> AuthorizedAsync()
    {
        if (token is null)
        {
            using var response = await client.GetAsync("/trust/create-web-app-token");
            response.EnsureSuccessStatusCode();
            token = (await response.Content.ReadAsStringAsync()).Trim('"');
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    public async Task<JsonArray> GetArrayAsync(string path)
    {
        var c = await AuthorizedAsync();
        return (await c.GetFromJsonAsync<JsonArray>(path))!;
    }

    public async Task<JsonObject> GetObjectAsync(string path)
    {
        var c = await AuthorizedAsync();
        return (await c.GetFromJsonAsync<JsonObject>(path))!;
    }

    public async Task<Guid> PostAsync(string path, object body)
    {
        var c = await AuthorizedAsync();
        using var response = await c.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return Guid.TryParse(result["data"]?.ToString(), out var id) ? id : Guid.Empty;
    }

    public async Task PutAsync(string path, JsonNode body)
    {
        var c = await AuthorizedAsync();
        using var response = await c.PutAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Creates the host and every fixture sensor, with the fixture's tokens and flags.</summary>
    public async Task CreateFixtureAsync()
    {
        var hostId = await PostAsync("/host", new { name = Fixture.HostName, location = "contract", enabled = true });
        foreach (var s in Fixture.Sensors)
        {
            var id = await PostAsync("/sensor", new
            {
                parentId = hostId,
                type = s.Type,
                name = s.Alias,
                enabled = true,
                interval = s.Interval,
                gracePeriod = s.GracePeriod,
                parameters = s.Parameters,
            });
            // PUT takes the tokens from the client (plan §2.3 issue 2), which gives the fixture known tokens.
            var sensor = await GetObjectAsync($"/sensor/{id}");
            sensor["secretWriteToken"] = s.WriteToken;
            sensor["secretReadToken"] = s.ReadToken;
            sensor["enabled"] = s.Enabled;
            sensor["ignore"] = s.Ignore;
            sensor.Remove("parent");
            await PutAsync("/sensor", sensor);
        }
    }
}

public static class ImportFixture
{
    /// <summary>
    /// What the importer test needs beyond the contract's events: a notification rule linked to two
    /// sensors (NotificationTask then writes notificationruleevent rows), a mail input rule, a DNS
    /// zone (DnsCheckTask parses it into entries and log lines) and a key value. Mail log rows are
    /// added by tools/contract.sh with sqlite3: only an IMAP mailbox creates them.
    /// </summary>
    public static async Task CreateExtrasAsync(LegacyApi api)
    {
        var sensors = await api.GetArrayAsync("/sensor");
        Guid IdOf(string name) => Guid.Parse(sensors.Single(s => s!["name"]?.GetValue<string>() == name)!["id"]!.GetValue<string>());

        var rule = await api.PostAsync("/notificationrule", new
        {
            name = "ops webhook",
            // E-mail without SMTP settings: the notifier only warns, so the task completes and writes
            // its notificationruleevent rows (a failing webhook makes it throw before it writes any).
            recipient = "ops@example.com",
            enabled = true,
            type = 0,
            subscribedSensors = Array.Empty<Guid>(),
        });
        await api.PostAsync($"/notificationrule/{rule}/link-sensor/{IdOf("simple17")}", new { });
        await api.PostAsync($"/notificationrule/{rule}/link-sensor/{IdOf("readfail")}", new { });

        await api.PostAsync("/mailinput", new
        {
            name = "backup mail",
            enabled = true,
            fromTest = "backup@",
            subjectTest = "Backup",
            okTest = "success",
            okCode = 200,
            failTest = "failed",
            failCode = 500,
            defaultCode = 500,
            extractMessageRegex = "Result: (.*)",
            sensorOutputId = IdOf("simple25"),
        });

        var host = (await api.GetArrayAsync("/host")).Single()!["id"]!.GetValue<string>();
        await api.PostAsync("/dnszone", new
        {
            zoneName = "example.com",
            // The format v0.12's parser takes: absolute names, SOA first (PNMTD/Testing/DbTestHelper.cs).
            zoneFileContent = "example.com. 300 IN SOA ns1.example.com. ns2.example.com. (\n1681728879\n3600\n300\n1814400\n300\n)\n" +
                "example.com. 300 IN A 203.0.113.12\nwww.example.com. 300 IN CNAME example.com.\nexample.com. 300 IN TXT \"v=spf1 -all\"\n",
            interval = 3600,
            enabled = true,
            requiresProcessing = true,
            hostId = host,
        });

        await api.PostAsync("/keyvalue", new { key = "contract_note", value = "kept as is", note = "importer fixture", enabled = true, isReadOnly = false });
    }
}

public sealed class LegacyApiProbe(LegacyApi api) : IStateProbe
{
    public async Task<SortedDictionary<string, RecordedSensorState>> CaptureAsync()
    {
        var sensors = await api.GetArrayAsync("/sensor");
        var byId = sensors.Select(n => n!.AsObject()).ToList();
        var state = new SortedDictionary<string, RecordedSensorState>(StringComparer.Ordinal);

        foreach (var fixtureSensor in Fixture.Sensors)
        {
            var sensor = byId.Single(s => s["name"]?.GetValue<string>() == fixtureSensor.Alias && s["olderSiblingId"] is null);
            var id = sensor["id"]!.GetValue<string>();
            var siblings = new List<RecordedSibling>();
            foreach (var sibling in byId.Where(s => s["olderSiblingId"]?.GetValue<string>() == id))
            {
                siblings.Add(new RecordedSibling
                {
                    Name = sibling["name"]?.GetValue<string>(),
                    Type = sibling["type"]!.GetValue<int>(),
                    Enabled = sibling["enabled"]!.GetValue<bool>(),
                    Ignore = sibling["ignore"]!.GetValue<bool>(),
                    Interval = sibling["interval"]!.GetValue<int>(),
                    GracePeriod = sibling["gracePeriod"]!.GetValue<int>(),
                    Parameters = sibling["parameters"]?.GetValue<string>(),
                    Events = await EventsAsync(sibling["id"]!.GetValue<string>()),
                });
            }
            state[fixtureSensor.Alias] = new RecordedSensorState
            {
                Events = await EventsAsync(id),
                Siblings = siblings
                    .OrderBy(s => s.Name, StringComparer.Ordinal)
                    .ThenBy(s => string.Join("|", s.Events.Select(e => $"{e.Code}:{e.Message}")), StringComparer.Ordinal)
                    .ToList(),
            };
        }
        return state;
    }

    private async Task<List<RecordedEvent>> EventsAsync(string sensorId)
    {
        var events = await api.GetArrayAsync($"/events/sensor/{sensorId}");
        return events
            .Select(e => e!.AsObject())
            .OrderBy(e => e["created"]!.GetValue<DateTime>())
            .ThenBy(e => e["statusCode"]!.GetValue<int>())
            .Select(e => new RecordedEvent
            {
                Code = e["statusCode"]!.GetValue<int>(),
                Message = e["message"]?.GetValue<string>(),
                Source = Normalizer.Source(e["source"]?.GetValue<string>()),
            })
            .ToList();
    }
}
