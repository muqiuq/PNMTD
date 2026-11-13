using System.Buffers;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PNMTD.Lib.Agent;

/// <summary>A message above the limits (§3.5): closed with 4413.</summary>
public sealed class AgentFrameTooLargeException(string message) : Exception(message);

/// <summary>A message that is not an envelope: closed with 4400.</summary>
public sealed class AgentFrameInvalidException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Frames (docs/windows-sensor-plan.md §3.5): text frames carry the envelope's JSON, binary frames the same JSON
/// gzip-compressed. Reading stops at the limit on the wire, and decompression at the limit times
/// <see cref="AgentProtocol.DecompressionFactor"/>, which also stops a gzip bomb.
/// </summary>
public static class AgentFrames
{
    /// <summary>Above this, a message goes compressed.</summary>
    public const int CompressAbove = 2048;

    public static AgentEnvelope Envelope<T>(string type, T body, string? id = null) =>
        new(type, id ?? AgentSigning.NewRequestId(), DateTimeOffset.UtcNow, AgentJson.ToElement(body));

    public static (byte[] Bytes, WebSocketMessageType Type) Encode(AgentEnvelope envelope, bool? compress = null)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, AgentJson.Options);
        if (compress ?? json.Length > CompressAbove)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(json);
            }
            return (output.ToArray(), WebSocketMessageType.Binary);
        }
        return (json, WebSocketMessageType.Text);
    }

    public static async Task SendAsync(WebSocket socket, AgentEnvelope envelope, SemaphoreSlim gate, CancellationToken ct, bool? compress = null)
    {
        var (bytes, type) = Encode(envelope, compress);
        await gate.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, type, endOfMessage: true, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The next message, or null when the other side closed. Throws on a message above the limit or one that can't be read.</summary>
    public static async Task<AgentEnvelope?> ReceiveAsync(WebSocket socket, int maxBytes, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }
                if (message.Length + result.Count > maxBytes)
                {
                    throw new AgentFrameTooLargeException($"A message above {maxBytes} bytes.");
                }
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var bytes = result.MessageType == WebSocketMessageType.Binary
                ? Decompress(message.ToArray(), (long)maxBytes * AgentProtocol.DecompressionFactor)
                : message.ToArray();
            try
            {
                return JsonSerializer.Deserialize<AgentEnvelope>(bytes, AgentJson.Options) is { T.Length: > 0 and <= 64 } envelope
                    ? envelope
                    : throw new AgentFrameInvalidException("A message without a type.");
            }
            catch (JsonException e)
            {
                throw new AgentFrameInvalidException("A message that is not JSON.", e);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte[] Decompress(byte[] compressed, long limit)
    {
        try
        {
            using var input = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                if (output.Length + read > limit)
                {
                    throw new AgentFrameTooLargeException($"A message above {limit} bytes decompressed.");
                }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException e)
        {
            throw new AgentFrameInvalidException("A binary message that is not gzip.", e);
        }
    }

    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
