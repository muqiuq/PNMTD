using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Udp;
using PNMTD.Models.Db;
using PNMTD.Udp;

namespace PNMTD.Services.Domain;

/// <summary>What the UDP services share: hex in and out, packet fields, the audit log, the endpoint.</summary>
internal static class UdpViews
{
    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    public static string SpacedHex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    public static string CArray(byte[] bytes) => "{" + string.Join(", ", bytes.Select(b => "0x" + b.ToString("X2", CultureInfo.InvariantCulture))) + "}";

    /// <summary>The id as the header carries it: "07", "01 2C".</summary>
    public static string IdBytes(long deviceId)
    {
        var id = (uint)deviceId;
        var length = PacketHeader.MinimalIdLength(id);
        return string.Join(' ', Enumerable.Range(0, length).Select(i => ((byte)(id >> (8 * (length - 1 - i)))).ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Hex as people type it: case-insensitive, with spaces, colons, dashes and 0x allowed. Null when
    /// it is not exactly <paramref name="length"/> bytes, or one byte repeated (00…00, FF…FF).
    /// </summary>
    public static byte[]? ParseSecret(string? text, int length, string field, ApiValidator v, string what)
    {
        if (text is null)
        {
            return null;
        }
        var bytes = ParseHex(text);
        if (bytes is null || bytes.Length != length)
        {
            v.Check(false, field, $"The {what} is {length * 2} hex digits ({length} bytes).");
            return null;
        }
        if (bytes.All(b => b == bytes[0]))
        {
            v.Check(false, field, $"The {what} must not be one byte repeated.");
            return null;
        }
        return bytes;
    }

    public static byte[]? ParseHex(string text)
    {
        var compact = new StringBuilder();
        foreach (var token in text.Split([' ', ':', '-', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            compact.Append(token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token);
        }
        try
        {
            return compact.Length % 2 == 0 ? Convert.FromHexString(compact.ToString()) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static byte[] NewSecret(int length)
    {
        while (true)
        {
            var bytes = RandomNumberGenerator.GetBytes(length);
            if (!bytes.All(b => b == bytes[0]))
            {
                return bytes;
            }
        }
    }

    /// <summary>The fields of a stored datagram; null when it does not parse.</summary>
    public static IReadOnlyList<PacketField>? Fields(byte[]? datagram, byte[]? plaintext, bool? verified)
    {
        if (datagram is null || !PacketCodec.TryParse(datagram, out var packet, out _))
        {
            return null;
        }
        var payload = plaintext ?? (packet.Header.Suite.IsEncrypted() ? null : packet.Body.ToArray());
        return PacketFields.Describe(packet, payload, verified);
    }

    /// <summary>The plaintext of a COMMAND's payload for its fields: CMD_ID and the command bytes.</summary>
    public static byte[] CommandPlaintext(short cmdId, byte[] command) => Payloads.Command((byte)cmdId, command);

    public static UdpLogItem Log(UdpCommandLogEntity l)
    {
        var plaintext = l.Kind switch
        {
            Models.Enums.UdpLogKind.Sent or Models.Enums.UdpLogKind.Resent when l.CmdId is { } cmd && l.Payload is { } p => CommandPlaintext(cmd, p),
            _ => l.Payload,
        };
        return new UdpLogItem(l.Id, Instants.Utc(l.Created), l.Kind, l.Direction, l.CommandId, l.OtaJobId, l.SensorId, l.DeviceId, l.NodeName, l.CmdId,
            l.Attempt, l.Endpoint, l.Suite, l.Fcnt, l.Datagram, l.Payload, l.ResultStatus, l.UserName, l.Note,
            Fields(l.Datagram, plaintext, l.Datagram is null ? null : true));
    }

    public static UdpCommandItem Command(UdpCommandEntity c) => new(c.Id, c.SensorId, c.DeviceId, c.NodeName, c.CmdId, c.Payload, c.State, c.Delivery,
        c.Suite, c.Attempts, c.MaxAttempts, Instants.Utc(c.Created), c.CreatedByName, NullableUtc(c.ExpiresAt), NullableUtc(c.LastSentAt),
        NullableUtc(c.CompletedAt), c.ResultStatus, c.ResultStatus is { } s ? Payloads.CommandStatus((byte)s) : null, c.Result, c.Note);

    public static DateTime? NullableUtc(DateTime? value) => value is { } v ? Clock.AsUtc(v) : null;

    public static IReadOnlyList<UdpReasonCount> Reasons(IReadOnlyDictionary<Models.Enums.UdpRejectReason, long> counts) =>
        counts.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => new UdpReasonCount(kv.Key, kv.Value)).ToList();

    public static UdpNumbers Numbers(UdpNodeNumbers n) => new(n.Accepted, n.Rejected, n.BytesIn, n.BytesOut, n.Loss, Reasons(n.RejectedByReason));

    /// <summary>An entry in the audit log (auth_log), as the user changes do: who, from where, what. Never a secret.</summary>
    public static void Audit(PnmtdDbContext db, IHttpContextAccessor accessor, string eventType, string detail)
    {
        var http = accessor.HttpContext;
        var audit = http is null ? AuditContext.Cli : AuditContext.From(http);
        db.AuthLog.Add(new AuthLogEntity
        {
            Id = Guid.NewGuid(),
            CreatedAt = Clock.UtcNow,
            UserId = http?.User.UserId(),
            Username = http?.User.Identity?.Name ?? "",
            EventType = eventType,
            IpAddress = audit.IpAddress,
            UserAgent = string.IsNullOrEmpty(audit.UserAgent) ? null : audit.UserAgent,
            Detail = audit.Origin is null ? detail : $"{audit.Origin}: {detail}",
        });
    }

    public static (Guid? Id, string? Name) User(IHttpContextAccessor accessor) =>
        (accessor.HttpContext?.User.UserId(), accessor.HttpContext?.User.Identity?.Name);
}

/// <summary>Where devices send to, as the UI shows it: Udp:PublicEndpoint, else PublicBaseUrl's (or the request's) host and the port.</summary>
public sealed class UdpEndpoint(IOptionsMonitor<UdpOptions> options, UdpListenerState listener, PublicUrls urls)
{
    public int Port => options.CurrentValue.Port is > 0 and var p ? p : listener.Port ?? 5028;

    public string Value
    {
        get
        {
            if (Text.Trimmed(options.CurrentValue.PublicEndpoint) is { } configured)
            {
                return configured;
            }
            var host = Uri.TryCreate(urls.Base, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : "localhost";
            return $"{(host.Contains(':') ? $"[{host}]" : host)}:{Port}";
        }
    }

    public string Host => Value.LastIndexOf(':') is var i and > 0 ? Value[..i].Trim('[', ']') : Value;
}
