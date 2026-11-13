using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;
using Renci.SshNet.Common;

namespace PNMTD.Mikrotik;

/// <summary>Where and how to log in (docs/mikrotik-plan.md §4.5).</summary>
public sealed record RouterTarget(
    Guid SensorId,
    string Host,
    int Port,
    string Username,
    RouterCredentials Credentials,
    RouterHostKey? PinnedHostKey,
    TimeSpan ConnectTimeout,
    RouterProxy? Proxy = null);

/// <summary>PNMTD's keys (the <c>pnmt</c> user), or an admin's password for one job (§7.2).</summary>
public sealed record RouterCredentials(IReadOnlyList<string> PrivateKeysPem, string? Password = null)
{
    public static RouterCredentials Keys(IReadOnlyList<string> pems) => new(pems);

    public static RouterCredentials ForPassword(string password) => new([], password);

    /// <summary>Never the password.</summary>
    public override string ToString() => Password is null ? $"{PrivateKeysPem.Count} key(s)" : "a password";
}

/// <summary>The gateway's SOCKS5 server, for the WireGuard modes (§3.4).</summary>
public sealed record RouterProxy(string Host, int Port, string Username, string Password)
{
    public override string ToString() => $"socks5://{Host}:{Port}";
}

public sealed record RouterHostKey(string Type, string Fingerprint)
{
    public override string ToString() => $"{Type} SHA256:{Fingerprint}";
}

/// <summary>A command's outcome: the output (cut at the limit), the exit status, and whether it timed out.</summary>
public sealed record RouterExecResult(string Output, string Error, int? ExitStatus, bool TimedOut, bool Truncated, TimeSpan Duration)
{
    public bool Succeeded => !TimedOut && ExitStatus is null or 0;
}

public enum RouterFailureKind
{
    /// <summary>440: no connection (timeout, refused, name not found, the proxy).</summary>
    Unreachable,

    /// <summary>441: the login was refused.</summary>
    AuthenticationFailed,

    /// <summary>441: the router presented another host key than the pinned one.</summary>
    HostKeyChanged,

    /// <summary>440: the session broke or the router answered something unexpected.</summary>
    Protocol,
}

public sealed class RouterSessionException(RouterFailureKind kind, string message, RouterHostKey? presentedHostKey = null, Exception? inner = null)
    : Exception(message, inner)
{
    public RouterFailureKind Kind { get; } = kind;

    public RouterHostKey? PresentedHostKey { get; } = presentedHostKey;
}

/// <summary>An SSH session to one router. Tests replace it with recorded output (§14).</summary>
public interface IRouterSession : IAsyncDisposable
{
    RouterHostKey HostKey { get; }

    Task<RouterExecResult> ExecAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken ct);
}

public interface IRouterConnector
{
    /// <summary>Connects and logs in; throws <see cref="RouterSessionException"/>.</summary>
    Task<IRouterSession> ConnectAsync(RouterTarget target, CancellationToken ct);
}

