using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Mikrotik;

/// <summary>The newest version of a channel, as MikroTik's update server names it.</summary>
public sealed record LatestVersion(string Version, DateTime? ReleasedAt);

/// <summary>
/// The newest RouterOS version of each channel (docs/mikrotik-plan.md §5.4), fetched centrally from
/// <c>NEWESTa7.&lt;channel&gt;</c>, whose answer is the version and its release time ("7.25.1 1793623193"),
/// and kept in <c>mikrotik_settings.latest_versions</c>. No router needs rights for it.
/// </summary>
public sealed class VersionCatalog(
    IDbContextFactory<PnmtdDbContext> dbs,
    IHttpClientFactory http,
    IOptionsMonitor<MikrotikOptions> options,
    ILogger<VersionCatalog> logger) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A little after the start, so a restart loop does not hammer the update server.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            var o = options.CurrentValue;
            if (o.Enabled && o.VersionCheck.Enabled)
            {
                try
                {
                    await RefreshAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The RouterOS version check failed");
                }
            }
            await Task.Delay(TimeSpan.FromHours(Math.Max(1, o.VersionCheck.IntervalHours)), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Fetches every channel and stores what it got; a channel that fails keeps its last value.</summary>
    public async Task<IReadOnlyDictionary<string, LatestVersion>> RefreshAsync(CancellationToken ct)
    {
        var o = options.CurrentValue.VersionCheck;
        var client = http.CreateClient(nameof(VersionCatalog));
        client.Timeout = TimeSpan.FromSeconds(20);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var settings = await db.MikrotikSettings.SingleOrDefaultAsync(s => s.Id == MikrotikSettingsEntity.SingletonId, ct);
        if (settings is null)
        {
            // The key store creates the row with its tag; the catalog waits for it rather than racing it.
            return new Dictionary<string, LatestVersion>();
        }
        var latest = Read(settings.LatestVersions).ToDictionary(kv => kv.Key, kv => kv.Value);
        var errors = new List<string>();
        var baseUrl = o.BaseUrl.EndsWith('/') ? o.BaseUrl : o.BaseUrl + "/";
        foreach (var channel in o.Channels)
        {
            try
            {
                var text = await client.GetStringAsync($"{baseUrl}NEWESTa7.{channel}", ct);
                if (TryParse(text, out var version))
                {
                    latest[channel] = version;
                }
                else
                {
                    errors.Add($"{channel}: unexpected answer");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                errors.Add($"{channel}: {ex.Message}");
            }
        }
        settings.LatestVersions = JsonSerializer.Serialize(latest, Json);
        settings.VersionsCheckedAt = Clock.UtcNow;
        settings.VersionCheckError = errors.Count == 0 ? null : string.Join("; ", errors);
        db.UpdateKeyValueTimestampToNow(KeyValueKeyEnums.LAST_MIKROTIK_VERSION_CHECK);
        await db.SaveChangesAsync(ct);
        return latest;
    }

    /// <summary>"7.25.1 1793623193": the version and a Unix time; the time is optional.</summary>
    public static bool TryParse(string text, out LatestVersion version)
    {
        version = new LatestVersion("", null);
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || !RouterOsVersion.TryParse(parts[0], out _))
        {
            return false;
        }
        DateTime? released = parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds is > 0 and < 32_503_680_000
            ? DateTime.UnixEpoch.AddSeconds(seconds)
            : null;
        version = new LatestVersion(parts[0], released);
        return true;
    }

    public static IReadOnlyDictionary<string, LatestVersion> Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return new Dictionary<string, LatestVersion>();
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, LatestVersion>>(json, Json) ?? new Dictionary<string, LatestVersion>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, LatestVersion>();
        }
    }
}
