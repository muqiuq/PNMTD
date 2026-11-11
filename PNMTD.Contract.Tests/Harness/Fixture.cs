using System.Security.Cryptography;
using System.Text;

namespace PNMTD.Contract.Tests.Harness;

/// <summary>Sensor types as the v0.12 API serializes them (numbers).</summary>
public static class SensorTypes
{
    public const int Simple = 10;
    public const int Heartbeat = 20;
    public const int HeartbeatValueCheck = 25;
    public const int ValueCheck = 40;
    public const int Encapsulated = 50;
    public const int OneWithinTimespan = 60;
    public const int AllWithinTimespan = 61;
}

/// <summary>
/// One sensor of the fixture database. The alias is also the sensor's name, so a probe can find it
/// again in any version. Tokens are derived from the alias (or from <see cref="TokenAlias"/>, which
/// lets two sensors share a token on purpose).
/// </summary>
/// <remarks>
/// Intervals are a day, so the heartbeat task never adds a 460 during a run. Timespan sensors get an
/// invalid descriptor, so the timespan task skips them and only the events a case submits exist.
/// </remarks>
public sealed record FixtureSensor(
    string Alias,
    int Type = SensorTypes.Simple,
    bool Enabled = true,
    bool Ignore = false,
    string? Parameters = null,
    string? TokenAlias = null)
{
    public int Interval => 86400;
    public int GracePeriod => 86400;
    public string WriteToken => Tokens.Write(TokenAlias ?? Alias);
    public string ReadToken => Tokens.Read(TokenAlias ?? Alias);
}

public static class Tokens
{
    /// <summary>Same shape as real tokens: 7 hex digits, a dash, a lower-case GUID.</summary>
    public static string Write(string alias) => Make("w:" + alias);

    public static string Read(string alias) => Make("r:" + alias);

    private static string Make(string seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("pnmt-contract/" + seed));
        var prefix = Convert.ToHexString(hash, 0, 4)[..7].ToLowerInvariant();
        var guid = new Guid(hash.AsSpan(8, 16));
        return $"{prefix}-{guid}";
    }
}

public static class Fixture
{
    public const string HostName = "contract-host";

    public static readonly IReadOnlyList<FixtureSensor> Sensors = Build();

    private static List<FixtureSensor> Build()
    {
        var list = new List<FixtureSensor>();
        for (var i = 1; i <= 40; i++)
        {
            list.Add(new FixtureSensor($"simple{i:00}"));
        }
        list.AddRange(
        [
            new("disabled", Enabled: false),
            new("ignored", Ignore: true),
            new("dupA", TokenAlias: "dup"),
            new("dupB", TokenAlias: "dup"),
            new("rate"),
            new("rate2"),
            new("dedupe"),
            new("dedupePost"),
            new("vc1", SensorTypes.ValueCheck, Parameters: "^OK"),
            new("vc2", SensorTypes.ValueCheck, Parameters: "^OK"),
            new("vc3", SensorTypes.ValueCheck, Parameters: "^OK"),
            new("vcpost", SensorTypes.ValueCheck, Parameters: "^OK"),
            new("vcbad", SensorTypes.ValueCheck, Parameters: "(["),
            new("vcnull", SensorTypes.ValueCheck),
            new("hbvc", SensorTypes.HeartbeatValueCheck, Parameters: @"^\d+$"),
            new("hb", SensorTypes.Heartbeat),
            new("enc1", SensorTypes.Encapsulated),
            new("enc2", SensorTypes.Encapsulated),
            new("enc3", SensorTypes.Encapsulated),
            new("enc4", SensorTypes.Encapsulated),
            new("enc5", SensorTypes.Encapsulated),
            new("encmax", SensorTypes.Encapsulated),
            new("readmsg"),
            new("readnone"),
            new("readfail"),
            new("readnull"),
            new("readempty"),
            new("readdisabled", Enabled: false),
            new("readignored", Ignore: true),
            new("oneA", SensorTypes.OneWithinTimespan, Parameters: "invalid"),
            new("oneB", SensorTypes.OneWithinTimespan, Parameters: "invalid"),
            new("allA", SensorTypes.AllWithinTimespan, Parameters: "invalid"),
            new("allB", SensorTypes.AllWithinTimespan, Parameters: "invalid"),
        ]);
        return list;
    }

    public static FixtureSensor Get(string alias) => Sensors.Single(s => s.Alias == alias);
}