/// <summary>SSH.NET: key or password login, the host key check, exec with a timeout and an output limit.</summary>
public sealed class SshRouterConnector : IRouterConnector
{
    public async Task<IRouterSession> ConnectAsync(RouterTarget target, CancellationToken ct)
    {
        var methods = new List<AuthenticationMethod>();
        var keyFiles = new List<PrivateKeyFile>();
        try
        {
            if (target.Credentials.PrivateKeysPem.Count > 0)
            {
                foreach (var pem in target.Credentials.PrivateKeysPem)
                {
                    keyFiles.Add(new PrivateKeyFile(new MemoryStream(Encoding.ASCII.GetBytes(pem))));
                }
                methods.Add(new PrivateKeyAuthenticationMethod(target.Username, [.. keyFiles]));
            }
            if (target.Credentials.Password is { } password)
            {
                methods.Add(new PasswordAuthenticationMethod(target.Username, password));
            }
            var info = target.Proxy is { } proxy
                ? new ConnectionInfo(target.Host, target.Port, target.Username, ProxyTypes.Socks5, proxy.Host, proxy.Port, proxy.Username, proxy.Password, [.. methods])
                : new ConnectionInfo(target.Host, target.Port, target.Username, [.. methods]);
            info.Timeout = target.ConnectTimeout;
            info.Encoding = Encoding.UTF8;

            var client = new SshClient(info);
            RouterHostKey? presented = null;
            var refused = false;
            client.HostKeyReceived += (_, e) =>
            {
                presented = new RouterHostKey(e.HostKeyName, e.FingerPrintSHA256);
                var pinned = target.PinnedHostKey;
                e.CanTrust = pinned is null || (pinned.Type == presented.Type && pinned.Fingerprint == presented.Fingerprint);
                refused = !e.CanTrust;
            };
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(target.ConnectTimeout + TimeSpan.FromSeconds(5));
                await client.ConnectAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                client.Dispose();
                throw Map(ex, refused, presented);
            }
            return new SshRouterSession(client, presented ?? throw new RouterSessionException(RouterFailureKind.Protocol, "SSH: no host key"));
        }
        finally
        {
            foreach (var key in keyFiles)
            {
                key.Dispose();
            }
        }
    }

    private static RouterSessionException Map(Exception ex, bool hostKeyRefused, RouterHostKey? presented)
    {
        if (hostKeyRefused)
        {
            return new RouterSessionException(RouterFailureKind.HostKeyChanged, $"Host key changed: the router presented {presented}", presented, ex);
        }
        return ex switch
        {
            SshAuthenticationException => new RouterSessionException(RouterFailureKind.AuthenticationFailed, "SSH: login refused", presented, ex),
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } =>
                new RouterSessionException(RouterFailureKind.Unreachable, "SSH: host name not found", presented, ex),
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } =>
                new RouterSessionException(RouterFailureKind.Unreachable, "SSH: connection refused", presented, ex),
            SocketException s => new RouterSessionException(RouterFailureKind.Unreachable, $"SSH: {s.SocketErrorCode}", presented, ex),
            SshOperationTimeoutException or OperationCanceledException or TimeoutException =>
                new RouterSessionException(RouterFailureKind.Unreachable, "SSH: connection timed out", presented, ex),
            ProxyException => new RouterSessionException(RouterFailureKind.Unreachable, $"Gateway: {ex.Message}", presented, ex),
            SshConnectionException => new RouterSessionException(RouterFailureKind.Unreachable, $"SSH: {ex.Message}", presented, ex),
            _ => new RouterSessionException(RouterFailureKind.Protocol, $"SSH: {ex.GetType().Name}: {ex.Message}", presented, ex),
        };
    }

    private sealed class SshRouterSession(SshClient client, RouterHostKey hostKey) : IRouterSession
    {
        public RouterHostKey HostKey { get; } = hostKey;

        public async Task<RouterExecResult> ExecAsync(string command, TimeSpan timeout, int maxOutputBytes, CancellationToken ct)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            using var cmd = client.CreateCommand(command);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            var output = new MemoryStream();
            var truncated = false;
            Task execution;
            try
            {
                execution = cmd.ExecuteAsync(limit.Token);
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException)
            {
                throw new RouterSessionException(RouterFailureKind.Protocol, $"SSH: {ex.Message}", inner: ex);
            }
            // The output is read while the command runs, so a router that sends without end is cut off
            // at the limit instead of filling the memory (§4.5).
            var reading = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                int n;
                while ((n = await cmd.OutputStream.ReadAsync(buffer, limit.Token)) > 0)
                {
                    var room = maxOutputBytes - (int)output.Length;
                    output.Write(buffer, 0, Math.Clamp(n, 0, Math.Max(room, 0)));
                    if (n > room)
                    {
                        truncated = true;
                        await limit.CancelAsync();
                        return;
                    }
                }
            }, CancellationToken.None);
            var timedOut = false;
            try
            {
                await execution;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = !truncated;
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException)
            {
                throw new RouterSessionException(RouterFailureKind.Protocol, $"SSH: {ex.Message}", inner: ex);
            }
            try
            {
                await reading.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or ObjectDisposedException)
            {
                // The channel is closed or cancelled: what was read so far is the output.
            }
            ct.ThrowIfCancellationRequested();
            string error;
            try
            {
                error = timedOut || truncated ? "" : cmd.Error;
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
            {
                error = "";
            }
            return new RouterExecResult(Decode(output.ToArray()), Cap(error, 64 * 1024), timedOut ? null : cmd.ExitStatus, timedOut, truncated, started.Elapsed);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
            }
            catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException)
            {
            }
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>RouterOS writes CRLF; the rest of PNMTD works with LF.</summary>
    internal static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max];
}
