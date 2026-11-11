using Npgsql;

namespace PNMTD.Contract.Tests.Harness;

/// <summary>
/// What the cases left behind, read straight from the 1.0 database (tools/contract.sh publishes its
/// port on loopback and sets CONTRACT_DB_CONNECTION). Produces the same shape as
/// <see cref="LegacyApiProbe"/>, which read v0.12's management API, so the golden state recorded from
/// v0.12.1 compares as it is.
/// </summary>
public sealed class DatabaseProbe(string connectionString) : IStateProbe
{
    private sealed record Sensor(Guid Id, Guid? OlderSiblingId, string? Name, int Type, bool Enabled, bool Ignore,
        int Interval, int GracePeriod, string? Parameters);

    public async Task<SortedDictionary<string, RecordedSensorState>> CaptureAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var sensors = new List<Sensor>();
        await using (var command = new NpgsqlCommand(
            """SELECT "Id", "OlderSiblingId", "Name", "Type", "Enabled", "Ignore", "Interval", "GracePeriod", "Parameters" FROM sensors""",
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                sensors.Add(new Sensor(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetInt32(6), reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        // In the order LegacyApiProbe sorted them: by time, then code.
        var events = new Dictionary<Guid, List<RecordedEvent>>();
        await using (var command = new NpgsqlCommand(
            """SELECT "SensorId", "Code", "Message", "Source" FROM events ORDER BY "SensorId", "Created", "Code", "Message" """,
            connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var sensorId = reader.GetGuid(0);
                if (!events.TryGetValue(sensorId, out var list))
                {
                    events[sensorId] = list = [];
                }
                list.Add(new RecordedEvent
                {
                    Code = reader.GetInt32(1),
                    Message = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Source = Normalizer.Source(reader.IsDBNull(3) ? null : reader.GetString(3)),
                });
            }
        }
        List<RecordedEvent> EventsOf(Guid id) => events.TryGetValue(id, out var list) ? list : [];

        var state = new SortedDictionary<string, RecordedSensorState>(StringComparer.Ordinal);
        foreach (var fixtureSensor in Fixture.Sensors)
        {
            var sensor = sensors.Single(s => s.Name == fixtureSensor.Alias && s.OlderSiblingId is null);
            var siblings = sensors.Where(s => s.OlderSiblingId == sensor.Id)
                .Select(s => new RecordedSibling
                {
                    Name = s.Name,
                    Type = s.Type,
                    Enabled = s.Enabled,
                    Ignore = s.Ignore,
                    Interval = s.Interval,
                    GracePeriod = s.GracePeriod,
                    Parameters = s.Parameters,
                    Events = EventsOf(s.Id),
                })
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ThenBy(s => string.Join("|", s.Events.Select(e => $"{e.Code}:{e.Message}")), StringComparer.Ordinal)
                .ToList();
            state[fixtureSensor.Alias] = new RecordedSensorState { Events = EventsOf(sensor.Id), Siblings = siblings };
        }
        return state;
    }
}
