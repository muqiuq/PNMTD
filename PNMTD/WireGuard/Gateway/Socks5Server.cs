using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace PNMTD.WireGuard.Gateway;

/// <summary>
/// The gateway's SOCKS5 server (docs/mikrotik-plan.md §3.4): how PNMTD's SSH connections get into the
/// tunnel without the app having a capability. CONNECT only (RFC 1928), with a username and password
/// (RFC 1929), IPv4 targets only, and only to an allowed router address and its SSH port. Connections that
/// arrive through the tunnel itself are refused.
/// </summary>
public sealed class Socks5Server(int port, string username, Func<string> password, Func<IPAddress, bool> fromTunnel, ILogger logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, int> allowed = new();
    private TcpListener? listener;
    private Task? accepting;
    private readonly CancellationTokenSource stopping = new();

    /// <summary>The router addresses PNMTD may reach, each with its SSH port. Replaced at every sync.</summary>
    public void SetAllowed(IReadOnlyDictionary<string, int> targets)
    {
        foreach (var key in allowed.Keys.Except(targets.Keys))
        {
            allowed.TryRemove(key, out _);
        }
        foreach (var (address, sshPort) in targets)
        {
            allowed[address] = sshPort;
        }
    }

    public int Port => ((IPEndPoint?)listener?.LocalEndpoint)?.Port ?? port;

    public void Start()
    {
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        accepting = AcceptAsync(stopping.Token);
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "SOCKS: accept failed");
                continue;
            }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        if (fromTunnel(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            logger.LogWarning("SOCKS: refused {Remote}, which came through the tunnel", remote);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var stream = client.GetStream();
        try
        {
            var target = await NegotiateAsync(stream, timeout.Token);
            if (target is null)
            {
                return;
            }
            using var upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(target, timeout.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                await ReplyAsync(stream, ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ? (byte)5 : (byte)4, ct);
                return;
            }
            await ReplyAsync(stream, 0, ct);
            var other = upstream.GetStream();
            // Both directions until either side closes.
            var a = stream.CopyToAsync(other, ct);
            var b = other.CopyToAsync(stream, ct);
            await Task.WhenAny(a, b);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The greeting, the login and the request; the target when everything is allowed, else null (after the error reply).</summary>
    private async Task<IPEndPoint?> NegotiateAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = await ReadAsync(stream, 2, ct);
        if (head[0] != 5)
        {
            return null;
        }
        var methods = await ReadAsync(stream, head[1], ct);
        if (!methods.Contains((byte)2))
        {
            await stream.WriteAsync(new byte[] { 5, 0xFF }, ct);
            return null;
        }
        await stream.WriteAsync(new byte[] { 5, 2 }, ct);

        var version = await ReadAsync(stream, 2, ct);
        var user = await ReadAsync(stream, version[1], ct);
        var passwordLength = await ReadAsync(stream, 1, ct);
        var pass = await ReadAsync(stream, passwordLength[0], ct);
        var ok = version[0] == 1
            && CryptographicOperations.FixedTimeEquals(user, Encoding.UTF8.GetBytes(username))
            && CryptographicOperations.FixedTimeEquals(pass, Encoding.UTF8.GetBytes(password()));
        await stream.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 }, ct);
        if (!ok)
        {
            logger.LogWarning("SOCKS: wrong username or password");
            return null;
        }

        var request = await ReadAsync(stream, 4, ct);
        if (request[0] != 5 || request[1] != 1)
        {
            await ReplyAsync(stream, 7, ct);
            return null;
        }
        if (request[3] != 1)
        {
            // Router tunnel addresses are IPv4; names and IPv6 are refused.
            await ReplyAsync(stream, 8, ct);
            return null;
        }
        var address = new IPAddress(await ReadAsync(stream, 4, ct));
        var targetPort = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(stream, 2, ct));
        if (!allowed.TryGetValue(address.ToString(), out var sshPort) || sshPort != targetPort)
        {
            logger.LogWarning("SOCKS: {Address}:{Port} is not a router's SSH port", address, targetPort);
            await ReplyAsync(stream, 2, ct);
            return null;
        }
        return new IPEndPoint(address, targetPort);
    }

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int length, CancellationToken ct)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private static Task ReplyAsync(NetworkStream stream, byte code, CancellationToken ct) =>
        stream.WriteAsync(new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 }, ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener?.Stop();
        if (accepting is not null)
        {
            try
            {
                await accepting;
            }
            catch (OperationCanceledException)
            {
            }
        }
        stopping.Dispose();
    }
}
