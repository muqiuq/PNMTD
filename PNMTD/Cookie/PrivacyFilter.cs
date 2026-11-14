using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Ai;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>
/// The privacy filter in <c>Code</c> mode (docs/cookie-plan.md §13.2, §19.1): the names of PNMT's users and of the Redmine
/// people Cookie has seen, e-mail addresses and phone numbers become <c>PERSON_n</c>, <c>EMAIL_n</c> and <c>PHONE_n</c>.
/// Host names and addresses stay. The placeholders are stable: one mapping for the whole server, handed out under a lock,
/// kept in <c>cookie_pseudonyms</c>, and restored by code before anything is shown. <c>Off</c> leaves the text as it is.
/// </summary>
public sealed partial class PrivacyFilter(IDbContextFactory<PnmtdDbContext> dbs, IOptionsMonitor<CookieOptions> options)
{
    private readonly Lock gate = new();
    private readonly Dictionary<(string Kind, string Value), string> byValue = [];
    private readonly Dictionary<string, string> byPlaceholder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> next = [];
    private readonly List<CookiePseudonymEntity> pending = [];
    private bool loaded;

    public bool Off => options.CurrentValue.PrivacyOff;

    /// <summary>Account names that are ordinary words: masked, they would turn every "admin" or "backup" in a text into a person.</summary>
    public static readonly IReadOnlySet<string> AccountWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "root", "test", "tester", "user", "users", "operator", "operators", "viewer", "viewers", "guest", "system", "service",
        "support", "backup", "monitor", "monitoring", "pnmt", "pnmtd", "cookie", "seraph", "redmine", "zabbix", "helpdesk", "it", "noc", "ops",
    };

    /// <summary>A filter for one round or request, with the names known now.</summary>
    public async Task<Pseudonyms> OpenAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await LoadAsync(db, ct);
        if (Off)
        {
            return new Pseudonyms(this, null);
        }
        var names = new List<string>();
        foreach (var u in await db.Users.AsNoTracking().Select(u => new { u.DisplayName, u.Username }).ToListAsync(ct))
        {
            names.Add(u.DisplayName);
            names.Add(u.Username);
        }
        names.AddRange(await db.CookieCases.AsNoTracking().Where(c => c.AuthorName != null).Select(c => c.AuthorName!).Distinct().ToListAsync(ct));
        names.AddRange(await db.CookieCases.AsNoTracking().Where(c => c.AssigneeName != null).Select(c => c.AssigneeName!).Distinct().ToListAsync(ct));
        names.AddRange(await db.RedmineIssues.AsNoTracking().Where(i => i.AssignedToName != null).Select(i => i.AssignedToName!).Distinct().ToListAsync(ct));
        lock (gate)
        {
            names.AddRange(byValue.Where(p => p.Key.Kind == "PERSON").Select(p => byPlaceholder[p.Value]));
        }
        var list = names.Select(n => n.Trim()).Where(n => n.Length >= 3 && !n.All(char.IsDigit) && !AccountWords.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(n => n.Length).ToList();
        var regex = list.Count == 0 ? null
            : new Regex($@"(?<![\w.-])(?:{string.Join("|", list.Select(Regex.Escape))})(?![\w-])", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
        return new Pseudonyms(this, regex);
    }

    /// <summary>The placeholders back to their values, for showing or sending.</summary>
    public string Unmask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }
        lock (gate)
        {
            return PlaceholderPattern().Replace(text, m => byPlaceholder.TryGetValue(m.Value, out var value) ? value : m.Value);
        }
    }

    /// <summary>Loads the mapping once, so that texts written before a restart are restored.</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (!loaded)
        {
            await using var db = await dbs.CreateDbContextAsync(ct);
            await LoadAsync(db, ct);
        }
    }

    private async Task LoadAsync(PnmtdDbContext db, CancellationToken ct)
    {
        if (loaded)
        {
            return;
        }
        var known = await db.CookiePseudonyms.AsNoTracking().ToListAsync(ct);
        lock (gate)
        {
            if (loaded)
            {
                return;
            }
            foreach (var p in known)
            {
                byValue[(p.Kind, p.Value)] = p.Placeholder;
                byPlaceholder[p.Placeholder] = p.Original;
                var n = int.TryParse(p.Placeholder.AsSpan(p.Placeholder.LastIndexOf('_') + 1), out var number) ? number : 0;
                next[p.Kind] = Math.Max(next.GetValueOrDefault(p.Kind), n);
            }
            loaded = true;
        }
    }

    internal string Placeholder(string kind, string value)
    {
        var key = (kind, value.Trim().ToLowerInvariant());
        lock (gate)
        {
            if (byValue.TryGetValue(key, out var existing))
            {
                return existing;
            }
            var n = next.GetValueOrDefault(kind) + 1;
            next[kind] = n;
            var placeholder = $"{kind}_{n}";
            byValue[key] = placeholder;
            byPlaceholder[placeholder] = value.Trim();
            pending.Add(new CookiePseudonymEntity
            {
                Kind = kind, Value = CaseBook.Cut(key.Item2, 300)!, Original = CaseBook.Cut(value.Trim(), 300)!, Placeholder = placeholder,
                CreatedAt = Clock.UtcNow,
            });
            return placeholder;
        }
    }

    /// <summary>Writes the placeholders handed out since the last call.</summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        List<CookiePseudonymEntity> batch;
        lock (gate)
        {
            batch = [.. pending];
            pending.Clear();
        }
        if (batch.Count == 0)
        {
            return;
        }
        await using var db = await dbs.CreateDbContextAsync(ct);
        db.CookiePseudonyms.AddRange(batch);
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex(@"\b(?:PERSON|EMAIL|PHONE)_\d+\b")]
    private static partial Regex PlaceholderPattern();
}

/// <summary>One round's masking, with the names known when it began.</summary>
public sealed class Pseudonyms(PrivacyFilter owner, Regex? names)
{
    /// <summary>The text with people, e-mail addresses and phone numbers replaced.</summary>
    public string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text) || owner.Off)
        {
            return text ?? "";
        }
        text = Scrubber.Mail().Replace(text, m => owner.Placeholder("EMAIL", m.Value));
        if (names is not null)
        {
            text = names.Replace(text, m => owner.Placeholder("PERSON", m.Value));
        }
        return Scrubber.Phone().Replace(text, m => Scrubber.IsPhoneNumber(m.Value) ? owner.Placeholder("PHONE", m.Value) : m.Value);
    }

    public string Unmask(string? text) => owner.Unmask(text);

    public Task SaveAsync(CancellationToken ct) => owner.SaveAsync(ct);
}
