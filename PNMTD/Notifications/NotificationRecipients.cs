using Microsoft.EntityFrameworkCore;
using PNMTD.Availability;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Notifications;

/// <summary>One message to one address (docs/notification-recipients-plan.md §4): the recipient as a provider takes it, whose it is, and how.</summary>
public sealed record Delivery(string Recipient, string Person, string Channel);

/// <summary>
/// Whom a rule reaches at a moment (§4). <see cref="Who"/> is the summary for the rule's page and <c>LastSentTo</c>;
/// <see cref="Why"/> says why there is nobody; <see cref="Because"/> is the line the message carries: "to you as the responsible today".
/// </summary>
public sealed record Resolved(IReadOnlyList<Delivery> Deliveries, string Who, string? Why, string? Because)
{
    public static Resolved Nobody(string why) => new([], "", why, null);

    /// <summary>How many people, not addresses.</summary>
    public int People => Deliveries.Select(d => d.Person).Distinct().Count();
}

/// <summary>
/// The one place that turns a rule into deliveries (docs/notification-recipients-plan.md §4): an address as it is; a user or a
/// group's active members at the contact details of their accounts, by the rule's channels; the responsible as the plan names them,
/// then the rule's stand-in. The task, the rule's page and the coverage all ask it.
/// </summary>
public sealed class NotificationRecipients(AvailabilityPlan plan)
{
    public const string ResponsibleLabel = "The responsible of the day";

    private sealed record Person(Guid Id, string Name, bool IsActive, string? Email, string? PushoverKey);

    public async Task<Resolved> ResolveAsync(PnmtdDbContext db, NotificationRuleEntity rule, DateTime utcNow, CancellationToken ct) =>
        rule.RecipientKind switch
        {
            NotificationRecipientKind.User => await UserAsync(db, rule, ct),
            NotificationRecipientKind.Group => await GroupAsync(db, rule, ct),
            NotificationRecipientKind.Responsible => await ResponsibleAsync(db, rule, utcNow, ct),
            _ => Address(rule),
        };

    /// <summary>"e-mail address or Pushover key": what the rule's channels need a person to have.</summary>
    public static string Details(NotificationRuleEntity rule) => (rule.ByEmail, rule.ByPushover) switch
    {
        (true, true) => "e-mail address or Pushover key",
        (true, false) => "e-mail address",
        (false, true) => "Pushover key",
        _ => "channel",
    };

    /// <summary>"Pushover and e-mail", for the audit and the page.</summary>
    public static string Channels(NotificationRuleEntity rule) => (rule.ByEmail, rule.ByPushover) switch
    {
        (true, true) => "Pushover and e-mail",
        (true, false) => "e-mail",
        (false, true) => "Pushover",
        _ => "nothing",
    };

    private static Resolved Address(NotificationRuleEntity rule)
    {
        var provider = NotificationProviders.Match(rule.Recipient);
        return provider is null
            ? Resolved.Nobody("The address matches no provider.")
            : new Resolved([new Delivery(rule.Recipient!.Trim(), "", provider)], provider, null, null);
    }

    private static async Task<Resolved> UserAsync(PnmtdDbContext db, NotificationRuleEntity rule, CancellationToken ct)
    {
        if (rule.UserId is not { } id)
        {
            return Resolved.Nobody("The user was removed.");
        }
        var person = await PeopleAsync(db, [id], ct);
        if (person.Count == 0)
        {
            return Resolved.Nobody("The user was removed.");
        }
        if (!person[0].IsActive)
        {
            return Resolved.Nobody($"{person[0].Name} is deactivated.");
        }
        return Reach(person, rule, "to you", _ => null, nobody: $"{person[0].Name} has no {Details(rule)}.");
    }

    private static async Task<Resolved> GroupAsync(PnmtdDbContext db, NotificationRuleEntity rule, CancellationToken ct)
    {
        if (rule.GroupId is not { } id)
        {
            return Resolved.Nobody("The group was removed.");
        }
        var name = await db.Groups.AsNoTracking().Where(g => g.Id == id).Select(g => g.Name).SingleOrDefaultAsync(ct);
        if (name is null)
        {
            return Resolved.Nobody("The group was removed.");
        }
        var members = await MembersAsync(db, id, ct);
        if (members.Count == 0)
        {
            return Resolved.Nobody($"{name} has no active member.");
        }
        return Reach(members, rule, $"to you as a member of {name}", _ => null, nobody: $"{name} has no active member with {Details(rule)}.");
    }

