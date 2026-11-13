using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PNMTD.Agents;
using PNMTD.Data;
using PNMTD.Lib.Agent;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Windows;

/// <summary>
/// Sections (docs/windows-sensor-plan.md §4, §4.9): stored gzip-compressed, one per server and kind. A section with the hash
/// already stored only moves its times; one with a new hash is compared with the stored one (the differs write the readable
/// changes and 253) and replaces it.
/// </summary>
public static class WindowsSections
{
    /// <summary>A section's JSON may be this large decompressed.</summary>
    public const int MaxJsonBytes = 8 * 1024 * 1024;

    public static byte[] Compress(string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(json));
        }
        return output.ToArray();
    }

    public static string Decompress(byte[] content)
    {
        using var input = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static async Task<T?> ReadAsync<T>(PnmtdDbContext db, Guid sensorId, string name, CancellationToken ct) where T : class
    {
        var row = await db.WindowsSections.AsNoTracking().SingleOrDefaultAsync(s => s.SensorId == sensorId && s.Section == name, ct);
        return row is null ? null : Parse<T>(row.Content);
    }

    public static T? Parse<T>(byte[] content) where T : class
    {
        try
        {
            return AgentJson.Deserialize<T>(Decompress(content));
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Stores a section; answers the stored one it replaced when the hash changed, else null.</summary>
    public static async Task<(WindowsSectionEntity Row, byte[]? Previous)> StoreAsync(PnmtdDbContext db, Guid sensorId, SectionMessage message, CancellationToken ct)
    {
        var json = message.Data.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes)
        {
            throw new InvalidOperationException($"The section {message.Name} is above {MaxJsonBytes / 1024 / 1024} MB.");
        }
        var now = Clock.UtcNow;
        var row = await db.WindowsSections.SingleOrDefaultAsync(s => s.SensorId == sensorId && s.Section == message.Name, ct);
        var errors = (message.Errors ?? []).Take(50).Select(e => Cuts.Cut(e, 500)).ToList();
        var hash = Cuts.Cut(message.Hash, 64);
        if (row is not null && row.Hash == hash && row.SchemaVersion == message.SchemaVersion)
        {
            row.CollectedAt = message.CollectedAt.UtcDateTime;
            row.ReceivedAt = now;
            row.Errors = errors;
            return (row, null);
        }
        var previous = row?.Content;
        if (row is null)
        {
            row = new WindowsSectionEntity { SensorId = sensorId, Section = message.Name };
            db.WindowsSections.Add(row);
        }
        row.SchemaVersion = message.SchemaVersion;
        row.Hash = hash;
        row.CollectedAt = message.CollectedAt.UtcDateTime;
        row.ReceivedAt = now;
        row.ChangedAt = now;
        row.Content = Compress(json);
        row.Errors = errors;
        return (row, previous);
    }
}
