using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Cookie;

/// <summary>A new instruction: from Admin → Cookie, from the chat as a draft, or from a lesson (§10.3, §11, §21.1).</summary>
public sealed record InstructionDraft(string Kind, string Scope, string? Value, string? Label, string Text, DateTime Until);

/// <summary>
/// Mute, suppress and free-text instructions (docs/cookie-plan.md §10.3, §21.1), enforced in code. <i>Mute</i> a host, sensor,
/// location or case: no messages, tickets, notes on tickets or big brother calls for it; its cases still open and show.
/// <i>Suppress</i> a channel (pushover, email, ticket, redmine-note), a sensor type, or a kind of case. A <i>note</i> goes into
/// the prompts. Each ends, at most 30 days away; a draft from the chat counts only once a person confirms it.
/// </summary>
public sealed class Instructions(IDbContextFactory<PnmtdDbContext> dbs)
{
    public const int MaxDays = 30;

    public static readonly IReadOnlyList<string> Kinds = ["mute", "suppress", "note"];
    public static readonly IReadOnlyList<string> MuteScopes = ["host", "sensor", "location", "case"];
    public static readonly IReadOnlyList<string> SuppressScopes = ["channel", "sensor-type", "case-kind"];
    public static readonly IReadOnlyList<string> Channels = ["pushover", "email", "ticket", "redmine-note", "access", "away"];

    public async Task<List<CookieInstructionEntity>> ActiveAsync(CancellationToken ct)
    {
        var now = Clock.UtcNow;
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.CookieInstructions.AsNoTracking().Where(i => i.Confirmed && i.EndedAt == null && i.Until > now).OrderBy(i => i.CreatedAt).ToListAsync(ct);
    }

    /// <summary>The channel an action uses, for <i>suppress</i>.</summary>
    public static string? ChannelOf(CookieActionKind kind) => kind switch
    {
        CookieActionKind.Pushover => "pushover",
        // A suppress of the channel "email" stops the lounge's e-mails too (docs/lounge-plan.md §8.2).
        CookieActionKind.Email or CookieActionKind.LoungeEmail => "email",
        CookieActionKind.Ticket => "ticket",
        CookieActionKind.PrivateNote or CookieActionKind.PublicAnswer => "redmine-note",
        // The away watch's notice (docs/cookie-redmine-tab-plan.md §6.4): mute of the case, or a suppress of the channel "away".
        CookieActionKind.AwayNotice => "away",
        _ => null,
    };

