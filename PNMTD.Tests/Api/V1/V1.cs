using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Api.V1;

/// <summary>Creating test data through the API, and events with set times straight in the database.</summary>
internal static class V1
{
    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, 60)];

    /// <summary>Where a section of Admin → Cookie is saved: the Redmine section has its own controller (docs/cookie-redmine-tab-plan.md §3.2).</summary>
    public static string CookieSettingsPath(string section) =>
        section == "redmine" ? "/api/v1/cookie/admin/redmine/settings" : $"/api/v1/cookie/admin/settings/{section}";

    public static async Task<HostDetail> CreateHostAsync(HttpClient client, string? name = null, bool enabled = true)
    {
        using var response = await client.PostJsonAsync("/api/v1/hosts", new { name = name ?? Unique("host"), location = "lab", enabled });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<HostDetail>();
    }

    public static async Task<SensorDetail> CreateSensorAsync(HttpClient client, Guid hostId, SensorType type = SensorType.SIMPLE,
        string? name = null, string? parameters = null, int interval = 0, bool enabled = true, bool ignore = false)
    {
        using var response = await client.PostJsonAsync("/api/v1/sensors",
            new { hostId, type, name = name ?? Unique("sensor"), parameters, interval, enabled, ignore });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<SensorDetail>();
    }

    /// <summary>
    /// The availability plan's working hours (docs/cookie-plan.md §7.3): <c>[]</c> is always open, null the default week. The
    /// types and the other settings stay as they are.
    /// </summary>
    public static async Task AvailabilityHoursAsync(HttpClient admin, List<PNMTD.Cookie.DayHours>? week)
    {
        var current = await (await admin.GetAsync("/api/v1/availability/settings")).ReadAsync<AvailabilitySettingsItem>();
        var types = current.Types.Where(t => !t.Retired)
            .Select(t => new AvailabilityTypeInput(t.Id, t.Name, t.Code, t.Color, t.Flags, t.IsDefault, t.IsDayOff)).ToList();
        var settings = current.Settings with { Week = week ?? [.. PNMTD.Cookie.WorkingCalendar.DefaultWeek] };
        using var response = await admin.PutAsJsonAsync("/api/v1/availability/settings", new AvailabilitySettingsRequest(types, settings),
            PNMTD.Availability.AvailabilitySettings.Json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Events with set times, as the sensor API would have stored them.</summary>
    public static void AddEvents(TestApp app, Guid sensorId, params (DateTime At, int Code, string? Message)[] events)
    {
        using var db = app.NewDb();
        foreach (var (at, code, message) in events)
        {
            db.Events.Add(new EventEntity { Id = Guid.NewGuid(), SensorId = sensorId, Created = at, Code = code, Message = message, Source = "203.0.113.5:4711" });
        }
        db.SaveChanges();
    }

    public static async Task<string> CodeOf(HttpResponseMessage response) =>
        (await response.ProblemAsync()).GetProperty("code").GetString()!;

    public static async Task<JsonElement> ErrorsOf(HttpResponseMessage response) =>
        (await response.ProblemAsync()).GetProperty("errors");
}
