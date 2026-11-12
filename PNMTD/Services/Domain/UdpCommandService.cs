using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Udp;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;
using PNMTD.Udp;

namespace PNMTD.Services.Domain;

/// <summary>
/// Commands (docs/udp-iot-plan.md §3.10, §4.6): queued here, delivered by the listener with the
/// node's next uplink. Every step goes to the permanent command log; nothing here deletes anything.
/// </summary>
public sealed class UdpCommandService(PnmtdDbContext db, IOptionsMonitor<UdpOptions> options, IHttpContextAccessor accessor)
{
    public async Task<Paged<UdpCommandItem>> ListAsync(Guid sensorId, UdpCommandQuery query, CancellationToken ct)
    {
        var (_, descending) = Paging.Sort(query.Sort, "-created", "created");
        var commands = db.UdpCommands.AsNoTracking().Where(c => c.SensorId == sensorId);
        if (query.State is { } state)
        {
            commands = commands.Where(c => c.State == state);
        }
        commands = descending ? commands.OrderByDescending(c => c.Created) : commands.OrderBy(c => c.Created);
        var page = await Paging.PageAsync(commands, query, ct);
        return new Paged<UdpCommandItem>(page.Items.Select(UdpViews.Command).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<IReadOnlyList<UdpLogItem>> CommandLogAsync(Guid sensorId, Guid commandId, CancellationToken ct)
    {
        if (!await db.UdpCommands.AnyAsync(c => c.Id == commandId && c.SensorId == sensorId, ct))
        {
            throw ApiException.NotFound("Command");
        }
        var rows = await db.UdpCommandLog.AsNoTracking().Where(l => l.CommandId == commandId).OrderBy(l => l.Created).ThenBy(l => l.Id).ToListAsync(ct);
        return rows.Select(UdpViews.Log).ToList();
    }

    /// <summary>The permanent log of every node, deleted nodes too: newest first.</summary>
    public async Task<Paged<UdpLogItem>> LogAsync(UdpLogQuery query, CancellationToken ct)
    {
        new ApiValidator()
            .Check(query.From is null || query.To is null || query.From <= query.To, "from", "from must not be after to.")
            .ThrowIfInvalid();
        var log = db.UdpCommandLog.AsNoTracking();
        if (query.SensorId is { } sensorId)
        {
            log = log.Where(l => l.SensorId == sensorId);
        }
        if (query.DeviceId is { } deviceId)
        {
            log = log.Where(l => l.DeviceId == deviceId);
        }
        if (query.Kind is { } kind)
        {
            log = log.Where(l => l.Kind == kind);
        }
        if (query.From is { } from)
        {
            var f = Clock.AsUtc(from);
            log = log.Where(l => l.Created >= f);
        }
        if (query.To is { } to)
        {
            var t = Clock.AsUtc(to);
            log = log.Where(l => l.Created <= t);
        }
        if (Text.Pattern(query.Search) is { } pattern)
        {
            log = log.Where(l => EF.Functions.ILike(l.NodeName, pattern, @"\") || (l.Note != null && EF.Functions.ILike(l.Note, pattern, @"\")));
        }
        var page = await Paging.PageAsync(log.OrderByDescending(l => l.Created).ThenByDescending(l => l.Id), query, ct);
        return new Paged<UdpLogItem>(page.Items.Select(UdpViews.Log).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<UdpCommandCreated> CreateAsync(Guid sensorId, UdpCommandRequest request, CancellationToken ct)
    {
        var o = options.CurrentValue.Commands;
        var node = await db.UdpNodes.Include(n => n.Sensor).SingleOrDefaultAsync(n => n.SensorId == sensorId, ct) ?? throw ApiException.NotFound("UDP node");
        var v = new ApiValidator();
        v.Check(request.Payload is not null, "payload", "The command's bytes (Base64), empty allowed.");
        v.Check(request.Payload is null || request.Payload.Length <= o.MaxBytes, "payload", $"At most {o.MaxBytes} bytes.");
        v.Check(Enum.IsDefined(request.Delivery), "delivery", "ResultRequired or FireAndForget.");
        v.Check(request.Suite is null || (Enum.IsDefined(request.Suite.Value) && request.Suite.Value.BelongsTo(node.KeyFamily)), "suite",
            $"NONE, TOKEN32 or a suite of {node.KeyFamily}.");
        v.Check(request.ExpiresInHours is null or (>= 0 and <= 8760), "expiresInHours", "Between 0 (never) and 8760 hours.");
        v.Check(request.MaxAttempts is null or (>= 1 and <= 100), "maxAttempts", "Between 1 and 100.");
        v.ThrowIfInvalid();

        var open = await db.UdpCommands.Where(c => c.SensorId == sensorId && (c.State == UdpCommandState.Queued || c.State == UdpCommandState.Sent))
            .Select(c => c.CmdId).ToListAsync(ct);
        if (open.Count >= o.MaxOpenPerNode)
        {
            throw ApiException.Conflict("too_many_commands", $"The node has {open.Count} open commands already; at most {o.MaxOpenPerNode}.");
        }
        // CMD_ID: 8 bits per node, increasing, never one that an open command uses (§3.10).
        var cmdId = node.NextCmdId;
        while (open.Contains(cmdId))
        {
            cmdId = (short)((cmdId + 1) % 256);
        }
        node.NextCmdId = (short)((cmdId + 1) % 256);

        var now = Clock.UtcNow;
        var (userId, userName) = UdpViews.User(accessor);
        var hours = request.ExpiresInHours ?? o.DefaultExpiryHours;
        var command = new UdpCommandEntity
        {
            Id = Guid.NewGuid(),
            SensorId = sensorId,
            DeviceId = node.DeviceId,
            NodeName = node.Sensor.Name,
            CmdId = cmdId,
            Payload = request.Payload!,
            State = UdpCommandState.Queued,
            Delivery = request.Delivery,
            Suite = request.Suite,
            MaxAttempts = (short)(request.Delivery == UdpCommandDelivery.FireAndForget ? 1 : request.MaxAttempts ?? o.DefaultMaxAttempts),
            Created = now,
            CreatedBy = userId,
            CreatedByName = userName,
            ExpiresAt = hours == 0 ? null : now.AddHours(hours),
        };
        db.UdpCommands.Add(command);
        db.UdpCommandLog.Add(new UdpCommandLogEntity
        {
            Created = now,
            CommandId = command.Id,
            SensorId = sensorId,
            DeviceId = node.DeviceId,
            NodeName = node.Sensor.Name,
            Kind = UdpLogKind.Queued,
            CmdId = cmdId,
            Payload = command.Payload,
            Suite = command.Suite,
            UserId = userId,
            UserName = userName,
            Note = $"{command.Delivery}, {(command.ExpiresAt is null ? "no expiry" : $"expires in {hours} h")}, {command.Payload.Length} bytes",
        });
        await db.SaveChangesAsync(ct);
        return new UdpCommandCreated(UdpViews.Command(command), Warnings(node, command));
    }

    private static List<string> Warnings(UdpNodeEntity node, UdpCommandEntity command)
    {
        var warnings = new List<string>();
        var allowed = SecuritySuites.FromMask(node.AllowedSuites);
        var suite = command.Suite ?? node.ResponseSuite;
        if (suite is { } s ? !s.IsKeyed() : allowed.Any(a => !a.IsKeyed()))
        {
            warnings.Add(suite is null
                ? "The node may report without a signature: the command would then go out unsigned or with only a token, and anyone who sees a packet can forge it."
                : $"{suite.Value.Name()} is not signed: anyone who sees a packet can forge this command.");
        }
        if (suite is { } k && k.IsKeyed() && node.SessionMode == UdpSessionMode.JoinRequired && node.SessionKey is null && node.PendingSessionKey is null)
        {
            warnings.Add("The node has no session yet: the command waits until it has joined.");
        }
        var worst = 8 + 1 + command.Payload.Length + 8;
        if (worst > 100)
        {
            warnings.Add($"Up to {worst} bytes on the wire: under 64 bytes is recommended for NB-IoT.");
        }
        return warnings;
    }

    public async Task CancelAsync(Guid sensorId, Guid commandId, CancellationToken ct)
    {
        var command = await db.UdpCommands.SingleOrDefaultAsync(c => c.Id == commandId && c.SensorId == sensorId, ct) ?? throw ApiException.NotFound("Command");
        if (!command.State.IsOpen())
        {
            throw ApiException.Conflict("not_open", $"The command is {command.State} already.");
        }
        var now = Clock.UtcNow;
        var (userId, userName) = UdpViews.User(accessor);
        command.State = UdpCommandState.Cancelled;
        command.CompletedAt = now;
        db.UdpCommandLog.Add(new UdpCommandLogEntity
        {
            Created = now,
            CommandId = command.Id,
            SensorId = sensorId,
            DeviceId = command.DeviceId,
            NodeName = command.NodeName,
            Kind = UdpLogKind.Cancelled,
            CmdId = command.CmdId,
            UserId = userId,
            UserName = userName,
            Note = command.Attempts > 0 ? $"after {command.Attempts} attempts: no further sends" : "before it was sent",
        });
        await db.SaveChangesAsync(ct);
    }
}
