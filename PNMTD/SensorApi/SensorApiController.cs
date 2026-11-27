using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PNMTD.Data;
using PNMTD.Exceptions;
using PNMTD.Helper;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Models.Requests;
using PNMTD.Models.Responses;
using PNMTD.Services.ApiOutputFormat;

namespace PNMTD.SensorApi;

/// <summary>
/// The sensor API: <c>/event</c> and <c>/read</c>, frozen at v0.12.1 (docs/webui-migration-plan.md §3.3).
/// Hundreds of applications report here, many of which can never be updated. The golden contract
/// (PNMTD.Contract.Tests) defines what "unchanged" means: routes, status codes, headers and body
/// bytes, quirks included. Keep it isolated from /api/v1: MVC's default JSON settings, no shared DTOs,
/// and none of the UI's middleware (see <see cref="SensorPaths"/>).
/// </summary>
[ApiController]
[AllowAnonymous]
public class SensorApiController(PnmtdDbContext db) : ControllerBase
{
    [HttpGet("read/{secretToken}/{option?}", Name = "Read last event value")]
    public object ReadEvent(string secretToken, string? option)
    {
        var sensorR = db.Sensors.Where(s => s.SecretReadToken == secretToken && s.Enabled).SingleOrDefault();

        if (sensorR == null)
        {
            return NotFound();
        }

        EventEntity? lastEvent;

        if (sensorR.Type == SensorType.ALL_WITHIN_TIMESPAN || sensorR.Type == SensorType.ONE_WITHIN_TIMESPAN)
        {
            lastEvent = db.Events
                .Where(e => e.SensorId == sensorR.Id &&
                        (e.Code == PNMTStatusCodes.ALL_WITHIN_TIMESPAN_FAILED ||
                         e.Code == PNMTStatusCodes.ALL_WITHIN_TIMESPAN_OK ||
                         e.Code == PNMTStatusCodes.ONE_WITHIN_TIMESPAN_FAILED ||
                         e.Code == PNMTStatusCodes.ONE_WITHIN_TIMESPAN_OK))
                .OrderByDescending(e => e.Created).FirstOrDefault();
        }
        else
        {
            lastEvent = db.Events.Where(e => e.SensorId == sensorR.Id).OrderByDescending(e => e.Created).FirstOrDefault();
        }

        bool? lastEventSuccess = null;
        string? lastEventMessage = null;
        if (lastEvent != null)
        {
            lastEventSuccess = lastEvent.IsSuccess;
            lastEventMessage = lastEvent.Message;
        }

        return Ok(ApiOutputFormatter.FormatOutput(new List<KeyValuePair<string, object>>()
        {
            new("lastEventSuccess", lastEventSuccess!),
            new("lastEventMessage", lastEventMessage!),
        }, option!));
    }

    [HttpGet("event/{secretToken}/{code}/{message?}", Name = "Submit Event (Get)")]
    public object SubmitEvent(string secretToken, int code, string? message)
    {
        var sensorR = db.Sensors.Where(s => s.SecretWriteToken == secretToken && s.Enabled);

        if (!sensorR.Any())
        {
            return NotFound();
        }

        // Single() throws for a duplicate token: a 500, as in v0.12 (§3.3).
        var sensor = sensorR.Single();
        if (sensor.Type == SensorType.LICENSE)
        {
            return LicenseSensor();
        }

