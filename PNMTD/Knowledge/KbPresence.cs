using PNMTD.Api.V1.Contracts;
using PNMTD.Lib.Helper;

namespace PNMTD.Knowledge;

/// <summary>
/// Who else is editing a page (docs/locations-knowledge-plan.md §5.7): an open editor reports every 30 seconds, a report
/// counts for 90. In memory: it blocks nothing, and one PNMTD instance needs no more. <i>Since</i> is the first report of
/// an unbroken run.
/// </summary>
public sealed class KbPresence
{
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(90);

    private readonly object gate = new();
    private readonly Dictionary<(Guid Page, Guid User), Entry> entries = [];

    private sealed record Entry(string Name, DateTime Since, DateTime Last);

    /// <summary>The user is on the page's editing page now; <paramref name="stopped"/>: they left it.</summary>
    public void Report(Guid page, Guid user, string name, bool stopped = false)
    {
        var now = Clock.UtcNow;
        lock (gate)
        {
            if (stopped)
            {
                entries.Remove((page, user));
                return;
            }
            var since = entries.TryGetValue((page, user), out var entry) && now - entry.Last <= Expiry ? entry.Since : now;
            entries[(page, user)] = new Entry(name, since, now);
            if (entries.Count > 1000)
            {
                foreach (var key in entries.Where(e => now - e.Value.Last > Expiry).Select(e => e.Key).ToList())
                {
                    entries.Remove(key);
                }
            }
        }
    }

    /// <summary>Everyone editing the page within the last 90 seconds but <paramref name="me"/>, the longest first.</summary>
    public IReadOnlyList<KbEditor> Others(Guid page, Guid? me)
    {
        var now = Clock.UtcNow;
        lock (gate)
        {
            return entries.Where(e => e.Key.Page == page && e.Key.User != me && now - e.Value.Last <= Expiry)
                .OrderBy(e => e.Value.Since).Select(e => new KbEditor(e.Value.Name, e.Value.Since)).ToList();
        }
    }
}
