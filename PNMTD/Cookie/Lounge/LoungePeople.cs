using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie.Lounge;

/// <summary>What wakes the lounge (docs/lounge-plan.md §13): a person's message or comment, Start, Stop, Talk now, Put to sleep, a settings change.</summary>
public sealed class LoungeSignals
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly Lock gate = new();
    private readonly List<string> reasons = [];

    public void Raise(string reason)
    {
        lock (gate)
        {
            if (reasons.Count < 50)
            {
                reasons.Add(reason);
            }
            if (signal.CurrentCount == 0)
            {
                signal.Release();
            }
        }
    }

    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => await signal.WaitAsync(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, ct);

    public IReadOnlyList<string> Take()
    {
        lock (gate)
        {
            var taken = reasons.Distinct().ToList();
            reasons.Clear();
            return taken;
        }
    }
}

/// <summary>
/// The people of one scene (docs/lounge-plan.md §9.4): each PNMT user's placeholders (or names, with the privacy filter off), who opted out
/// of being talked about (a deactivated user counts as opted out), and their names as the voices see them: <i>a colleague</i>.
/// </summary>
public sealed partial class LoungePeople
{
    public const string Colleague = "a colleague";

    private readonly Regex? optedOut;

    private LoungePeople(IReadOnlyDictionary<string, Guid> byToken, IReadOnlyDictionary<Guid, (string Name, bool Mentions, bool Active)> users,
        IReadOnlyList<string> optedOutTokens)
    {
        ByToken = byToken;
        Users = users;
        OptedOutTokens = optedOutTokens;
        optedOut = optedOutTokens.Count == 0 ? null
            : new Regex($@"(?<![\w]){string.Join("|", optedOutTokens.OrderByDescending(t => t.Length).Select(Regex.Escape))}(?![\w])", RegexOptions.IgnoreCase,
                TimeSpan.FromMilliseconds(500));
    }

    /// <summary>A placeholder (or name) → the user.</summary>
    public IReadOnlyDictionary<string, Guid> ByToken { get; }

    public IReadOnlyDictionary<Guid, (string Name, bool Mentions, bool Active)> Users { get; }

    /// <summary>What the voices must not mention (the prompt's <c>{not_mentioned}</c>).</summary>
    public IReadOnlyList<string> OptedOutTokens { get; }

    public static async Task<LoungePeople> BuildAsync(PnmtdDbContext db, Pseudonyms p, bool privacyOff, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.DisplayName, u.Username, u.IsActive, u.LoungeMentions }).ToListAsync(ct);
        var byToken = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var optedOut = new List<string>();
        foreach (var u in users)
        {
            foreach (var name in new[] { u.DisplayName, u.Username })
            {
                var token = privacyOff ? name.Trim() : p.Mask(name).Trim();
                if (token.Length < 3 || (!privacyOff && !Placeholder().IsMatch(token)) || PrivacyFilter.AccountWords.Contains(token))
                {
                    continue;
                }
                byToken.TryAdd(token, u.Id);
                if (!u.IsActive || !u.LoungeMentions)
                {
                    optedOut.Add(token);
                }
            }
        }
        return new LoungePeople(byToken, users.ToDictionary(u => u.Id, u => (u.DisplayName, u.LoungeMentions, u.IsActive)), [.. optedOut.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The text with everyone who opted out as <i>a colleague</i> (§9.4).</summary>
    public string Hide(string? text) => string.IsNullOrEmpty(text) || optedOut is null ? text ?? "" : optedOut.Replace(text, Colleague);

    /// <summary>The first opted-out person a voice's message names, or null.</summary>
    public string? Names(string text) => optedOut?.Match(text) is { Success: true } m ? m.Value : null;

    /// <summary>A user as the voices see them: their placeholder, or <i>a colleague</i> when they opted out.</summary>
    public string As(Guid? userId, Pseudonyms p) =>
        userId is { } id && Users.TryGetValue(id, out var u) ? (u.Mentions && u.Active ? p.Mask(u.Name) : Colleague) : Colleague;

    [GeneratedRegex(@"^PERSON_\d+$")]
    private static partial Regex Placeholder();
}

/// <summary>The lounge's clock for the API and the tab: one place for "today" in PNMT's zone.</summary>
internal static class LoungeDay
{
    public static DateTime TodayUtc => Clock.ToUtc(Clock.LocalNow.Date);
}
