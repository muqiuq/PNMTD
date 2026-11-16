using PNMTD.Cookie;
using PNMTD.Models.Db;
using PNMTD.Redmine;
using PNMTD.Zabbix;

namespace PNMTD.Tests.Cookie;

/// <summary>
/// What Cookie counts as available (docs/cookie-fallback-plan.md §3, §4.1, §4.2, §5.1): Redmine as none, read-only or writable
/// from its configuration and the case book's reads, Zabbix from its configuration and the poller's snapshot, each with a grace
/// period; and the texts that follow from it, which the acting tests check against the prompts with the real settings.
/// </summary>
[TestClass]
public sealed class IntegrationsTests
{
    private static readonly RedmineOptions NoRedmine = new();
    private static readonly RedmineOptions ReadOnlyRedmine = new() { Url = "https://redmine.example", ApiKey = "k", Projects = "1" };
    private static readonly RedmineOptions WritableRedmine = new() { Url = "https://redmine.example", ApiKey = "k", Projects = "1", WriteApiKey = "w" };
    private static readonly ZabbixOptions NoZabbix = new();
    private static readonly ZabbixOptions Zabbix = new() { Url = "https://zabbix.example", ApiToken = "t" };
    private static readonly DateTime Now = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);
    private static readonly ZabbixSnapshot.State Fresh = new([], Now.AddMinutes(-1), null, null);

    [TestMethod]
    public void Redmine_is_none_read_only_or_writable_by_its_configuration()
    {
        var none = CookieIntegrations.Read(NoRedmine, Zabbix, Fresh, null, true, Now);
        Assert.AreEqual(RedmineAvailability.None, none.Redmine);
        Assert.IsFalse(none.RedmineReadable);
        Assert.IsFalse(none.CanTicket);
        StringAssert.Contains(none.RedmineReason, "isn't configured");
        StringAssert.Contains(none.NoTicketReason, "what matters goes by e-mail");

        var readOnly = CookieIntegrations.Read(ReadOnlyRedmine, Zabbix, Fresh, null, true, Now);
        Assert.AreEqual(RedmineAvailability.ReadOnly, readOnly.Redmine);
        Assert.IsTrue(readOnly.RedmineReadable);
        Assert.IsFalse(readOnly.RedmineWritable);
        Assert.AreEqual("A ticket needs Redmine__WriteApiKey and a project for Cookie's tickets.", readOnly.NoTicketReason, "the text an install knows");

        var writable = CookieIntegrations.Read(WritableRedmine, Zabbix, Fresh, null, true, Now);
        Assert.AreEqual(RedmineAvailability.Writable, writable.Redmine);
        Assert.IsTrue(writable.CanTicket);
        Assert.IsNull(writable.RedmineReason);

        var noProject = CookieIntegrations.Read(WritableRedmine, Zabbix, Fresh, null, false, Now);
        Assert.IsTrue(noProject.RedmineWritable, "notes on tickets need no project");
        Assert.IsFalse(noProject.CanTicket, "a ticket does");
        StringAssert.Contains(noProject.NoTicketReason, "a project for Cookie's tickets");
    }

    [TestMethod]
    public void A_failing_Redmine_counts_until_the_grace_period_is_over_and_the_first_read_brings_it_back()
    {
        var runtime = new CookieRuntimeEntity { CaseBookError = "Redmine: 502 Bad Gateway.", TicketsAt = Now.AddMinutes(-29), CaseBookAt = Now };
        Assert.AreEqual(RedmineAvailability.Writable, CookieIntegrations.Read(WritableRedmine, Zabbix, Fresh, runtime, true, Now).Redmine, "a blip within the grace period");

        runtime.TicketsAt = Now.AddMinutes(-31);
        var gone = CookieIntegrations.Read(WritableRedmine, Zabbix, Fresh, runtime, true, Now);
        Assert.AreEqual(RedmineAvailability.None, gone.Redmine);
        StringAssert.Contains(gone.RedmineReason, "unreachable since");
        StringAssert.Contains(gone.RedmineReason, "502 Bad Gateway");
        Assert.IsFalse(gone.RedmineReason!.Contains("Redmine: 502", StringComparison.Ordinal), "the error's own prefix isn't repeated");
        Assert.AreEqual(runtime.TicketsAt, gone.RedmineSince);

        var never = new CookieRuntimeEntity { CaseBookError = "Redmine: no route to host", TicketsAt = null, CaseBookAt = Now };
        Assert.AreEqual(RedmineAvailability.None, CookieIntegrations.Read(WritableRedmine, Zabbix, Fresh, never, true, Now).Redmine, "never read: gone at once");

        var other = new CookieRuntimeEntity { CaseBookError = "Zabbix: something else", TicketsAt = Now.AddHours(-2) };
        Assert.AreEqual(RedmineAvailability.Writable, CookieIntegrations.Read(WritableRedmine, Zabbix, Fresh, other, true, Now).Redmine, "another error isn't Redmine's");

        var back = new CookieRuntimeEntity { CaseBookError = null, TicketsAt = Now };
        Assert.AreEqual(RedmineAvailability.Writable, CookieIntegrations.Read(WritableRedmine, Zabbix, Fresh, back, true, Now).Redmine, "the first successful read");
        Assert.IsTrue(CookieIntegrations.Grace == TimeSpan.FromMinutes(30), "the plan's grace period (§3.3)");
    }

    [TestMethod]
    public void Zabbix_follows_its_configuration_and_the_snapshot()
    {
        Assert.IsFalse(CookieIntegrations.Read(WritableRedmine, NoZabbix, Fresh, null, true, Now).Zabbix);
        StringAssert.Contains(CookieIntegrations.Read(WritableRedmine, NoZabbix, Fresh, null, true, Now).ZabbixReason, "isn't configured");
        Assert.IsTrue(CookieIntegrations.Read(WritableRedmine, Zabbix, Fresh, null, true, Now).Zabbix);

        var blip = new ZabbixSnapshot.State([], Now.AddMinutes(-10), "timeout", Now.AddMinutes(-1));
        Assert.IsTrue(CookieIntegrations.Read(WritableRedmine, Zabbix, blip, null, true, Now).Zabbix, "within the grace period");

        var gone = CookieIntegrations.Read(WritableRedmine, Zabbix, new ZabbixSnapshot.State([], Now.AddMinutes(-40), "timeout", Now.AddMinutes(-1)), null, true, Now);
        Assert.IsFalse(gone.Zabbix);
        StringAssert.Contains(gone.ZabbixReason, "unreachable since");
        Assert.AreEqual(Now.AddMinutes(-40), gone.ZabbixSince);

        var neverPolled = CookieIntegrations.Read(WritableRedmine, Zabbix, new ZabbixSnapshot.State([], null, "refused", Now), null, true, Now);
        Assert.IsFalse(neverPolled.Zabbix, "never polled and failing: gone at once");
        Assert.IsTrue(CookieIntegrations.Read(WritableRedmine, Zabbix, new ZabbixSnapshot.State([], null, null, null), null, true, Now).Zabbix,
            "not polled yet and no error: nothing says it is gone");
    }

    [TestMethod]
    public void The_fixed_texts_name_only_what_is_missing()
    {
        Assert.AreEqual("", CookieBrain.Missing(IntegrationsReading.Everything));

        var none = CookieIntegrations.Read(NoRedmine, NoZabbix, Fresh, null, false, Now);
        var missing = CookieBrain.Missing(none);
        StringAssert.Contains(missing, "don't look for tickets");
        StringAssert.Contains(missing, "don't look for Zabbix problems or acknowledgements");

        var readOnly = CookieIntegrations.Read(ReadOnlyRedmine, Zabbix, Fresh, null, true, Now);
        Assert.AreEqual("", CookieBrain.Missing(readOnly), "tickets can be read: nothing to leave out");
        StringAssert.Contains(CookieBrain.NoTicketSystem(readOnly), "There is no ticket system you can write to now: Redmine__WriteApiKey is not set");
        StringAssert.Contains(CookieBrain.NoTicketSystem(readOnly), "Don't propose tickets or notes on tickets");
        StringAssert.Contains(CookieBrain.NoTicketSystem(readOnly), "e-mail to whoever is responsible");

        var noProject = CookieIntegrations.Read(WritableRedmine, Zabbix, Fresh, null, false, Now);
        StringAssert.Contains(CookieBrain.NoTicketSystem(noProject), "no project for your own tickets");
        Assert.IsFalse(CookieBrain.NoTicketSystem(noProject).Contains("notes on tickets", StringComparison.Ordinal), "notes stay possible");

        Assert.IsTrue(CookieBrain.Possible(CookieActionKind.PrivateNote, noProject));
        Assert.IsFalse(CookieBrain.Possible(CookieActionKind.Ticket, noProject));
        Assert.IsFalse(CookieBrain.Possible(CookieActionKind.PublicAnswer, readOnly));
        Assert.IsTrue(CookieBrain.Possible(CookieActionKind.Pushover, none));
        Assert.IsTrue(CookieBrain.Possible(CookieActionKind.Email, none));

        StringAssert.Contains(CookieBrain.WithMissing("The instructions.", none), "The instructions.\n\n");
        Assert.AreEqual("The instructions.", CookieBrain.WithMissing("The instructions.", IntegrationsReading.Everything));
    }
}
