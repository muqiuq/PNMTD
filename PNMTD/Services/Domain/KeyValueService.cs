using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Models.Db;
using PNMTD.Models.Enums;

namespace PNMTD.Services.Domain;

/// <summary>Key values (admins): task state written by the background tasks, and settings they read.</summary>
public sealed class KeyValueService(PnmtdDbContext db)
{
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        [Key(KeyValueKeyEnums.LAST_HEARTBEAT_TASK_RUN)] = "When the heartbeat check last ran.",
        [Key(KeyValueKeyEnums.NUM_OF_HEARTBEAT_SENSORS_PROCESSED)] = "Heartbeat sensors the last heartbeat check looked at.",
        [Key(KeyValueKeyEnums.LAST_NOTIFICATION_TASK_RUN)] = "When the notification task last ran.",
        [Key(KeyValueKeyEnums.LAST_PING_TASK_RUN)] = "When the ping check last ran.",
        [Key(KeyValueKeyEnums.NUM_OF_SUCCESSFULL_PINGS)] = "Successful pings in the last ping check.",
        [Key(KeyValueKeyEnums.NUM_OF_FAILED_PINGS)] = "Failed pings in the last ping check.",
        [Key(KeyValueKeyEnums.LAST_MAILBOX_CHECK)] = "When the IMAP mailbox was last read.",
        [Key(KeyValueKeyEnums.LAST_MAIL_PROCESSING)] = "When the mail input rules were last applied.",
        [Key(KeyValueKeyEnums.LAST_TIMESPAN_CONDITIONS_CHECK_TASK_RUN)] = "When the timespan sensors were last evaluated.",
        [Key(KeyValueKeyEnums.LAST_UPLINK_CHECK)] = "When the uplink was last checked.",
        [Key(KeyValueKeyEnums.LAST_UPLINK_CHECKED_LINK)] = "The uplink URL checked last.",
        [Key(KeyValueKeyEnums.LAST_UPLINK_CHECK_SUCCESSFULL)] = "When an uplink check last succeeded.",
        [Key(KeyValueKeyEnums.UPLINK_OK)] = "Whether the uplink is up. While it is down, heartbeats are not checked.",
        [Key(KeyValueKeyEnums.MAILBOX_DEFAULT_DELETE_ARCHIVED_MESSAGES_OLDER_THEN_DAYS)] = "Processed mail older than this many days is deleted from the mailbox.",
        [Key(KeyValueKeyEnums.MAILBOX_DEFAULT_MARGIN_IN_DAYS)] = "Mail older than this many days is not read.",
        [Key(KeyValueKeyEnums.LAST_DNS_CHECK)] = "When the DNS check last ran.",
        [Key(KeyValueKeyEnums.DNS_SERVERS)] = "The resolvers the DNS check asks, comma-separated IP addresses.",
    };

    private static string Key(KeyValueKeyEnums key) => KeyValueDbExtensions.KeyOf(key);

    private static KeyValueItem ToItem(KeyValueEntity k) =>
        new(k.Id, k.Key, k.Value, k.Note, k.Enabled, k.IsReadOnly, Descriptions.GetValueOrDefault(k.Key));

    /// <summary>Sorted by key. Search: key, value and note.</summary>
    public async Task<Paged<KeyValueItem>> ListAsync(PageQuery query, CancellationToken ct)
    {
        var values = db.KeyValues.AsNoTracking();
        if (Text.Pattern(query.Search) is { } pattern)
        {
            values = values.Where(k => EF.Functions.ILike(k.Key, pattern, @"\") || EF.Functions.ILike(k.Value, pattern, @"\")
                || EF.Functions.ILike(k.Note, pattern, @"\"));
        }
        var page = await Paging.PageAsync(values.OrderBy(k => k.Key), query, ct);
        return new Paged<KeyValueItem>(page.Items.Select(ToItem).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<KeyValueItem> GetAsync(Guid id, CancellationToken ct) =>
        ToItem(await db.KeyValues.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw ApiException.NotFound("Key value"));

    public async Task<KeyValueItem> CreateAsync(KeyValueRequest request, CancellationToken ct)
    {
        var key = Validate(request);
        if (await db.KeyValues.AnyAsync(k => k.Key == key, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"The key {key} exists already.");
        }
        var entry = new KeyValueEntity { Id = Guid.NewGuid(), Key = key, IsReadOnly = false };
        Apply(entry, request);
        db.KeyValues.Add(entry);
        await db.SaveChangesAsync(ct);
        return ToItem(entry);
    }

    public async Task<KeyValueItem> UpdateAsync(Guid id, KeyValueRequest request, CancellationToken ct)
    {
        var entry = await db.KeyValues.SingleOrDefaultAsync(k => k.Id == id, ct) ?? throw ApiException.NotFound("Key value");
        if (entry.IsReadOnly)
        {
            throw ApiException.Conflict(ApiErrorCodes.ReadOnly, "This entry is written by a background task.");
        }
        var key = Validate(request);
        if (key != entry.Key && await db.KeyValues.AnyAsync(k => k.Key == key, ct))
        {
            throw ApiException.Conflict(ApiErrorCodes.DuplicateKey, $"The key {key} exists already.");
        }
        entry.Key = key;
        Apply(entry, request);
        await db.SaveChangesAsync(ct);
        return ToItem(entry);
    }

    /// <summary>Read-only entries too: the task writes them again on its next run.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var key = await db.KeyValues.AsNoTracking().Where(k => k.Id == id).Select(k => k.Key).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("Key value");
        // Deleted by ExecuteDelete below: the audit learns the key here (docs/audit-redmine-zabbix-plan.md §3.3).
        AuditLog.Target(AuditTarget.Of(AuditTargets.KeyValue, id, key));
        AuditLog.Summary($"{key} deleted");
        await db.KeyValues.Where(k => k.Id == id).ExecuteDeleteAsync(ct);
    }

    private static string Validate(KeyValueRequest request)
    {
        var key = request.Key?.Trim().ToLowerInvariant();
        new ApiValidator()
            .Check(key is { Length: >= 1 and <= 128 }, "key", "The key must have 1 to 128 characters.")
            .Check(request.Value is not null && request.Value.Length <= 4000, "value", "A value of at most 4000 characters.")
            .Check(request.Note is null || request.Note.Length <= 1000, "note", "At most 1000 characters.")
            .ThrowIfInvalid();
        return key!;
    }

    private static void Apply(KeyValueEntity entry, KeyValueRequest request)
    {
        entry.Value = request.Value!;
        entry.Note = request.Note ?? "";
        entry.Enabled = request.Enabled;
    }
}
