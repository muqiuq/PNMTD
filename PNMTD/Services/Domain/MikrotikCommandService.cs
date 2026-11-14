using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Mikrotik;
using PNMTD.Mikrotik;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Services.Domain;

/// <summary>
/// Remote commands (docs/mikrotik-plan.md §8): a text for one router or several, run as <c>pnmt</c> (CanWrite) or
/// as the router's admin (Admin, the password checked on every router first and then held in the
/// <see cref="CredentialVault"/> only). PNMTD sends the text as typed: the router enforces the user's rights.
/// The history is permanent, admin runs are an Admin's to see, and an Admin can redact a job.
/// </summary>
public sealed class MikrotikCommandService(
    PnmtdDbContext db,
    MikrotikJobService jobs,
    CredentialVault vault,
    JobRunner runner,
    IOptionsMonitor<MikrotikOptions> options,
    IHttpContextAccessor accessor)
{
    /// <summary>An admin run needs a login; the router enforces the rest with the admin's group (§8.1).</summary>
    public static readonly string[] AdminPolicies = ["ssh"];

    /// <summary>
    /// Checks and queues a batch, one job per router. With <paramref name="claim"/> the jobs are created as
    /// running, for the CLI to run in its own process (<see cref="JobRunner.RunHereAsync"/>).
    /// </summary>
    public async Task<MikrotikCommandsCreated> CreateAsync(MikrotikCommandRequest request, CancellationToken ct, bool claim = false)
    {
        var o = options.CurrentValue.Commands;
        var asAdmin = request.RunAs == MikrotikRunAs.Admin;
        if (asAdmin)
        {
            if (!IsAdmin())
            {
                throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.ForbiddenRole, "Commands as a router's admin are an Admin's.");
            }
            jobs.RequireHttps();
        }
        var text = (request.Command ?? "").ReplaceLineEndings("\n").TrimEnd();
        var username = request.Username?.Trim();
        var timeout = request.TimeoutSeconds ?? o.DefaultTimeoutSeconds;
        var v = new ApiValidator();
        v.Check(request.SensorIds.Count is >= 1 and <= 500, "sensorIds", "Between 1 and 500 routers.");
        v.Check(text.Trim().Length > 0, "command", "A RouterOS command or script.");
        v.Check(Encoding.UTF8.GetByteCount(text) <= o.MaxCommandBytes, "command", $"At most {o.MaxCommandBytes / 1024} KiB.");
        v.Check(timeout >= 5 && timeout <= o.MaxTimeoutSeconds, "timeoutSeconds", $"From 5 to {o.MaxTimeoutSeconds} seconds.");
        if (asAdmin)
        {
            v.Check(username is { Length: > 0 and <= 100 }, "username", "The router admin's username.");
            v.Check(!string.IsNullOrEmpty(request.Password), "password", "The router admin's password, for this run only.");
            v.Check(!string.Equals(username, RouterPoll.UserName, StringComparison.OrdinalIgnoreCase), "username", "pnmt is PNMT's own user: run as pnmt instead.");
        }
        else
        {
            v.Check(string.IsNullOrEmpty(request.Password), "password", "A run as pnmt takes no password.");
        }
        v.ThrowIfInvalid();

        var ids = request.SensorIds.Distinct().ToList();
        var routers = (await db.MikrotikRouters.Include(r => r.Sensor).ThenInclude(s => s.Parent).Where(r => ids.Contains(r.SensorId)).ToListAsync(ct))
            .OrderBy(r => ids.IndexOf(r.SensorId)).ToList();
        var updating = await db.MikrotikJobs
            .Where(j => j.Kind == MikrotikJobKind.Update && j.SensorId != null && ids.Contains(j.SensorId.Value)
                && (j.State == MikrotikJobState.Queued || j.State == MikrotikJobState.Running))
            .Select(j => j.SensorId!.Value).ToListAsync(ct);
        var refused = ids.Where(id => routers.All(r => r.SensorId != id)).Select(id => new MikrotikJobRefusal(id, "?", "Not a MikroTik router.")).ToList();
        var accepted = new List<(MikrotikRouterEntity Router, string? Group)>();
        foreach (var router in routers)
        {
            var reason = !router.Mode.UsesSsh() ? "WireGuard only: there is no login."
                : !router.Sensor.Enabled ? "The sensor is disabled."
                : updating.Contains(router.SensorId) ? "An update is queued or running on it."
                : router.HostKeyFingerprint is null ? "PNMTD has not logged in to it yet."
                : null;
            string? group = null;
            if (reason is null && asAdmin)
            {
                (reason, group) = await jobs.CheckCredentialsAsync(router, username!, request.Password!, AdminPolicies, ct);
            }
            if (reason is null)
            {
                accepted.Add((router, group));
            }
            else
            {
                refused.Add(new MikrotikJobRefusal(router.SensorId, router.Sensor.Name, reason));
            }
        }

        var batchId = Guid.NewGuid();
        var (userId, userName) = UdpViews.User(accessor);
        var now = Clock.UtcNow;
        var runAs = asAdmin ? username! : RouterPoll.UserName;
        var jobOptions = JsonSerializer.Serialize(new CommandJobOptions(request.BackupBeforeAndAfter ?? asAdmin, request.OneAtATime ?? asAdmin), VersionCatalog.Json);
        var created = accepted.Select((a, i) => new MikrotikJobEntity
        {
            Id = Guid.NewGuid(), BatchId = batchId, Kind = MikrotikJobKind.Command, SensorId = a.Router.SensorId, RouterName = MikrotikJobService.DisplayName(a.Router),
            State = claim ? MikrotikJobState.Running : MikrotikJobState.Queued, StartedAt = claim ? now : null, Step = MikrotikJobStep.Check,
            RequestedBy = userId, RequestedByName = userName ?? "cli", AdminUsername = asAdmin ? username : null, CreatedAt = now.AddMicroseconds(i),
            Command = text, RunAs = runAs, TimeoutSeconds = timeout, Options = jobOptions,
        }).ToList();
        if (created.Count > 0)
        {
            if (asAdmin)
            {
                vault.Put(batchId, username!, request.Password!);
            }
            db.MikrotikJobs.AddRange(created);
            UdpViews.Audit(db, accessor, AuditEvents.MikrotikCommandStarted,
                $"command on {created.Count} router(s) as {runAs} (batch {batchId}): {string.Join(", ", created.Select(j => j.RouterName))}");
            await db.SaveChangesAsync(ct);
            // The admin's group on each router, as checked (§7.2).
            foreach (var (job, group) in created.Zip(accepted.Select(a => a.Group)).Where(x => x.Second is not null))
            {
                db.MikrotikJobLog.Add(new MikrotikJobLogEntity
                {
                    JobId = job.Id, At = now, Step = MikrotikJobStep.Check, Outcome = MikrotikJobOutcome.Info,
                    Message = $"The password of {username} was accepted; its group is {group}.",
                });
            }
            await db.SaveChangesAsync(ct);
            if (!claim)
            {
                runner.Wake();
            }
        }
        return new MikrotikCommandsCreated(batchId, created.Select(Item).ToList(), refused);
    }

    public async Task<Paged<MikrotikCommandItem>> ListAsync(MikrotikCommandQuery query, CancellationToken ct)
    {
        var commands = Visible();
        if (query.SensorId is { } sensor) commands = commands.Where(j => j.SensorId == sensor);
        if (query.BatchId is { } batch) commands = commands.Where(j => j.BatchId == batch);
        if (query.State is { } state) commands = commands.Where(j => j.State == state);
        if (Text.Trimmed(query.Search) is { } search)
        {
            commands = commands.Where(j => EF.Functions.ILike(j.RouterName, $"%{search}%") || EF.Functions.ILike(j.Command!, $"%{search}%"));
        }
        var page = await Paging.PageAsync(WithoutOutput(commands.OrderByDescending(j => j.CreatedAt)), query, ct);
        return new Paged<MikrotikCommandItem>(page.Items.Select(Item).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<MikrotikCommandDetail> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await Visible().SingleOrDefaultAsync(j => j.Id == id, ct) ?? throw ApiException.NotFound("Command");
        var log = await db.MikrotikJobLog.AsNoTracking().Where(l => l.JobId == id).OrderBy(l => l.Id).ToListAsync(ct);
        int? added = null, removed = null;
        if (job.BackupBeforeId is { } before && job.BackupAfterId is { } after)
        {
            if (before == after)
            {
                (added, removed) = (0, 0);
            }
            else if (await db.MikrotikBackups.AsNoTracking().Where(b => b.Id == after).Select(b => new { b.LinesAdded, b.LinesRemoved }).SingleOrDefaultAsync(ct) is { } counts)
            {
                (added, removed) = (counts.LinesAdded, counts.LinesRemoved);
            }
        }
        return new MikrotikCommandDetail(Item(job), job.Output,
            log.Select(l => new MikrotikJobLogItem(l.Id, Instants.Utc(l.At), l.Step, l.Outcome, l.Message, l.Output)).ToList(), added, removed);
    }

    /// <summary>A batch's results grouped by outcome and identical output (§8.3).</summary>
    public async Task<MikrotikCommandBatch> BatchAsync(Guid batchId, CancellationToken ct)
    {
        var batch = await Visible().Where(j => j.BatchId == batchId).OrderBy(j => j.CreatedAt).ToListAsync(ct);
        if (batch.Count == 0)
        {
            throw ApiException.NotFound("Batch");
        }
        var groups = CommandOutput.Group(batch, j => $"{(int)j.State}\u0000{(j.Output ?? "").TrimEnd()}")
            .Select(g => new MikrotikCommandGroup(g[0].State, g[0].Output, g.Any(j => j.OutputTruncated), g.Select(Item).ToList()))
            .ToList();
        var first = batch[0];
        return new MikrotikCommandBatch(batchId, first.Command, first.RunAs ?? RouterPoll.UserName, first.AdminUsername is not null,
            first.RequestedByName, Instants.Utc(first.CreatedAt), groups);
    }

    /// <summary>One's own runs, or any as an Admin. A running command's session is closed; its export after is not interrupted.</summary>
    public async Task CancelAsync(Guid id, CancellationToken ct)
    {
        var admin = IsAdmin();
        var job = await db.MikrotikJobs.SingleOrDefaultAsync(j => j.Id == id && j.Kind == MikrotikJobKind.Command && (admin || j.AdminUsername == null), ct)
            ?? throw ApiException.NotFound("Command");
        if (!admin && job.RequestedBy != UdpViews.User(accessor).Id)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.ForbiddenRole, "Only your own commands; an Admin cancels any.");
        }
        if (job.State is not (MikrotikJobState.Queued or MikrotikJobState.Running))
        {
            throw ApiException.Conflict("job_finished", "The command has finished.");
        }
        if (job.State == MikrotikJobState.Running && job.Step > MikrotikJobStep.Run)
        {
            throw ApiException.Conflict("job_finishing", "The command has run: only its export after is left.");
        }
        job.State = MikrotikJobState.Cancelled;
        if (job.StartedAt is null)
        {
            job.FinishedAt = Clock.UtcNow;
            job.Error = "Cancelled before it started.";
        }
        UdpViews.Audit(db, accessor, AuditEvents.MikrotikCommandCancelled, $"command on {job.RouterName} as {job.RunAs} cancelled (job {job.Id})");
        await db.SaveChangesAsync(ct);
        runner.Cancel(job.Id);
        if (!await db.MikrotikJobs.AnyAsync(j => j.BatchId == job.BatchId && (j.State == MikrotikJobState.Queued || j.State == MikrotikJobState.Running), ct))
        {
            vault.Remove(job.BatchId);
        }
    }

    /// <summary>
    /// Replaces a finished job's text, output and error with who redacted it and when (§8.3): for a secret typed
    /// into a command. The backups before and after stay. Audit-logged.
    /// </summary>
    public async Task<MikrotikCommandItem> RedactAsync(Guid id, CancellationToken ct)
    {
        var job = await db.MikrotikJobs.SingleOrDefaultAsync(j => j.Id == id && j.Kind == MikrotikJobKind.Command, ct) ?? throw ApiException.NotFound("Command");
        if (job.State is MikrotikJobState.Queued or MikrotikJobState.Running)
        {
            throw ApiException.Conflict("job_running", "A command is redacted once it has finished.");
        }
        if (job.RedactedAt is null)
        {
            var name = UdpViews.User(accessor).Name ?? "cli";
            var now = Clock.UtcNow;
            job.Command = null;
            job.Output = null;
            job.Error = job.Error is null ? null : "Redacted.";
            job.RedactedAt = now;
            job.RedactedBy = name;
            // The router's error line can quote the text: the run's messages go too.
            foreach (var line in await db.MikrotikJobLog.Where(l => l.JobId == id && (l.Step == MikrotikJobStep.Run || l.Step == MikrotikJobStep.Done)).ToListAsync(ct))
            {
                line.Message = $"Redacted by {name}.";
                line.Output = null;
            }
            UdpViews.Audit(db, accessor, AuditEvents.MikrotikCommandRedacted, $"command on {job.RouterName} as {job.RunAs} redacted (job {job.Id})");
            await db.SaveChangesAsync(ct);
        }
        return Item(job);
    }

    /// <summary>Commands only, and admin runs only for Admins (§8.3).</summary>
    private IQueryable<MikrotikJobEntity> Visible()
    {
        var commands = db.MikrotikJobs.AsNoTracking().Where(j => j.Kind == MikrotikJobKind.Command);
        return IsAdmin() ? commands : commands.Where(j => j.AdminUsername == null);
    }

    /// <summary>A page of jobs without their output (up to 256 KiB each).</summary>
    public static IQueryable<MikrotikJobEntity> WithoutOutput(IQueryable<MikrotikJobEntity> jobs) => jobs.Select(j => new MikrotikJobEntity
    {
        Id = j.Id, BatchId = j.BatchId, Kind = j.Kind, SensorId = j.SensorId, RouterName = j.RouterName, State = j.State, Step = j.Step,
        RequestedBy = j.RequestedBy, RequestedByName = j.RequestedByName, AdminUsername = j.AdminUsername, CreatedAt = j.CreatedAt,
        StartedAt = j.StartedAt, FinishedAt = j.FinishedAt, FromVersion = j.FromVersion, ToVersion = j.ToVersion, Channel = j.Channel,
        Error = j.Error, Command = j.Command, RunAs = j.RunAs, TimeoutSeconds = j.TimeoutSeconds, ExitStatus = j.ExitStatus,
        OutputTruncated = j.OutputTruncated, BackupBeforeId = j.BackupBeforeId, BackupAfterId = j.BackupAfterId, RedactedAt = j.RedactedAt,
        RedactedBy = j.RedactedBy,
    });

    private bool IsAdmin() => accessor.HttpContext is not { } http || http.User.IsInRole(Roles.Admin);

    public static MikrotikCommandItem Item(MikrotikJobEntity j) => new(j.Id, j.BatchId, j.SensorId, j.RouterName, j.State, j.Step, j.RequestedByName,
        Instants.Utc(j.CreatedAt), UdpViews.NullableUtc(j.StartedAt), UdpViews.NullableUtc(j.FinishedAt), j.Command, j.RunAs ?? RouterPoll.UserName,
        j.AdminUsername is not null, j.TimeoutSeconds ?? 60, j.ExitStatus, j.Error, j.OutputTruncated, j.BackupBeforeId, j.BackupAfterId,
        UdpViews.NullableUtc(j.RedactedAt), j.RedactedBy);
}
