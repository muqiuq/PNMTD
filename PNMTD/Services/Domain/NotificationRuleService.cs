using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
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
}

public sealed class NotificationRuleService(PnmtdDbContext db)
{
    private static readonly string[] SortFields = ["name", "type", "recipient"];

    /// <summary>Sort: name (default), type, recipient.</summary>
    public async Task<Paged<NotificationRuleItem>> ListAsync(PageQuery query, bool withSecrets, CancellationToken ct)
    {
        var (field, descending) = Paging.Sort(query.Sort, "name", SortFields);
        var pattern = Text.Pattern(query.Search);
        var rules = db.NotificationRules.AsNoTracking();
        if (pattern is not null)
        {
            rules = rules.Where(r => EF.Functions.ILike(r.Name, pattern, @"\") || EF.Functions.ILike(r.Recipient, pattern, @"\"));
        }
        rules = (field, descending) switch
        {
            ("type", false) => rules.OrderBy(r => r.Type).ThenBy(r => r.Name),
            ("type", true) => rules.OrderByDescending(r => r.Type).ThenBy(r => r.Name),
            ("recipient", false) => rules.OrderBy(r => r.Recipient).ThenBy(r => r.Name),
            ("recipient", true) => rules.OrderByDescending(r => r.Recipient).ThenBy(r => r.Name),
            (_, true) => rules.OrderByDescending(r => r.Name),
            _ => rules.OrderBy(r => r.Name),
        };
        var page = await Paging.PageAsync(rules.Select(r => new RuleRow(r.Id, r.Name, r.Recipient, r.Type, r.Enabled, r.SubscribedSensors.Count)),
            query, ct);
        return new Paged<NotificationRuleItem>(page.Items.Select(r => ToItem(r, withSecrets)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    private sealed record RuleRow(Guid Id, string Name, string Recipient, NotificationRuleType Type, bool Enabled, int SensorCount);

    private static NotificationRuleItem ToItem(RuleRow r, bool withSecrets)
    {
        var provider = NotificationProviders.Match(r.Recipient) ?? "unknown";
        return new NotificationRuleItem(r.Id, r.Name, withSecrets ? r.Recipient : NotificationProviders.Mask(r.Recipient, provider),
            provider, r.Type, r.Enabled, r.SensorCount);
    }

    public async Task<IReadOnlyList<NotificationRuleItem>> ForSensorAsync(Guid sensorId, bool withSecrets, CancellationToken ct)
    {
        var rows = await db.NotificationRules.AsNoTracking()
            .Where(r => r.SubscribedSensors.Any(l => l.SensorId == sensorId))
            .OrderBy(r => r.Name)
            .Select(r => new RuleRow(r.Id, r.Name, r.Recipient, r.Type, r.Enabled, r.SubscribedSensors.Count))
            .ToListAsync(ct);
        return rows.Select(r => ToItem(r, withSecrets)).ToList();
    }

    public async Task<NotificationRuleDetail> GetAsync(Guid id, bool withSecrets, CancellationToken ct)
    {
        var rule = await db.NotificationRules.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct)
            ?? throw ApiException.NotFound("Notification rule");
        var provider = NotificationProviders.Match(rule.Recipient) ?? "unknown";
        return new NotificationRuleDetail(rule.Id, rule.Name,
            withSecrets ? rule.Recipient : NotificationProviders.Mask(rule.Recipient, provider), provider, rule.Type, rule.Enabled,
            await SensorsAsync(id, ct));
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

    public async Task<NotificationRuleDetail> CreateAsync(NotificationRuleRequest request, CancellationToken ct)
    {
        Validate(request);
        var rule = new NotificationRuleEntity { Id = Guid.NewGuid() };
        Apply(rule, request);
        db.NotificationRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return await GetAsync(rule.Id, withSecrets: true, ct);
    }

    public async Task<NotificationRuleDetail> UpdateAsync(Guid id, NotificationRuleRequest request, CancellationToken ct)
    {
        var rule = await db.NotificationRules.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound("Notification rule");
        Validate(request);
        Apply(rule, request);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, withSecrets: true, ct);
    }

    /// <summary>The rule, its sensor links and its notification records (ON DELETE CASCADE).</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        if (await db.NotificationRules.Where(r => r.Id == id).ExecuteDeleteAsync(ct) == 0)
        {
            throw ApiException.NotFound("Notification rule");
        }
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
        await db.NotificationRuleSensor.Where(l => l.NotificationRuleId == id && l.SensorId == sensorId).ExecuteDeleteAsync(ct);
    }

    private async Task EnsureExistsAsync(Guid id, CancellationToken ct)
    {
        if (!await db.NotificationRules.AnyAsync(r => r.Id == id, ct))
        {
            throw ApiException.NotFound("Notification rule");
        }
    }

    private static void Validate(NotificationRuleRequest request)
    {
        var name = request.Name?.Trim();
        var recipient = request.Recipient?.Trim();
        new ApiValidator()
            .Check(name is { Length: >= 1 and <= 128 }, "name", "The name must have 1 to 128 characters.")
            .Check(Enum.IsDefined(request.Type), "type", "Unknown rule type.")
            .Check(recipient is { Length: <= 2000 } && NotificationProviders.Match(recipient) is not null, "recipient",
                "An e-mail address, pushover://<user key> or an http(s):// webhook.")
            .ThrowIfInvalid();
    }

    private static void Apply(NotificationRuleEntity rule, NotificationRuleRequest request)
    {
        rule.Name = request.Name!.Trim();
        rule.Recipient = request.Recipient!.Trim();
        rule.Type = request.Type;
        rule.Enabled = request.Enabled;
    }
}
