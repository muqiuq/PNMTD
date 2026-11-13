using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PNMTD.Lib.Helper;

namespace PNMTD.Mikrotik;

/// <summary>
/// The router admin's password for one batch of jobs (docs/mikrotik-plan.md §7.2): in memory only, never
/// serialized, never logged, gone when the batch ends, after <c>Updates:CredentialLifetimeMinutes</c>, or with a
/// restart of PNMTD. The job rows record only the admin's name.
/// </summary>
public sealed class CredentialVault(IOptionsMonitor<MikrotikOptions> options)
{
    private sealed record Entry(string Username, string Password, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<Guid, Entry> entries = new();

    public void Put(Guid batchId, string username, string password) =>
        entries[batchId] = new Entry(username, password, Clock.UtcNow.AddMinutes(Math.Max(1, options.CurrentValue.Updates.CredentialLifetimeMinutes)));

    public (string Username, string Password)? Get(Guid batchId)
    {
        if (entries.TryGetValue(batchId, out var e))
        {
            if (e.ExpiresAt > Clock.UtcNow)
            {
                return (e.Username, e.Password);
            }
            entries.TryRemove(batchId, out _);
        }
        return null;
    }

    public void Remove(Guid batchId) => entries.TryRemove(batchId, out _);

    /// <summary>Never the secrets.</summary>
    public override string ToString() => $"{entries.Count} batch(es)";
}