        // A $ marks a message that is Base64 of UTF-8 text. Culture-sensitive StartsWith, as in v0.12.
        if (message != null && message.StartsWith("$"))
        {
            message = Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(1)));
        }

        using var transaction = BeginSensorTransaction(sensor.Id);

        var lastEvent = GetLastEvent(sensor.Id);
        var preLastEvent = GetPreLastEvent(sensor.Id);

        if (HasTooManyRequests(lastEvent, out var result)) return result!;

        var eventEntity = CreateOrUpdateEvent(lastEvent, preLastEvent, code, message, sensor);

        TrimEvents(sensor.Id);

        db.SaveChanges();
        transaction.Commit();

        return new DefaultResponse() { Success = true, Data = eventEntity.Id };
    }

    [HttpPost("event/{secretToken}/{code}", Name = "Submit Event (Post)")]
    public async Task<object> PostEvent(string secretToken, int code)
    {
        var sensorR = db.Sensors.Where(s => s.SecretWriteToken == secretToken && s.Enabled);

        if (!sensorR.Any())
        {
            return NotFound();
        }

        var sensor = sensorR.Single();
        if (sensor.Type == SensorType.LICENSE)
        {
            return LicenseSensor();
        }

        using var transaction = BeginSensorTransaction(sensor.Id);

        var lastEvent = GetLastEvent(sensor.Id);
        var preLastEvent = GetPreLastEvent(sensor.Id);

        if (HasTooManyRequests(lastEvent, out var result)) return result!;

        var message = (await Request.GetRequestBody()).Trim();

        if (message.StartsWith("$"))
        {
            message = Encoding.UTF8.GetString(Convert.FromBase64String(message.Substring(1)));
        }

        EventEntity eventEntity = null!;

        if (sensor.Type == SensorType.ENCAPSULADED)
        {
            // Case-sensitive Name/Code/Message, as v0.12's default deserializer options.
            var encapsulatedEvents = JsonSerializer.Deserialize<IEnumerable<EncapsulatedEvent>>(message)!;
            foreach (var encapsulatedEvent in encapsulatedEvents)
            {
                // The database only, not this request's unsaved siblings: a name repeated within one
                // array gets a second sibling, and the next report with that name fails (v0.12).
                var siblingSensor = db.Sensors.SingleOrDefault(s => s.OlderSiblingId == sensor.Id && s.Name == encapsulatedEvent.Name);
                if (siblingSensor == null)
                {
                    if (db.Sensors.Count(s => s.OlderSiblingId == sensor.Id) > GlobalConfiguration.MAXIMUM_NUM_OF_YOUNGER_SIBLINGS_SENSORS)
                    {
                        throw new MaximumNumberOfSensorSiblingsExceeded();
                    }
                    siblingSensor = new SensorEntity()
                    {
                        Id = Guid.NewGuid(),
                        Created = Clock.UtcNow,
                        OlderSibling = sensor,
                        OlderSiblingId = sensor.Id,
                        Enabled = true,
                        Name = encapsulatedEvent.Name,
                        ParentId = sensor.ParentId,
                        Type = SensorType.SIMPLE,
                        Interval = sensor.Interval,
                        GracePeriod = sensor.GracePeriod,
                        Parameters = "",
                    };
                    siblingSensor.SetNewSecretToken();
                    db.Sensors.Add(siblingSensor);
                }

                eventEntity = CreateNewEntity(encapsulatedEvent.Code, encapsulatedEvent.Message, siblingSensor);
                TrimEvents(siblingSensor.Id);
            }
        }
        else
        {
            eventEntity = CreateOrUpdateEvent(lastEvent, preLastEvent, code, message, sensor);
        }

        TrimEvents(sensor.Id);

        db.SaveChanges();
        transaction.Commit();

        // An empty ENCAPSULADED array throws here: a 500, as in v0.12.
        return new DefaultResponse() { Success = true, Data = eventEntity.Id };
    }

    /// <summary>
    /// Submissions for one sensor, one after the other: the rate limit, the dedupe rule and the
    /// sibling sensors of ENCAPSULADED read before they write. SQLite serialised every write;
    /// PostgreSQL does not, so a transaction-scoped advisory lock per sensor does it (§3.4).
    /// Nothing in the response changes; a failure rolls back as before.
    /// </summary>
    private IDbContextTransaction BeginSensorTransaction(Guid sensorId)
    {
        var transaction = db.Database.BeginTransaction();
        db.Database.ExecuteSqlRaw("SELECT pg_advisory_xact_lock({0})", BitConverter.ToInt64(sensorId.ToByteArray(), 0));
        return transaction;
    }

    private EventEntity CreateNewEntity(int code, string? message, SensorEntity sensor)
    {
        var eventEntity = new EventEntity()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Message = message,
            Created = Clock.UtcNow,
            SensorId = sensor.Id,
            Sensor = sensor,
            Source = RemoteSource(),
        };
        if (sensor.Type == SensorType.VALUECHECK || sensor.Type == SensorType.HEARTBEAT_VALUECHECK)
        {
            SensorTypeActionHelper.AdjustCodeForValueCheckSensorTypeInEvent(eventEntity, sensor);
        }

        db.Events.Add(eventEntity);

        return eventEntity;
    }

    /// <summary>
    /// Dedupe: when the last two events and this one share code, message and source address, only
    /// the last event's time moves.
    /// </summary>
    private EventEntity CreateOrUpdateEvent(EventEntity? lastEvent, EventEntity? preLastEvent, int newCode, string? message, SensorEntity sensor)
    {
        var source = RemoteSource();
        if (preLastEvent != null &&
            lastEvent != null &&
            preLastEvent.Code == lastEvent.Code &&
            preLastEvent.Message == lastEvent.Message &&
            RemoteAddressHelper.StripPortFromRemoteAddressIfAny(lastEvent.Source) == RemoteAddressHelper.StripPortFromRemoteAddressIfAny(preLastEvent.Source) &&
            lastEvent.Code == newCode && lastEvent.Message == message &&
            RemoteAddressHelper.StripPortFromRemoteAddressIfAny(lastEvent.Source) == RemoteAddressHelper.StripPortFromRemoteAddressIfAny(source))
        {
            lastEvent.Created = Clock.UtcNow;
            return lastEvent;
        }
        return CreateNewEntity(newCode, message, sensor);
    }

    private EventEntity? GetLastEvent(Guid sensorId) =>
        db.Events.Where(e => e.SensorId == sensorId).OrderByDescending(e => e.Created).FirstOrDefault();

    private EventEntity? GetPreLastEvent(Guid sensorId) =>
        db.Events.Where(e => e.SensorId == sensorId).OrderByDescending(e => e.Created).Skip(1).FirstOrDefault();

    /// <summary>At most 2000 stored events per sensor (counted before this request's event is saved).</summary>
    private void TrimEvents(Guid sensorId)
    {
        var surplus = db.Events.Where(e => e.SensorId == sensorId)
            .OrderByDescending(e => e.Created)
            .Skip(GlobalConfiguration.MAXIMUM_NUM_OF_EVENTS_PER_SENSOR)
            .ToList();
        db.Events.RemoveRange(surplus);
    }

    private bool HasTooManyRequests(EventEntity? lastEvent, out ObjectResult? result)
    {
        if (lastEvent != null
            && (Clock.UtcNow - lastEvent.Created) < TimeSpan.FromSeconds(GlobalConfiguration.MINIMUM_TIME_DIFFERENCE_BETWEEN_EVENTS_IN_SECONDS)
            && !Global.IsDevelopment)
        {
            // The typo is part of the contract.
            result = Problem("Too many requesteds", statusCode: 425);
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>The client as "ip:port", after the forwarded headers (the Proxy setting).</summary>
    private string RemoteSource()
    {
        var address = HttpContext.Connection.RemoteIpAddress;
        return address == null ? "0.0.0.0" : $"{address}:{HttpContext.Connection.RemotePort}";
    }

    /// <summary>
    /// An item sensor's events are the licence evaluation's (docs/licences-plan.md §5.3): one posted with its write token would stand for weeks,
    /// since the verdict writes only when it changes.
    /// </summary>
    private ObjectResult LicenseSensor() =>
        StatusCode(StatusCodes.Status409Conflict, new DefaultResponse { Success = false, Message = "license_sensor: this sensor is a licence's or service's; PNMT writes its events." });

}