    private async Task<Resolved> ResponsibleAsync(PnmtdDbContext db, NotificationRuleEntity rule, DateTime utcNow, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(Clock.ToLocal(utcNow));
        var snapshot = await plan.DayAsync(today, ct);
        var working = snapshot.Calendar.IsWorking(utcNow);
        var responsibility = rule.Hours == NotificationHours.WorkingHours ? snapshot.ResponsibleAt(utcNow) : snapshot.ResponsibleOn(today);
        var notes = new List<string>();
        if (responsibility.UserIds.Count > 0)
        {
            var named = (await PeopleAsync(db, responsibility.UserIds, ct)).Where(p => p.IsActive).ToList();
            var role = responsibility.As;
            var reached = Reach(named, rule, $"to you as the {role} today", _ => role, nobody: "");
            if (reached.Deliveries.Count > 0)
            {
                return reached;
            }
            notes.Add(named.Count == 0 ? $"the {role} is deactivated" : $"the {role} ({string.Join(", ", named.Select(p => p.Name))}) has no {Details(rule)}");
        }
        else
        {
            notes.Add(rule.Hours == NotificationHours.WorkingHours && !working ? "outside working hours"
                : snapshot.Closed(today) ? "a closed day: nobody is responsible"
                : "nobody is responsible today");
        }

        // The stand-in (§4.3), whether or not they work: it is for when the plan has nobody.
        if (rule.UserId is { } userId)
        {
            var standIn = (await PeopleAsync(db, [userId], ct)).Where(p => p.IsActive).ToList();
            if (standIn.Count == 0)
            {
                notes.Add("the stand-in was removed or is deactivated");
            }
            else
            {
                var reached = Reach(standIn, rule, "to you as the stand-in for the responsible", _ => "stand-in", nobody: "");
                if (reached.Deliveries.Count > 0)
                {
                    return reached;
                }
                notes.Add($"the stand-in {standIn[0].Name} has no {Details(rule)}");
            }
        }
        else if (rule.GroupId is { } groupId)
        {
            var name = await db.Groups.AsNoTracking().Where(g => g.Id == groupId).Select(g => g.Name).SingleOrDefaultAsync(ct);
            var members = name is null ? [] : await MembersAsync(db, groupId, ct);
            if (name is null)
            {
                notes.Add("the stand-in group was removed");
            }
            else if (members.Count == 0)
            {
                notes.Add($"the stand-in group {name} has no active member");
            }
            else
            {
                var reached = Reach(members, rule, "to you as the stand-in for the responsible", _ => "stand-in", nobody: "");
                if (reached.Deliveries.Count > 0)
                {
                    return reached;
                }
                notes.Add($"the stand-in group {name} has no active member with {Details(rule)}");
            }
        }
        else
        {
            notes.Add("the rule has no stand-in");
        }
        var why = string.Join(", and ", notes);
        return Resolved.Nobody(char.ToUpperInvariant(why[0]) + why[1..] + ".");
    }

    /// <summary>The people's deliveries by the rule's channels, and the summary naming each one with their role and channels.</summary>
    private static Resolved Reach(IReadOnlyList<Person> people, NotificationRuleEntity rule, string because, Func<Person, string?> role, string nobody)
    {
        var deliveries = new List<Delivery>();
        var parts = new List<string>();
        foreach (var p in people)
        {
            var own = new List<Delivery>();
            if (rule.ByPushover && !string.IsNullOrWhiteSpace(p.PushoverKey))
            {
                own.Add(new Delivery(PushoverNotification.Prefix + p.PushoverKey.Trim(), p.Name, "Pushover"));
            }
            if (rule.ByEmail && !string.IsNullOrWhiteSpace(p.Email))
            {
                own.Add(new Delivery(p.Email.Trim(), p.Name, "e-mail"));
            }
            var label = role(p) is { } r ? $"{p.Name} ({r})" : p.Name;
            parts.Add(own.Count == 0 ? $"{p.Name} (no {Details(rule)})" : $"{label}: {string.Join(", ", own.Select(d => d.Channel))}");
            deliveries.AddRange(own);
        }
        return deliveries.Count == 0 ? Resolved.Nobody(nobody) : new Resolved(deliveries, string.Join("; ", parts), null, because);
    }

    private static async Task<List<Person>> PeopleAsync(PnmtdDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).OrderBy(u => u.DisplayName)
            .Select(u => new Person(u.Id, u.DisplayName, u.IsActive, u.Email, u.PushoverKey)).ToListAsync(ct);

    /// <summary>The group's active members, by name.</summary>
    private static async Task<List<Person>> MembersAsync(PnmtdDbContext db, Guid groupId, CancellationToken ct) =>
        await db.UserGroups.AsNoTracking().Where(m => m.GroupId == groupId)
            .Join(db.Users.AsNoTracking().Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, u) => u)
            .OrderBy(u => u.DisplayName)
            .Select(u => new Person(u.Id, u.DisplayName, u.IsActive, u.Email, u.PushoverKey)).ToListAsync(ct);
}
