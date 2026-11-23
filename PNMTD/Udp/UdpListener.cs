using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PNMTD.Lib.Helper;
using PNMTD.Models.Enums;

namespace PNMTD.Udp;

/// <summary>Whether the listener runs, where, and what failed: for the admin page, System health and info.sh.</summary>
public sealed class UdpListenerState
{
    private Func<int>? queueDepth;

    public bool Enabled { get; private set; }

    public bool Listening { get; private set; }

    /// <summary>The bound port (with Udp:Port 0, the one the system chose).</summary>
    public int? Port { get; private set; }

    public string? Error { get; private set; }

    public DateTime? Since { get; private set; }

    public int QueueDepth => queueDepth?.Invoke() ?? 0;

    internal void Off()
    {
        Enabled = false;
        Listening = false;
    }

    internal void Failed(string error)
    {
        Enabled = true;
        Listening = false;
        Error = error;
    }

    internal void Bound(int port, Func<int> depth)
    {
        Enabled = true;
        // The port first: a reader that sees Listening also sees the port (the tests wait for Listening, then read Port).
        Port = port;
        Listening = true;
        Error = null;
        Since = Clock.UtcNow;
        queueDepth = depth;
    }

    internal void Stopped() => Listening = false;
}

/// <summary>
/// The UDP socket (docs/udp-iot-plan.md §4.4): dual-stack, one receive loop, bounded queues per
/// worker. A node's datagrams always go to the same worker, so they are processed in order;
/// different nodes run in parallel. Responses leave from the same socket, to the sender's address.
/// If the port cannot be bound, the error is logged and shown; the app keeps running.
/// </summary>
public sealed class UdpListener(UdpPacketProcessor processor, UdpListenerState state, UdpStats stats,
    IOptions<UdpOptions> options, ILogger<UdpListener> logger) : BackgroundService
{
    private sealed record Datagram(byte[] Bytes, IPEndPoint Remote);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            state.Off();
            logger.LogInformation("The UDP listener is off (Udp:Enabled=false)");
            return;
        }
        Socket socket;
        try
        {
            socket = Bind(o.Port);
        }
        catch (SocketException ex)
        {
            state.Failed($"Port {o.Port}/udp cannot be bound: {ex.Message}");
            logger.LogError(ex, "The UDP listener cannot bind port {Port}", o.Port);
            return;
        }

        var workers = Enumerable.Range(0, Math.Max(1, o.Workers))
            .Select(_ => Channel.CreateBounded<Datagram>(new BoundedChannelOptions(Math.Max(16, o.QueueLength / Math.Max(1, o.Workers)))
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = true,
            }))
            .ToArray();
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        state.Bound(port, () => workers.Sum(w => w.Reader.Count));
        logger.LogInformation("UDP nodes: listening on port {Port}/udp", port);

        var tasks = workers.Select(w => Task.Run(() => WorkAsync(w.Reader, socket, stoppingToken), CancellationToken.None)).ToArray();
        var buffer = new byte[2048];
        var any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
                {
                    // An ICMP "port unreachable" for an earlier answer, or a datagram larger than the buffer.
                    continue;
                }
                var bytes = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
                var remote = (IPEndPoint)received.RemoteEndPoint;
                var worker = workers[(int)(Partition(bytes) % (uint)workers.Length)];
                if (!worker.Writer.TryWrite(new Datagram(bytes, remote)))
                {
                    stats.Datagram(bytes.Length);
                    stats.Reject(UdpRejectReason.QueueFull, null, remote, bytes);
                }
            }
        }
        finally
        {
            foreach (var w in workers)
            {
                w.Writer.TryComplete();
            }
            await Task.WhenAll(tasks);
            await processor.FlushAllAsync(CancellationToken.None);
            socket.Dispose();
            state.Stopped();
        }
    }

    private static Socket Bind(int port)
    {
        try
        {
            var dual = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            dual.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return dual;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
        {
            // A host without IPv6.
            var v4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            v4.Bind(new IPEndPoint(IPAddress.Any, port));
            return v4;
        }
    }

    /// <summary>The device id when the header can be read, so a node always lands on the same worker.</summary>
    private static uint Partition(byte[] bytes)
    {
        if (bytes.Length < 3)
        {
            return 0;
        }
        var length = (bytes[0] & 0x3) + 1;
        uint id = 0;
        for (var i = 0; i < length && 2 + i < bytes.Length; i++)
        {
            id = (id << 8) | bytes[2 + i];
        }
        return id;
    }

    private async Task WorkAsync(ChannelReader<Datagram> reader, Socket socket, CancellationToken stoppingToken)
    {
        await foreach (var datagram in reader.ReadAllAsync(CancellationToken.None))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                continue;
            }
            try
            {
                var response = await processor.ProcessAsync(datagram.Bytes, datagram.Remote, stoppingToken);
                if (response is not null)
                {
                    await socket.SendToAsync(response, SocketFlags.None, datagram.Remote, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "UDP datagram from {Remote} failed", Endpoints.Format(datagram.Remote));
            }
        }
    }
}
