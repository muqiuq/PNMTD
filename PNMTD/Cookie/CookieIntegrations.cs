using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Data;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Zabbix;

namespace PNMTD.Cookie;

/// <summary>What Cookie can do with Redmine now (docs/cookie-fallback-plan.md §3.1).</summary>
public enum RedmineAvailability { None, ReadOnly, Writable }

/// <summary>
/// One reading of what is available (docs/cookie-fallback-plan.md §3): Redmine as none, read-only or writable and Zabbix as
/// available or not, each with why not and since when; and whether Cookie has a project for its own tickets. The prompts,
/// the tools, the executor, the duties, the chat and the UI all read the same one, so they can't disagree.
/// </summary>
public sealed record IntegrationsReading(RedmineAvailability Redmine, string? RedmineReason, DateTime? RedmineSince, bool TicketProject,
    bool Zabbix, string? ZabbixReason, DateTime? ZabbixSince)
{
    /// <summary>Everything there: before the first reading, and for the tests.</summary>
    public static readonly IntegrationsReading Everything = new(RedmineAvailability.Writable, null, null, true, true, null, null);

    public bool RedmineReadable => Redmine != RedmineAvailability.None;

    public bool RedmineWritable => Redmine == RedmineAvailability.Writable;

    /// <summary>A ticket needs a writable Redmine and a project under Admin → Cookie → Redmine → Cookie's tickets.</summary>
    public bool CanTicket => RedmineWritable && TicketProject;

    /// <summary>Why there is no ticket: the executor's refusal, the duty's outcome.</summary>
    public string NoTicketReason => Redmine switch
    {
        RedmineAvailability.None => $"{RedmineReason}: no ticket; what matters goes by e-mail.",
        RedmineAvailability.ReadOnly => "A ticket needs Redmine__WriteApiKey and a project for Cookie's tickets.",
        _ => "A ticket needs a project for Cookie's tickets under Admin → Cookie → Redmine.",
    };

    /// <summary>Why there is no ticket, in a few words, for texts that go to people.</summary>
    public string NoTicketShort => Redmine switch
    {
        RedmineAvailability.None => RedmineReason ?? "Redmine isn't available",
        RedmineAvailability.ReadOnly => "Redmine__WriteApiKey is not set",
        _ => "no project for Cookie's tickets under Admin → Cookie → Redmine",
    };

    /// <summary>Why there is no note on a ticket.</summary>
    public string NoNoteReason => Redmine == RedmineAvailability.None ? $"{RedmineReason}: no note on a ticket." : "A note on a ticket needs Redmine__WriteApiKey.";

    /// <summary>The state for the API and the UI.</summary>
    public string RedmineState => Redmine switch
    {
        RedmineAvailability.None => "none",
        RedmineAvailability.ReadOnly => "readOnly",
        _ => "writable",
    };
}

/// <summary>
/// Reads what is available (docs/cookie-fallback-plan.md §3), once per round and whenever asked: the configuration, and
/// the reads the case book and the Zabbix poller do anyway, with a grace period so that one failed read never flips Cookie.
/// The first successful read makes an integration available again.
/// </summary>
public sealed class CookieIntegrations(IDbContextFactory<PnmtdDbContext> dbs, CookieSettings settings, IOptionsMonitor<RedmineOptions> redmine,
    IOptionsMonitor<ZabbixOptions> zabbix, ZabbixSnapshot snapshot)
{
    /// <summary>How long a failing integration still counts as available (§3.3).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    private volatile IntegrationsReading? current;

    /// <summary>The last reading, for the tools' <c>Available</c> (§4.3); from the configuration alone before the first one.</summary>
    public IntegrationsReading Current => current ?? Read(redmine.CurrentValue, zabbix.CurrentValue, snapshot.Current, null, true, Clock.UtcNow);

    public async Task<IntegrationsReading> ReadAsync(CancellationToken ct)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var runtime = await db.CookieRuntime.AsNoTracking().SingleOrDefaultAsync(r => r.SingletonId == CookieRuntimeEntity.Id, ct);
        var project = (await settings.GetAsync<CookieRedmineSettings>(ct)).TicketProjectId is not null;
        return current = Read(redmine.CurrentValue, zabbix.CurrentValue, snapshot.Current, runtime, project, Clock.UtcNow);
    }

    /// <summary>The reading from its inputs (§3.1), without I/O.</summary>
    public static IntegrationsReading Read(RedmineOptions redmine, ZabbixOptions zabbix, ZabbixSnapshot.State state, CookieRuntimeEntity? runtime, bool ticketProject,
        DateTime now)
    {
        RedmineAvailability r;
        string? why = null;
        DateTime? since = null;
        if (!redmine.Enabled)
        {
            r = RedmineAvailability.None;
            why = "Redmine isn't configured (Redmine__Url, Redmine__ApiKey, Redmine__Projects)";
        }
        else if (runtime?.CaseBookError is { } error && error.StartsWith("Redmine:", StringComparison.Ordinal)
            && (runtime.TicketsAt is null || now - runtime.TicketsAt > Grace))
        {
            r = RedmineAvailability.None;
            since = runtime.TicketsAt ?? runtime.CaseBookAt;
            why = $"Redmine is unreachable since {When(since)} ({error["Redmine:".Length..].Trim().TrimEnd('.')})";
        }
        else if (!redmine.CanWrite)
        {
            r = RedmineAvailability.ReadOnly;
            why = "Redmine__WriteApiKey is not set: Redmine can be read, not written";
        }
        else
        {
            r = RedmineAvailability.Writable;
        }

        bool z;
        string? zWhy = null;
        DateTime? zSince = null;
        if (!zabbix.Enabled)
        {
            z = false;
            zWhy = "Zabbix isn't configured (Zabbix__Url, Zabbix__ApiToken)";
        }
        else if (state.Error is { } e && (state.AsOf is null || now - state.AsOf > Grace))
        {
            z = false;
            zSince = state.AsOf ?? state.ErrorAt;
            zWhy = $"Zabbix is unreachable since {When(zSince)} ({e.Trim().TrimEnd('.')})";
        }
        else
        {
            z = true;
        }
        return new IntegrationsReading(r, why, since, ticketProject, z, zWhy, zSince);
    }

    private static string When(DateTime? utc) => utc is { } t ? Clock.ToLocal(t).ToString("d MMM HH:mm", CultureInfo.InvariantCulture) : "the start";
}
