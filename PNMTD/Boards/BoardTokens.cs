using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using PNMTD.Lib.Helper;

namespace PNMTD.Boards;

/// <summary>
/// A board's token (docs/boards-plan.md §5.1): <c>pnmb_</c> and 43 base64url characters, 32 random bytes, the connectors' shape with a
/// prefix of its own. A request finds its board by the token's SHA-256; the token itself is kept only under Data Protection, to show
/// the URL again.
/// </summary>
public static partial class BoardTokens
{
    public const string Prefix = "pnmb_";

    public static string New() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256, lower-case hex: what the database keeps and looks up.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string Hint(string token) => token[^4..];

    /// <summary>Shaped like a token; anything else is refused before the database is asked.</summary>
    public static bool LooksLikeOne(string? value) => value is not null && Pattern().IsMatch(value);

    /// <summary>For <see cref="Ai.SecretScanner"/>: a token anywhere in a text.</summary>
    [GeneratedRegex(@"\bpnmb_[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])")]
    public static partial Regex Anywhere();

    [GeneratedRegex(@"^pnmb_[A-Za-z0-9_-]{43}$")]
    private static partial Regex Pattern();
}

/// <summary>The tokens under Data Protection (purpose PNMTD.Boards.Token.v1), in the key ring of the <c>pnmtd-keys</c> volume.</summary>
public sealed class BoardTokenProtector(IDataProtectionProvider protection)
{
    private readonly IDataProtector protector = protection.CreateProtector("PNMTD.Boards.Token.v1");

    public string Protect(string token) => protector.Protect(token);

    /// <summary>Null when the key ring that protected it is gone (a restore without the volume): the URL still works, by its hash.</summary>
    public string? Unprotect(string protectedToken)
    {
        try
        {
            return protector.Unprotect(protectedToken);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// The requests per address on <c>/pnmt-board/api</c> (§5.2): unknown tokens block an address for a while, as
/// <c>ConnectorGuard</c> does for keys, and every address has a budget per minute. Pruned above 10 000 addresses.
/// </summary>
public sealed class BoardGuard
{
    /// <summary>Unknown tokens within <see cref="Window"/> that block an address.</summary>
    public const int FailedTokens = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan BlockFor = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, (DateTime Start, int Count, DateTime? BlockedUntil)> failed = new();

    private readonly ConcurrentDictionary<string, (DateTime Minute, int Count)> requests = new();

    /// <summary>When the address is blocked, until when.</summary>
    public DateTime? BlockedUntil(string address)
    {
        var now = Clock.UtcNow;
        return failed.TryGetValue(address, out var e) && e.BlockedUntil is { } until && until > now ? until : null;
    }

    /// <summary>Counts an unknown token; true when this one blocked the address.</summary>
    public bool Failed(string address)
    {
        var now = Clock.UtcNow;
        var blocked = false;
        (DateTime Start, int Count, DateTime? BlockedUntil) Next((DateTime Start, int Count, DateTime? BlockedUntil) e)
        {
            var count = e.Count + 1;
            if (count >= FailedTokens)
            {
                blocked = true;
                return (now, 0, now + BlockFor);
            }
            return (e.Start, count, null);
        }
        failed.AddOrUpdate(address, _ => Next((now, 0, null)),
            (_, e) => e.BlockedUntil is { } until && until > now ? e : Next(now - e.Start >= Window ? (now, 0, null) : e));
        Prune(now);
        return blocked;
    }

    /// <summary>Counts a request; false when the address used up its minute's budget.</summary>
    public bool Allow(string address, int perMinute)
    {
        var now = Clock.UtcNow;
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var entry = requests.AddOrUpdate(address, _ => (minute, 1), (_, e) => e.Minute == minute ? (minute, e.Count + 1) : (minute, 1));
        Prune(now);
        return entry.Count <= Math.Max(1, perMinute);
    }

    private void Prune(DateTime now)
    {
        if (failed.Count > 10_000)
        {
            foreach (var (key, value) in failed)
            {
                if (now - value.Start >= Window && (value.BlockedUntil is null || value.BlockedUntil < now))
                {
                    failed.TryRemove(key, out _);
                }
            }
        }
        if (requests.Count > 10_000)
        {
            foreach (var (key, value) in requests)
            {
                if (now - value.Minute >= TimeSpan.FromMinutes(2))
                {
                    requests.TryRemove(key, out _);
                }
            }
        }
    }

    /// <summary>For tests.</summary>
    public void Reset()
    {
        failed.Clear();
        requests.Clear();
    }
}