    /// <summary>
    /// The instruction that stops an action, or null. Only what reaches people or leaves PNMT is muted or suppressed (and the
    /// big brother, as <see cref="CookieActionKind.HandToPerson"/> with <paramref name="bigBrother"/>); notes inside PNMT and a
    /// mail rule's event aren't.
    /// </summary>
    public static CookieInstructionEntity? Blocks(IReadOnlyList<CookieInstructionEntity> active, CookieActionKind kind, CookieCaseEntity? c,
        IReadOnlyList<CaseMember> members, Guid? objectId = null, bool bigBrother = false)
    {
        var channel = ChannelOf(kind);
        if (channel is null && !bigBrother)
        {
            return null;
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string scope, object? value)
        {
            if (value is not null)
            {
                ids.Add($"{scope}:{value}");
            }
        }
        Add("case", c?.Id);
        Add("host", c?.HostId);
        Add("location", c?.LocationId);
        Add("host", objectId);
        Add("sensor", objectId);
        foreach (var m in members.Where(m => m.Type == CaseMember.Sensor))
        {
            Add("sensor", m.Id);
        }
        var types = members.Select(m => m.SensorType).Where(t => t is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var i in active)
        {
            var value = i.Value?.Trim() ?? "";
            var hit = i.Kind switch
            {
                "mute" => ids.Contains($"{i.Scope}:{value}"),
                "suppress" => i.Scope switch
                {
                    "channel" => channel is not null && string.Equals(value, channel, StringComparison.OrdinalIgnoreCase),
                    "sensor-type" => types.Contains(value),
                    "case-kind" => c is not null && string.Equals(value, c.Kind.ToString(), StringComparison.OrdinalIgnoreCase),
                    _ => false,
                },
                _ => false,
            };
            if (hit)
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>
    /// A suppress of the channel <c>access</c> (docs/cookie-access-plan.md §6.4): it stops <i>unusual</i> and <i>suspicious</i> access
    /// notices for a while, never an <i>alarming</i> one. Mute doesn't stop them: a maintenance window is when nobody would notice.
    /// </summary>
    public static CookieInstructionEntity? SuppressesAccess(IReadOnlyList<CookieInstructionEntity> active, AccessNoticePayload? access) =>
        access is null || access.Verdict == "alarming" ? null
            : active.FirstOrDefault(i => i.Kind == "suppress" && i.Scope == "channel" && string.Equals(i.Value?.Trim(), "access", StringComparison.OrdinalIgnoreCase));

    /// <summary>The prompts' <c>{instructions}</c>: every active one, with who gave it and until when.</summary>
    public static string PromptText(IReadOnlyList<CookieInstructionEntity> active)
    {
        if (active.Count == 0)
        {
            return "None.";
        }
        var text = new StringBuilder();
        foreach (var i in active)
        {
            var until = Clock.ToLocal(i.Until).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var what = i.Kind == "note" ? i.Text : $"{i.Kind} {i.Scope} {i.Label ?? i.Value}{(string.IsNullOrWhiteSpace(i.Text) ? "" : $": {i.Text}")}";
            text.AppendLine(CultureInfo.InvariantCulture, $"- {what} (from {i.CreatedByName}, until {until})");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>Checks a new instruction (§10.3): a known kind and scope, a value where one is needed, an end at most 30 days away.</summary>
    public static void Validate(InstructionDraft d, ApiValidator v)
    {
        v.Check(Kinds.Contains(d.Kind), "kind", "mute, suppress or note.");
        if (d.Kind == "mute")
        {
            v.Check(MuteScopes.Contains(d.Scope), "scope", "host, sensor, location or case.");
            v.Check(Guid.TryParse(d.Value, out _), "value", "The id of what is muted.");
        }
        else if (d.Kind == "suppress")
        {
            v.Check(SuppressScopes.Contains(d.Scope), "scope", "channel, sensor-type or case-kind.");
            v.Check(!string.IsNullOrWhiteSpace(d.Value) && d.Value.Trim().Length <= 100, "value", "What is suppressed.");
            v.Check(d.Scope != "channel" || Channels.Contains(d.Value?.Trim().ToLowerInvariant() ?? ""), "value", "pushover, email, ticket, redmine-note, access or away.");
            v.Check(d.Scope != "case-kind" || d.Value?.Trim().ToLowerInvariant() is "problem" or "ticket", "value", "problem or ticket.");
        }
        else
        {
            v.Check(d.Text.Trim().Length is > 0 and <= 1000, "text", "1 to 1,000 characters.");
        }
        v.Check(d.Text.Length <= 1000, "text", "At most 1,000 characters.");
        v.Check(d.Until > Clock.UtcNow && d.Until <= Clock.UtcNow.AddDays(MaxDays).AddMinutes(1), "until", $"In the future, at most {MaxDays} days away.");
    }

    public async Task<CookieInstructionEntity> CreateAsync(InstructionDraft d, bool confirmed, string source, Guid? userId, string userName, CancellationToken ct)
    {
        var v = new ApiValidator();
        Validate(d, v);
        v.ThrowIfInvalid();
        await using var db = await dbs.CreateDbContextAsync(ct);
        var entity = new CookieInstructionEntity
        {
            Id = Guid.NewGuid(), Kind = d.Kind, Scope = d.Kind == "note" ? "" : d.Scope, Value = CaseBook.Cut(d.Value?.Trim(), 100),
            Label = CaseBook.Cut(d.Label?.Trim(), 200), Text = d.Text.Trim(), Until = d.Until, Confirmed = confirmed, CreatedAt = Clock.UtcNow,
            CreatedById = userId, CreatedByName = CaseBook.Cut(userName, 200)!, Source = source,
        };
        db.CookieInstructions.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }
}
