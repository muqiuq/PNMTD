using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Notifications;

namespace PNMTD.Services.Domain;

/// <summary>Which notification provider a recipient goes to, by the providers' own rules.</summary>
public static class NotificationProviders
{
    public static string? Match(string? recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return null;
        }
        if (PushoverNotification.Matches(recipient))
        {
            return "pushover";
        }
        if (HttpNotification.Matches(recipient))
        {
            return "http";
        }
        return EMailNotification.Matches(recipient) ? "email" : null;
    }

    /// <summary>For a Viewer: a pushover user key or a webhook URL can carry secrets.</summary>
    public static string Mask(string recipient, string provider) => provider switch
    {
        "pushover" => "pushover://…",
        "http" when Uri.TryCreate(recipient, UriKind.Absolute, out var uri) => $"{uri.Scheme}://{uri.Authority}/…",
        "http" => "http…",
        _ => recipient,
    };

    /// <summary>
    /// What a rule shows as its recipient and provider (docs/notification-recipients-plan.md §6.2): an address with its provider,
    /// masked for a Viewer; a user's or a group's name with <c>user</c> or <c>group</c>; the responsible with <c>responsible</c>.
    /// </summary>
    public static (string Label, string Provider) Describe(NotificationRecipientKind kind, string? recipient, string? userName, string? groupName,
        bool withSecrets)
    {
        switch (kind)
        {
            case NotificationRecipientKind.User:
                return (userName ?? "Removed user", "user");
            case NotificationRecipientKind.Group:
                return (groupName ?? "Removed group", "group");
            case NotificationRecipientKind.Responsible:
                return (NotificationRecipients.ResponsibleLabel, "responsible");
            default:
                var address = recipient ?? "";
                var provider = Match(address) ?? "unknown";
                return (withSecrets ? address : Mask(address, provider), provider);
        }
    }
}

public sealed class NotificationRuleService(PnmtdDbContext db, NotificationRecipients recipients)
{
    private static readonly string[] SortFields = ["name", "type", "recipient"];

