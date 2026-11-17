using System.Globalization;
using System.Net;

namespace PNMTD.Lib.Certificates;

/// <summary>
/// Where a certificate is read (docs/ssl-certificate-plan.md §4.3): a host and a port, from <c>example.com</c>,
/// <c>example.com:8443</c>, <c>https://example.com/path</c>, <c>10.0.0.5:443</c>, <c>[2001:db8::1]:443</c> or a bare
/// <c>2001:db8::1</c>. Stored as <see cref="Normalized"/>; international names as typed, sent as punycode.
/// </summary>
public sealed record TlsEndpoint(string Host, int Port)
{
    public const int DefaultPort = 443;

    public const int MaxHostLength = 253;

    private static readonly Dictionary<string, int> SchemePorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["https"] = 443, ["wss"] = 443, ["ldaps"] = 636, ["imaps"] = 993, ["pop3s"] = 995, ["smtps"] = 465, ["ftps"] = 990,
    };

    /// <summary>The host is an IPv4 or IPv6 address.</summary>
    public bool IsAddress => IsIp(Host);

    /// <summary>An IPv6 address, or an IPv4 address in its four parts (not the shorthands "10.1" or "123" that IPAddress also takes).</summary>
    public static bool IsIp(string? text) =>
        text is not null && IPAddress.TryParse(text, out var ip)
        && (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 || text.Split('.').Length == 4);

    /// <summary><c>host:port</c>, <c>[v6]:port</c>.</summary>
    public string Normalized => IPAddress.TryParse(Host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"[{Host}]:{Port.ToString(CultureInfo.InvariantCulture)}"
        : $"{Host}:{Port.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() => Normalized;

    /// <summary>The name as the network knows it: punycode for an international name, the address as it is.</summary>
    public static string Ascii(string host)
    {
        if (IsIp(host) || host.All(c => c < 128))
        {
            return host.ToLowerInvariant();
        }
        return new IdnMapping().GetAscii(host).ToLowerInvariant();
    }

    public static bool TryParse(string? text, out TlsEndpoint endpoint, out string? error)
    {
        endpoint = new TlsEndpoint("", DefaultPort);
        error = null;
        var value = text?.Trim() ?? "";
        if (value.Length == 0)
        {
            error = "An address: host, host:port or an https:// URL.";
            return false;
        }
        var port = DefaultPort;
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            var scheme = value[..schemeIndex];
            if (!SchemePorts.TryGetValue(scheme, out port))
            {
                error = $"A TLS address: {scheme}:// is not one (https://, ldaps://, imaps://, smtps://, …).";
                return false;
            }
            value = value[(schemeIndex + 3)..];
            var end = value.IndexOfAny(['/', '?', '#']);
            if (end >= 0)
            {
                value = value[..end];
            }
            var at = value.LastIndexOf('@');
            if (at >= 0)
            {
                value = value[(at + 1)..];
            }
        }
        string host;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
            {
                error = "An IPv6 address in brackets: [2001:db8::1]:443.";
                return false;
            }
            host = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !TryPort(rest[1..], out port)))
            {
                error = "A port from 1 to 65535 after the address: [2001:db8::1]:443.";
                return false;
            }
            if (!IPAddress.TryParse(host, out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                error = "Not an IPv6 address.";
                return false;
            }
        }
        else if (value.Count(c => c == ':') > 1)
        {
            // A bare IPv6 address: the port is 443 (or the scheme's).
            host = value;
            if (!IPAddress.TryParse(host, out _))
            {
                error = "Not an address: an IPv6 address with a port goes in brackets, [2001:db8::1]:443.";
                return false;
            }
        }
        else
        {
            var colon = value.IndexOf(':');
            host = colon >= 0 ? value[..colon] : value;
            if (colon >= 0 && !TryPort(value[(colon + 1)..], out port))
            {
                error = "A port from 1 to 65535: host:443.";
                return false;
            }
        }
        host = host.TrimEnd('.');
        if (!IsHost(host))
        {
            error = "A host name or an IP address.";
            return false;
        }
        if (IsIp(host))
        {
            host = IPAddress.Parse(host).ToString();
        }
        endpoint = new TlsEndpoint(IsIp(host) ? host : host.ToLowerInvariant(), port);
        return true;
    }

    public static TlsEndpoint Parse(string text) =>
        TryParse(text, out var endpoint, out var error) ? endpoint : throw new FormatException(error);

    /// <summary>A DNS name (international ones too) or an IP address, at most 253 characters.</summary>
    public static bool IsHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaxHostLength || host.Any(char.IsWhiteSpace))
        {
            return false;
        }
        if (IsIp(host))
        {
            return true;
        }
        string ascii;
        try
        {
            ascii = Ascii(host.TrimEnd('.'));
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (ascii.Length > MaxHostLength)
        {
            return false;
        }
        var labels = ascii.Split('.');
        // A wildcard is no host to connect to; underscores occur in internal names and are accepted.
        return labels.All(l => l.Length is >= 1 and <= 63 && !l.StartsWith('-') && !l.EndsWith('-')
            && l.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            && !labels[^1].All(char.IsDigit);
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}
