using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Lib.Models.Enum;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Services.Domain;

/// <summary>
/// Update jobs (docs/mikrotik-plan.md §7): checked before they exist (HTTPS, the credentials and the admin's
/// policies on every router), then queued for <see cref="JobRunner"/> with the password in the
/// <see cref="CredentialVault"/> only. Listing, a job's log, cancelling, and the changelog of a release.
/// </summary>
public sealed class MikrotikJobService(
    PnmtdDbContext db,
    RouterPollService polls,
    CredentialVault vault,
    JobRunner runner,
    IHttpClientFactory http,
    IOptionsMonitor<MikrotikOptions> options,
    IHttpContextAccessor accessor)
{
    public static readonly string[] Channels = ["stable", "long-term", "testing", "development"];

    /// <summary>What an update needs (M5's check on 7.25.1): check-for-updates needs policy, the reboot reboot.</summary>
    public static readonly string[] UpdatePolicies = ["ssh", "read", "write", "policy", "reboot"];

    /// <summary>And a binary backup: backup save needs test and sensitive too, SFTP ftp.</summary>
    public static readonly string[] BackupPolicies = ["test", "sensitive", "ftp"];

    private static readonly ConcurrentDictionary<string, string> Changelogs = new();

    public async Task<MikrotikJobsCreated> CreateAsync(MikrotikJobRequest request, CancellationToken ct)
    {
        RequireHttps();
        if (request.Kind != MikrotikJobKind.Update)
        {
            throw ApiException.Invalid("kind", "Updates only here; commands have their own endpoint.");
        }
        var v = new ApiValidator();
        var username = request.Username?.Trim();
        v.Check(request.SensorIds.Count is >= 1 and <= 500, "sensorIds", "Between 1 and 500 routers.");
        v.Check(username is { Length: > 0 and <= 100 }, "username", "The router admin's username.");
        v.Check(!string.IsNullOrEmpty(request.Password), "password", "The router admin's password, for this update only.");
        v.Check(request.Channel is null || Channels.Contains(request.Channel), "channel", string.Join(", ", Channels) + ", or empty for each router's own.");
        v.ThrowIfInvalid();

        var ids = request.SensorIds.Distinct().ToList();
        var routers = await db.MikrotikRouters.Include(r => r.Sensor).ThenInclude(s => s.Parent).Where(r => ids.Contains(r.SensorId)).ToListAsync(ct);
        var settings = await db.MikrotikSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == MikrotikSettingsEntity.SingletonId, ct);
        var latest = VersionCatalog.Read(settings?.LatestVersions);
        var needed = request.BinaryBackup ? UpdatePolicies.Concat(BackupPolicies).ToArray() : UpdatePolicies;
        var refused = new List<MikrotikJobRefusal>();
        var accepted = new List<(MikrotikRouterEntity Router, string Channel, string Target)>();
        foreach (var id in ids.Where(id => routers.All(r => r.SensorId != id)))
        {
            refused.Add(new MikrotikJobRefusal(id, "?", "Not a MikroTik router."));
        }
        foreach (var router in routers)
        {
            var name = router.Sensor.Name;
            var channel = request.Channel ?? router.Channel ?? "stable";
            string? reason = !router.Mode.UsesSsh() ? "WireGuard only: no login, no updates."
                : !router.Sensor.Enabled ? "The sensor is disabled."
                : await db.MikrotikJobs.AnyAsync(j => j.SensorId == router.SensorId && (j.State == MikrotikJobState.Queued || j.State == MikrotikJobState.Running), ct)
                    ? "A job is queued or running for it already."
                : !latest.TryGetValue(channel, out var target) ? $"The newest version of {channel} is not known yet (the version check)."
                : RouterOsVersion.TryParse(router.Version, out var installed) && RouterOsVersion.TryParse(target.Version, out var t) && installed >= t
                    ? $"Already on {router.Version}, the newest of {channel}."
                : null;
            reason ??= (await CheckCredentialsAsync(router, username!, request.Password!, needed, ct)).Refusal;
            if (reason is not null)
            {
                refused.Add(new MikrotikJobRefusal(router.SensorId, name, reason));
            }
            else
            {
                accepted.Add((router, channel, latest[channel].Version));
            }
        }
        var batchId = Guid.NewGuid();
        var (userId, userName) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        var jobOptions = JsonSerializer.Serialize(new UpdateJobOptions(request.BinaryBackup, request.Firmware, request.StopAtFirstFailure), VersionCatalog.Json);
        var jobs = accepted.Select((a, i) => new MikrotikJobEntity
        {
            Id = Guid.NewGuid(), BatchId = batchId, Kind = MikrotikJobKind.Update, SensorId = a.Router.SensorId, RouterName = MikrotikJobService.DisplayName(a.Router),
            State = MikrotikJobState.Queued, Step = MikrotikJobStep.Check, RequestedBy = userId, RequestedByName = userName ?? "cli",
            AdminUsername = username, CreatedAt = now.AddMicroseconds(i), FromVersion = a.Router.Version, ToVersion = a.Target, Channel = a.Channel,
            Options = jobOptions,
        }).ToList();
        if (jobs.Count > 0)
        {
            vault.Put(batchId, username!, request.Password!);
            db.MikrotikJobs.AddRange(jobs);
            UdpViews.Audit(db, accessor, AuthEvents.MikrotikUpdateStarted,
                $"update of {jobs.Count} router(s) as {username}: {string.Join(", ", jobs.Select(j => $"{j.RouterName} → {j.ToVersion}"))}");
            await db.SaveChangesAsync(ct);
            runner.Wake();
        }
        return new MikrotikJobsCreated(batchId, jobs.Select(Item).ToList(), refused);
    }

    /// <summary>A router's admin password only over HTTPS (§7.2), unless allowed for development. The CLI has no request.</summary>
    public void RequireHttps()
    {
        if (accessor.HttpContext is { } http && !http.Request.IsHttps && !options.CurrentValue.AllowPasswordsOverHttp)
        {
            throw ApiException.Invalid("password", "A router's admin password only over HTTPS (Mikrotik__AllowPasswordsOverHttp=true for development).");
        }
    }

    /// <summary>
    /// Logs in as the admin and reads the user's group (§7.2): "wrong password", or which policies are missing;
    /// else the group, as "full (ssh, read, write, …)". The password goes only to a pinned host key: a router
    /// PNMTD never logged in to is refused.
    /// </summary>
    public async Task<(string? Refusal, string? Group)> CheckCredentialsAsync(MikrotikRouterEntity router, string username, string password,
        string[] needed, CancellationToken ct)
    {
        if (router.HostKeyFingerprint is null)
        {
            return ("PNMTD has not logged in to it yet, so its host key is not pinned: the password goes only to a known router.", null);
        }
        var (target, error) = await polls.TargetAsync(db, router, username, RouterCredentials.ForPassword(password), ct);
        if (target is null)
        {
            return (error, null);
        }
        try
        {
            await using var session = await polls.Connector.ConnectAsync(target, ct);
            var exec = await session.ExecAsync(
                $"{{ :local g [/user/get [find name={RouterOsScript.Quote(username)}] group]; :put [:serialize to=json [/user/group/print as-value proplist=name,policy where name=$g]] }}",
                TimeSpan.FromSeconds(30), 64 * 1024, ct);
            using var json = JsonDocument.Parse(exec.Output.Trim());
            var group = json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0 ? json.RootElement[0] : json.RootElement;
            var policies = group.List("policy").Where(p => !p.StartsWith('!')).ToList();
            var missing = needed.Where(p => !policies.Contains(p)).ToList();
            return missing.Count == 0
                ? (null, $"{group.Text("name") ?? "?"} ({string.Join(", ", policies)})")
                : ($"{username} lacks the policies {string.Join(", ", missing)}.", null);
        }
        catch (RouterSessionException ex)
        {
            return (ex.Kind == RouterFailureKind.AuthenticationFailed ? $"Wrong username or password for {router.Sensor.Name}." : ex.Message, null);
        }
        catch (JsonException)
        {
            return ($"The group of {username} could not be read.", null);
        }
    }

    public async Task<Paged<MikrotikJobItem>> ListAsync(MikrotikJobQuery query, CancellationToken ct)
    {
        var jobs = db.MikrotikJobs.AsNoTracking();
        if (query.Kind is { } kind) jobs = jobs.Where(j => j.Kind == kind);
        if (query.State is { } state) jobs = jobs.Where(j => j.State == state);
        if (query.BatchId is { } batch) jobs = jobs.Where(j => j.BatchId == batch);
        if (query.SensorId is { } sensor) jobs = jobs.Where(j => j.SensorId == sensor);
        if (Text.Trimmed(query.Search) is { } search) jobs = jobs.Where(j => EF.Functions.ILike(j.RouterName, $"%{search}%"));
        var page = await Paging.PageAsync(MikrotikCommandService.WithoutOutput(jobs.OrderByDescending(j => j.CreatedAt)), query, ct);
        return new Paged<MikrotikJobItem>(page.Items.Select(Item).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>A job with its log. Through a router (<paramref name="sensorId"/>, CanRead) updates only: commands have their own rights (§8.3).</summary>
    public async Task<MikrotikJobDetail> GetAsync(Guid id, Guid? sensorId, CancellationToken ct)
    {
        var job = await db.MikrotikJobs.AsNoTracking()
            .SingleOrDefaultAsync(j => j.Id == id && (sensorId == null || (j.SensorId == sensorId && j.Kind == MikrotikJobKind.Update)), ct)
            ?? throw ApiException.NotFound("Job");
        var log = await db.MikrotikJobLog.AsNoTracking().Where(l => l.JobId == id).OrderBy(l => l.Id).ToListAsync(ct);
        return new MikrotikJobDetail(Item(job), log.Select(l => new MikrotikJobLogItem(l.Id, Instants.Utc(l.At), l.Step, l.Outcome, l.Message, l.Output)).ToList());
    }

    /// <summary>Until the reboot (§7.3): after it, the router is in the middle of the update.</summary>
    public async Task CancelAsync(Guid id, CancellationToken ct)
    {
        var job = await db.MikrotikJobs.SingleOrDefaultAsync(j => j.Id == id, ct) ?? throw ApiException.NotFound("Job");
        if (job.State is not (MikrotikJobState.Queued or MikrotikJobState.Running))
        {
            throw ApiException.Conflict("job_finished", "The job has finished.");
        }
        if (job.State == MikrotikJobState.Running && job.Step >= MikrotikJobStep.Reboot)
        {
            throw ApiException.Conflict("job_rebooting", "The router is rebooting with its update: the job cannot be cancelled any more.");
        }
        job.State = MikrotikJobState.Cancelled;
        if (job.StartedAt is null)
        {
            job.FinishedAt = Clock.UtcNow;
            job.Error = "Cancelled before it started.";
        }
        UdpViews.Audit(db, accessor, AuthEvents.MikrotikUpdateCancelled, $"update of {job.RouterName} to {job.ToVersion} cancelled");
        await db.SaveChangesAsync(ct);
        if (!await db.MikrotikJobs.AnyAsync(j => j.BatchId == job.BatchId && (j.State == MikrotikJobState.Queued || j.State == MikrotikJobState.Running), ct))
        {
            vault.Remove(job.BatchId);
        }
    }

    /// <summary>A release's changelog (§5.4), cached in memory: "What's new in 7.25.1 (…)".</summary>
    public async Task<MikrotikChangelog> ChangelogAsync(string version, CancellationToken ct)
    {
        if (!RouterOsVersion.TryParse(version, out var v))
        {
            throw ApiException.Invalid("version", "A RouterOS version such as 7.25.1.");
        }
        var key = v.ToString();
        if (!Changelogs.TryGetValue(key, out var text))
        {
            var baseUrl = options.CurrentValue.VersionCheck.BaseUrl.TrimEnd('/') + "/";
            try
            {
                var client = http.CreateClient(nameof(VersionCatalog));
                client.Timeout = TimeSpan.FromSeconds(20);
                text = await client.GetStringAsync($"{baseUrl}{key}/CHANGELOG", ct);
                Changelogs[key] = text.Length > 256 * 1024 ? text[..(256 * 1024)] : text;
            }
            catch (HttpRequestException ex)
            {
                throw ApiException.Conflict("changelog_unavailable", $"MikroTik's update server did not answer: {ex.Message}");
            }
        }
        return new MikrotikChangelog(key, Changelogs[key]);
    }

    /// <summary>A job's router as "host/sensor": routers are often all named "router", one per host.</summary>
    public static string DisplayName(MikrotikRouterEntity router) =>
        router.Sensor.Parent is { } host ? $"{host.Name}/{router.Sensor.Name}" : router.Sensor.Name;

    public static MikrotikJobItem Item(MikrotikJobEntity j) => new(j.Id, j.BatchId, j.Kind, j.SensorId, j.RouterName, j.State, j.Step, j.RequestedByName,
        j.AdminUsername, Instants.Utc(j.CreatedAt), UdpViews.NullableUtc(j.StartedAt), UdpViews.NullableUtc(j.FinishedAt), j.FromVersion, j.ToVersion,
        j.Channel, j.Error, j.Command, j.RunAs);
}