    /// <summary>Sort: name (default), type, recipient (the label). Search: the name, the address, the user's and the group's name.</summary>
    public async Task<Paged<NotificationRuleItem>> ListAsync(PageQuery query, bool withSecrets, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "name", SortFields);
        var pattern = Text.Pattern(query.Search);
        var rules = db.NotificationRules.AsNoTracking();
        if (pattern is not null)
        {
            rules = rules.Where(r => EF.Functions.ILike(r.Name, pattern, @"\")
                || (r.Recipient != null && EF.Functions.ILike(r.Recipient, pattern, @"\"))
                || (r.User != null && EF.Functions.ILike(r.User.DisplayName, pattern, @"\"))
                || (r.Group != null && EF.Functions.ILike(r.Group.Name, pattern, @"\")));
        }
        rules = (field, descending) switch
        {
            ("type", false) => rules.OrderBy(r => r.Type).ThenBy(r => r.Name),
            ("type", true) => rules.OrderByDescending(r => r.Type).ThenBy(r => r.Name),
            ("recipient", false) => rules.OrderBy(Label).ThenBy(r => r.Name),
            ("recipient", true) => rules.OrderByDescending(Label).ThenBy(r => r.Name),
            (_, true) => rules.OrderByDescending(r => r.Name),
            _ => rules.OrderBy(r => r.Name),
        };
        var page = await Paging.PageAsync(rules.Select(Row), query, ct);
        return new Paged<NotificationRuleItem>(page.Items.Select(r => ToItem(r, withSecrets)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    /// <summary>The recipient as it is shown, for sorting: the address, the user's name, the group's name, or the responsible's label.</summary>
    private static readonly System.Linq.Expressions.Expression<Func<NotificationRuleEntity, string?>> Label = r =>
        r.RecipientKind == NotificationRecipientKind.User ? (r.User != null ? r.User.DisplayName : "Removed user")
        : r.RecipientKind == NotificationRecipientKind.Group ? (r.Group != null ? r.Group.Name : "Removed group")
        : r.RecipientKind == NotificationRecipientKind.Responsible ? NotificationRecipients.ResponsibleLabel
        : r.Recipient;

    /// <summary>The one projection of a rule for an item, with the names its label needs (§6.2).</summary>
    internal static readonly System.Linq.Expressions.Expression<Func<NotificationRuleEntity, RuleRow>> Row = r => new RuleRow(r.Id, r.Name, r.Recipient,
        r.Type, r.Enabled, r.SubscribedSensors.Count, r.Scopes.Count, r.RecipientKind, r.User != null ? r.User.DisplayName : null,
        r.Group != null ? r.Group.Name : null);

    internal sealed record RuleRow(Guid Id, string Name, string? Recipient, NotificationRuleType Type, bool Enabled, int SensorCount, int ScopeCount = 0,
        NotificationRecipientKind Kind = NotificationRecipientKind.Address, string? UserName = null, string? GroupName = null);

    internal static NotificationRuleItem ToItem(RuleRow r, bool withSecrets, string? via = null)
    {
        var (label, provider) = NotificationProviders.Describe(r.Kind, r.Recipient, r.UserName, r.GroupName, withSecrets);
        return new NotificationRuleItem(r.Id, r.Name, label, provider, r.Type, r.Enabled, r.SensorCount, r.ScopeCount, via, r.Kind);
    }

    /// <summary>The rules that cover the sensor (docs/ai-operations-plan.md §11.4): linked, or through a scope.</summary>
    public async Task<IReadOnlyList<NotificationRuleItem>> ForSensorAsync(Guid sensorId, bool withSecrets, CancellationToken ct)
    {
        var sensor = await db.Sensors.AsNoTracking().Where(s => s.Id == sensorId)
            .Select(s => new { s.ParentId, Importance = s.Importance ?? s.Parent.Importance }).SingleOrDefaultAsync(ct);
        var rows = await db.NotificationRules.AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                Row = new RuleRow(r.Id, r.Name, r.Recipient, r.Type, r.Enabled, r.SubscribedSensors.Count, r.Scopes.Count, r.RecipientKind,
                    r.User != null ? r.User.DisplayName : null, r.Group != null ? r.Group.Name : null),
                Linked = r.SubscribedSensors.Any(l => l.SensorId == sensorId),
                Excluded = r.Exclusions.Any(x => x.SensorId == sensorId),
                Scopes = r.Scopes.Select(s => new { s.Kind, s.Importance, s.HostId, HostName = s.Host != null ? s.Host.Name : null }).ToList(),
            })
            .ToListAsync(ct);
        var items = new List<NotificationRuleItem>();
        foreach (var r in rows)
        {
            if (r.Linked)
            {
                items.Add(ToItem(r.Row, withSecrets, "link"));
                continue;
            }
            if (sensor is null || r.Excluded)
            {
                continue;
            }
            var scope = r.Scopes.FirstOrDefault(s => new NotificationRuleScopeEntity { Kind = s.Kind, Importance = s.Importance, HostId = s.HostId }
                .Covers(sensor.ParentId, sensor.Importance));
            if (scope is not null)
            {
                items.Add(ToItem(r.Row, withSecrets, "scope: " + NotificationManagerService.Label(scope.Kind, scope.Importance, scope.HostName)));
            }
        }
        return items;
    }

    public async Task<NotificationRuleDetail> GetAsync(Guid id, bool withSecrets, CancellationToken ct)
    {
        var rule = await db.NotificationRules.AsNoTracking().Include(r => r.User).Include(r => r.Group).SingleOrDefaultAsync(r => r.Id == id, ct)
            ?? throw ApiException.NotFound("Notification rule");
        var (label, provider) = NotificationProviders.Describe(rule.RecipientKind, rule.Recipient, rule.User?.DisplayName, rule.Group?.Name, withSecrets);
        var scopes = await db.NotificationRuleScopes.AsNoTracking().Where(s => s.RuleId == id)
            .OrderBy(s => s.Kind).ThenBy(s => s.Importance)
            .Select(s => new NotificationScope(s.Kind, s.Importance, s.Host != null ? new Ref(s.Host.Id, s.Host.Name) : null))
            .ToListAsync(ct);
        var exclusions = await db.Sensors.AsNoTracking()
            .Where(s => db.NotificationRuleExclusions.Any(x => x.RuleId == id && x.SensorId == s.Id))
            .OrderBy(s => s.Parent.Name).ThenBy(s => s.Name)
            .Select(s => new LinkedSensor(s.Id, s.Name, s.Type, s.ParentId, s.Parent.Name))
            .ToListAsync(ct);
        var resolved = await recipients.ResolveAsync(db, rule, Clock.UtcNow, ct);
        return new NotificationRuleDetail(rule.Id, rule.Name, label, provider, rule.Type, rule.Enabled, await SensorsAsync(id, ct), scopes, exclusions,
            rule.RecipientKind, rule.User is { } u ? new Ref(u.Id, u.DisplayName) : null, rule.Group is { } g ? new Ref(g.Id, g.Name) : null,
            rule.ByEmail, rule.ByPushover, rule.Hours, new NotificationReach(resolved.People, resolved.Deliveries.Count, resolved.Who, resolved.Why),
            rule.LastSentAt is { } at ? Instants.Utc(at) : null, rule.LastSentTo);
    }

    public async Task<IReadOnlyList<LinkedSensor>> SensorsAsync(Guid id, CancellationToken ct)
    {
        await EnsureExistsAsync(id, ct);
        var linked = await db.NotificationRuleSensor.AsNoTracking()
            .Where(l => l.NotificationRuleId == id)
            .OrderBy(l => l.Sensor.Parent.Name).ThenBy(l => l.Sensor.Name)
            .Select(l => new LinkedSensor(l.SensorId, l.Sensor.Name, l.Sensor.Type, l.Sensor.ParentId, l.Sensor.Parent.Name))
            .ToListAsync(ct);
        // v0.12 could link a sensor twice; it is listed once.
        return linked.DistinctBy(l => l.SensorId).ToList();
    }

    /// <summary>What the rule editor chooses from (§6.3): every user, active first, with whether they can be reached; every group with its size.</summary>
    public async Task<NotificationPeople> PeopleAsync(CancellationToken ct) => new(
        await db.Users.AsNoTracking().OrderByDescending(u => u.IsActive).ThenBy(u => u.DisplayName)
            .Select(u => new NotificationPerson(u.Id, u.Username, u.DisplayName, u.IsActive, u.Email != null && u.Email != "",
                u.PushoverKey != null && u.PushoverKey != ""))
            .ToListAsync(ct),
        await db.Groups.AsNoTracking().OrderBy(g => g.Name).Select(g => new NotificationGroupRef(g.Id, g.Name, g.Members.Count)).ToListAsync(ct));

    public async Task<NotificationRuleDetail> CreateAsync(NotificationRuleRequest request, CancellationToken ct)
    {
        var names = await ValidateAsync(request, ct);
        var rule = new NotificationRuleEntity { Id = Guid.NewGuid() };
        Apply(rule, request);
        db.NotificationRules.Add(rule);
        await db.SaveChangesAsync(ct);
        Summarise(rule, names);
        return await GetAsync(rule.Id, withSecrets: true, ct);
    }

    public async Task<NotificationRuleDetail> UpdateAsync(Guid id, NotificationRuleRequest request, CancellationToken ct)
    {
        var rule = await db.NotificationRules.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Notification rule");
        var names = await ValidateAsync(request, ct);
        Apply(rule, request);
        await db.SaveChangesAsync(ct);
        Summarise(rule, names);
        return await GetAsync(id, withSecrets: true, ct);
    }

    /// <summary>The rule, its sensor links and its notification records (ON DELETE CASCADE).</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var name = await db.NotificationRules.AsNoTracking().Where(r => r.Id == id).Select(r => r.Name).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Notification rule");
        // Deleted by ExecuteDelete below: the audit learns the rule here (docs/audit-redmine-zabbix-plan.md §3.3).
        AuditLog.Target(AuditTarget.Of(AuditTargets.NotificationRule, id, name));
        AuditLog.Summary($"{name} deleted");
        await db.NotificationRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Idempotent: an existing link stays as it is.</summary>
    public async Task LinkAsync(Guid id, Guid sensorId, CancellationToken ct)
    {
        await EnsureExistsAsync(id, ct);
        if (!await db.Sensors.AnyAsync(s => s.Id == sensorId, ct))
        {
            throw ApiException.NotFound("Sensor");
        }
        if (await db.NotificationRuleSensor.AnyAsync(l => l.NotificationRuleId == id && l.SensorId == sensorId, ct))
        {
            return;
        }
        db.NotificationRuleSensor.Add(new NotificationRuleSensorEntity { Id = Guid.NewGuid(), NotificationRuleId = id, SensorId = sensorId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Idempotent: unlinking what is not linked does nothing.</summary>
    public async Task UnlinkAsync(Guid id, Guid sensorId, CancellationToken ct)
    {
        await EnsureExistsAsync(id, ct);
        var names = await db.NotificationRules.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { Rule = r.Name, Sensor = db.Sensors.Where(s => s.Id == sensorId).Select(s => s.Parent.Name + " / " + s.Name).FirstOrDefault() })
            .SingleAsync(ct);
        AuditLog.Target(AuditTarget.Of(AuditTargets.NotificationRule, id, names.Rule, sensorId));
        AuditLog.Summary($"{names.Rule}: {names.Sensor ?? "a sensor"} unlinked");
        await db.NotificationRuleSensor.Where(l => l.NotificationRuleId == id && l.SensorId == sensorId).ExecuteDeleteAsync(ct);
    }

    private async Task EnsureExistsAsync(Guid id, CancellationToken ct)
    {
        if (!await db.NotificationRules.AnyAsync(r => r.Id == id, ct))
        {
            throw ApiException.NotFound("Notification rule");
        }
    }

    /// <summary>The names the request's user and group have, for the audit's summary.</summary>
    private sealed record Names(string? User, string? Group);

    /// <summary>
    /// The name and type as always; then by kind (§6.1): an address a provider takes; an existing, active user who can be reached
    /// by the rule's channels; an existing group; for the responsible, a stand-in that is a user or a group, not both. The person
    /// kinds need a channel.
    /// </summary>
    private async Task<Names> ValidateAsync(NotificationRuleRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var recipient = request.Recipient?.Trim();
        var v = new ApiValidator()
            .Check(name is { Length: >= 1 and <= 128 }, "name", "The name must have 1 to 128 characters.")
            .Check(Enum.IsDefined(request.Type), "type", "Unknown rule type.")
            .Check(Enum.IsDefined(request.RecipientKind), "recipientKind", "Address, User, Group or Responsible.");
        var kind = request.RecipientKind;
        var person = kind is NotificationRecipientKind.User or NotificationRecipientKind.Group or NotificationRecipientKind.Responsible;
        if (kind == NotificationRecipientKind.Address)
        {
            v.Check(recipient is { Length: <= 2000 } && NotificationProviders.Match(recipient) is not null, "recipient",
                "An e-mail address, pushover://<user key> or an http(s):// webhook.");
        }
        if (person)
        {
            v.Check(request.ByEmail || request.ByPushover, "byEmail", "Choose e-mail, Pushover or both.");
        }
        v.Check(kind != NotificationRecipientKind.Responsible || Enum.IsDefined(request.Hours), "hours", "Always or WorkingHours.");
        string? userName = null;
        string? groupName = null;
        var wantsUser = kind == NotificationRecipientKind.User || kind == NotificationRecipientKind.Responsible && request.UserId is not null;
        var wantsGroup = kind == NotificationRecipientKind.Group || kind == NotificationRecipientKind.Responsible && request.GroupId is not null;
        if (kind == NotificationRecipientKind.Responsible && request.UserId is not null && request.GroupId is not null)
        {
            v.Add("groupId", "A user or a group as the stand-in, not both.");
        }
        if (wantsUser)
        {
            var user = request.UserId is { } id
                ? await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => new { u.DisplayName, u.IsActive, u.Email, u.PushoverKey }).SingleOrDefaultAsync(ct)
                : null;
            if (user is null)
            {
                v.Add("userId", kind == NotificationRecipientKind.User ? "Choose a user." : "The stand-in user doesn't exist.");
            }
            else if (!user.IsActive)
            {
                v.Add("userId", $"{user.DisplayName} is deactivated.");
            }
            else
            {
                userName = user.DisplayName;
                var reachable = request.ByEmail && !string.IsNullOrWhiteSpace(user.Email) || request.ByPushover && !string.IsNullOrWhiteSpace(user.PushoverKey);
                // One person who can't be reached is a trap (decision 6); a group's membership changes, so a group may be empty.
                v.Check(kind != NotificationRecipientKind.User || reachable, "userId",
                    $"{user.DisplayName} has no {(request.ByEmail, request.ByPushover) switch
                    {
                        (true, true) => "e-mail address or Pushover key",
                        (true, false) => "e-mail address",
                        _ => "Pushover key",
                    }}.");
            }
        }
        if (wantsGroup)
        {
            groupName = request.GroupId is { } id ? await db.Groups.AsNoTracking().Where(g => g.Id == id).Select(g => g.Name).SingleOrDefaultAsync(ct) : null;
            if (groupName is null)
            {
                v.Add("groupId", kind == NotificationRecipientKind.Group ? "Choose a group." : "The stand-in group doesn't exist.");
            }
        }
        v.ThrowIfInvalid();
        return new Names(userName, groupName);
    }

    /// <summary>Sets the kind and the fields of that kind; the others are nulled, so a rule carries nothing of a kind it isn't.</summary>
    private static void Apply(NotificationRuleEntity rule, NotificationRuleRequest request)
    {
        rule.Name = request.Name!.Trim();
        rule.Type = request.Type;
        rule.Enabled = request.Enabled;
        rule.RecipientKind = request.RecipientKind;
        var kind = request.RecipientKind;
        rule.Recipient = kind == NotificationRecipientKind.Address ? request.Recipient!.Trim() : null;
        rule.UserId = kind is NotificationRecipientKind.User or NotificationRecipientKind.Responsible ? request.UserId : null;
        rule.GroupId = kind is NotificationRecipientKind.Group or NotificationRecipientKind.Responsible ? request.GroupId : null;
        var person = kind != NotificationRecipientKind.Address;
        rule.ByEmail = !person || request.ByEmail;
        rule.ByPushover = !person || request.ByPushover;
        rule.Hours = kind == NotificationRecipientKind.Responsible ? request.Hours : NotificationHours.Always;
    }

    /// <summary>The audit's summary names the person or group (§6.4), which the field changes show only as ids.</summary>
    private static void Summarise(NotificationRuleEntity rule, Names names)
    {
        var channels = NotificationRecipients.Channels(rule);
        switch (rule.RecipientKind)
        {
            case NotificationRecipientKind.User:
                AuditLog.Summary($"{rule.Name}: to {names.User} by {channels}");
                break;
            case NotificationRecipientKind.Group:
                AuditLog.Summary($"{rule.Name}: to {names.Group} by {channels}");
                break;
            case NotificationRecipientKind.Responsible:
                var standIn = names.User ?? names.Group;
                AuditLog.Summary($"{rule.Name}: to the responsible{(standIn is null ? ", no stand-in" : $", stand-in {standIn}")}, "
                    + $"{(rule.Hours == NotificationHours.WorkingHours ? "in working hours only" : "at any hour")}, by {channels}");
                break;
        }
    }
}
