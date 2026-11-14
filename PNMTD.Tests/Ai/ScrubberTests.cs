using PNMTD.Ai;

namespace PNMTD.Tests.Ai;

/// <summary>
/// The scrubber (docs/ai-operations-plan.md §9.1), which the knowledge base's AI tags use since the situation analysis went
/// (docs/cookie-plan.md §12): values replaced consistently, numbers and codes kept, phone numbers when asked.
/// </summary>
[TestClass]
public sealed class ScrubberTests
{
    [TestMethod]
    public void The_scrubber_replaces_values_consistently_and_keeps_numbers()
    {
        var scrubber = new Scrubber();
        scrubber.Name("nas-zurich-01", "H1");
        var text = scrubber.Scrub("Backup to 10.1.2.3 failed on NAS-Zurich-01 (fe80::1, 00:11:22:33:44:55) for admin@example.com via " +
            "https://files.example.com/a?b=1 and nas.example.com, job 3f2a9c1e-1111-4222-8333-444455556666, key ABCD1234EFGH5678IJ, code 500 at 12:30:45, " +
            "retry 3 of 10.1.2.3");
        Assert.AreEqual("Backup to <ip:2> failed on H1 (<ip:1>, <mac:1>) for <mail:1> via <url:1> and <host:1>, job <id:1>, key <token:1>, " +
            "code 500 at 12:30:45, retry 3 of <ip:2>", text);
        Assert.AreEqual("10.1.2.3", scrubber.Values["<ip:2>"]);
        Assert.AreEqual("nas.example.com", scrubber.Values["<host:1>"]);
    }

    [TestMethod]
    public void The_scrubber_replaces_phone_numbers_when_asked_and_keeps_dates_and_versions()
    {
        const string text = "Call Anna on +41 44 123 45 67 or 044 123 45 67, the desk (044) 765-43-21, Berlin 030/1234567, Zurich 0041 44 222 33 44. " +
            "Not on 01/02/2026, 2026-11-14, 12:30:45, v0.12.1, port 8080, 10.1.2.3 or 3 of 10.";
        Assert.AreEqual("Call Anna on <phone:1> or <phone:2>, the desk <phone:3>, Berlin <phone:4>, Zurich <phone:5>. " +
            "Not on 01/02/2026, 2026-11-14, 12:30:45, v0.12.1, port 8080, <ip:1> or 3 of 10.", new Scrubber { PhoneNumbers = true }.Scrub(text));
        StringAssert.Contains(new Scrubber().Scrub(text), "+41 44 123 45 67", "numbers stay unless asked");
    }
}
