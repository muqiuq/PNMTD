using PNMTD.Licenses;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;

namespace PNMTD.Tests.Licenses;

/// <summary>
/// An item's state (docs/licences-plan.md §4), pure: the codes and messages per kind, the precedence, the days, the end of life, the seats,
/// automatic renewals and their notice, cancelled and retired items, the keys, and 246 once after a renewal.
/// </summary>
[TestClass]
public sealed class LicenseVerdictTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    private static LicenseFacts Facts(LicenseKind kind = LicenseKind.Subscription, LicenseType type = LicenseType.License, DateOnly? expires = null,
        LicenseRenewal renewal = LicenseRenewal.Manual, int warn = 30, int? notice = null, bool warnBeforeRenewal = false, DateOnly? eol = null,
        string? eolNote = null, int eolWarn = 90, bool fromHosts = false, int? seats = null, int used = 0, LicenseStatus status = LicenseStatus.Active,
        DateOnly? announced = null, bool lastAutomatic = false) =>
        new(type, kind, 12, "M365 Business Standard", status, expires, renewal, warn, notice, warnBeforeRenewal, eol, eolNote, eolWarn, fromHosts, seats, used,
            "devices", announced, lastAutomatic);

    [TestMethod]
    public void ValidUntilItsExpiryAndExpiredTheDayAfter()
    {
        var expires = new DateOnly(2027, 3, 31);
        var ok = LicenseVerdict.Evaluate(Facts(expires: expires), Today)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, ok.Code);
        Assert.AreEqual("Subscription valid until 31 Mar 2027 · M365 Business Standard (LIC-12)", ok.Message);
        Assert.AreEqual(PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE, LicenseVerdict.Evaluate(Facts(expires: expires), expires.AddDays(-30))!.Code);
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, LicenseVerdict.Evaluate(Facts(expires: expires), expires.AddDays(-31))!.Code);
        var last = LicenseVerdict.Evaluate(Facts(expires: expires), expires)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE, last.Code, "valid through the day it expires");
        var expired = LicenseVerdict.Evaluate(Facts(expires: expires), expires.AddDays(1))!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_EXPIRED, expired.Code);
        Assert.AreEqual("Subscription ended on 31 Mar 2027 · M365 Business Standard (LIC-12)", expired.Message);
    }

    [TestMethod]
    public void NoWarningDaysWarnsOnlyOnTheLastDay()
    {
        var expires = Today.AddDays(1);
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, LicenseVerdict.Evaluate(Facts(expires: expires, warn: 0), Today)!.Code);
        var last = LicenseVerdict.Evaluate(Facts(expires: expires, warn: 0), expires)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE, last.Code);
        StringAssert.Contains(last.Message, "today is its last day");
    }

    [TestMethod]
    public void TheKindWordsTheMessages()
    {
        var expires = Today.AddDays(10);
        StringAssert.StartsWith(LicenseVerdict.Evaluate(Facts(LicenseKind.Purchase, LicenseType.Hardware, expires), Today)!.Message, "Warranty ends on");
        StringAssert.StartsWith(LicenseVerdict.Evaluate(Facts(LicenseKind.Trial, expires: expires), Today)!.Message, "Trial ends on");
        StringAssert.StartsWith(LicenseVerdict.Evaluate(Facts(LicenseKind.Lease, LicenseType.Hardware, Today.AddDays(-1)), Today)!.Message, "Lease ended on");
        StringAssert.StartsWith(LicenseVerdict.Evaluate(Facts(LicenseKind.Perpetual, expires: null, renewal: LicenseRenewal.None), Today)!.Message, "Doesn't expire");
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, LicenseVerdict.Evaluate(Facts(LicenseKind.Free, expires: null, renewal: LicenseRenewal.None), Today)!.Code,
            "an item without dates or seats is OK for ever");
        var hw = LicenseVerdict.Evaluate(Facts(LicenseKind.Purchase, LicenseType.Hardware, Today.AddYears(1), LicenseRenewal.None), Today)!;
        StringAssert.Contains(hw.Message, "(HW-12)");
    }

    [TestMethod]
    public void AnAutomaticRenewalWarnsOnlyOnRequestBeforeItsDeadline()
    {
        var expires = new DateOnly(2027, 6, 30);
        var quiet = LicenseVerdict.Evaluate(Facts(LicenseKind.Contract, LicenseType.Service, expires, LicenseRenewal.Automatic, notice: 90), new DateOnly(2027, 6, 1))!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, quiet.Code, "it renews by itself");
        StringAssert.StartsWith(quiet.Message, "Contract renews automatically on 1 Jul 2027");
        var facts = Facts(LicenseKind.Contract, LicenseType.Service, expires, LicenseRenewal.Automatic, notice: 90, warnBeforeRenewal: true);
        Assert.AreEqual(new DateOnly(2027, 4, 1), facts.CancelBy);
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, LicenseVerdict.Evaluate(facts, new DateOnly(2027, 3, 1))!.Code, "31 days before the deadline");
        var warned = LicenseVerdict.Evaluate(facts, new DateOnly(2027, 3, 2))!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE, warned.Code);
        Assert.AreEqual("Renews automatically on 1 Jul 2027 unless cancelled by 1 Apr 2027 · M365 Business Standard (SVC-12)", warned.Message);
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, LicenseVerdict.Evaluate(facts, new DateOnly(2027, 4, 2))!.Code, "past the deadline the renewal is certain");
    }

    [TestMethod]
    public void ACancelledItemWarnsAndExpiresAsOneThatDoesNotRenew()
    {
        var expires = Today.AddDays(5);
        var cancelled = LicenseVerdict.Evaluate(Facts(LicenseKind.Contract, LicenseType.Service, expires, LicenseRenewal.Automatic, status: LicenseStatus.Cancelled), Today)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE, cancelled.Code);
        StringAssert.StartsWith(cancelled.Message, "Ends on ");
        StringAssert.Contains(cancelled.Message, "(cancelled)");
        Assert.IsNull(LicenseVerdict.Evaluate(Facts(status: LicenseStatus.Retired, expires: Today.AddDays(-5)), Today), "a retired item has no verdict");
    }

    [TestMethod]
    public void TheEndOfLifeAndTheSeats()
    {
        var soon = LicenseVerdict.Evaluate(Facts(LicenseKind.Perpetual, renewal: LicenseRenewal.None, eol: Today.AddDays(90), eolNote: "end of extended support"), Today)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_EOL_SOON, soon.Code);
        StringAssert.Contains(soon.Message, "reaches its end of life (end of extended support) on");
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK,
            LicenseVerdict.Evaluate(Facts(LicenseKind.Perpetual, renewal: LicenseRenewal.None, eol: Today.AddDays(91)), Today)!.Code);
        var past = LicenseVerdict.Evaluate(Facts(LicenseKind.Subscription, LicenseType.Service, Today.AddYears(1), eol: Today.AddDays(-1)), Today)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_EOL, past.Code);
        StringAssert.Contains(past.Message, "end of service", "a service's end of life is its end of service");
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, LicenseVerdict.Evaluate(Facts(fromHosts: true, seats: 5, used: 5, renewal: LicenseRenewal.None), Today)!.Code);
        var over = LicenseVerdict.Evaluate(Facts(fromHosts: true, seats: 5, used: 6, renewal: LicenseRenewal.None), Today)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OVER_ASSIGNED, over.Code);
        StringAssert.StartsWith(over.Message, "Over-assigned: 6 of 5 devices");
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, LicenseVerdict.Evaluate(Facts(fromHosts: false, seats: 5, used: 9, renewal: LicenseRenewal.None), Today)!.Code,
            "seats not counted on hosts are never over");
    }

    [TestMethod]
    public void TheWorstFindingFirstWithTheOthersAndTheirKey()
    {
        var f = Facts(expires: Today.AddDays(-1), eol: Today.AddDays(10), fromHosts: true, seats: 1, used: 2);
        var v = LicenseVerdict.Evaluate(f, Today)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_EXPIRED, v.Code);
        CollectionAssert.AreEqual(new[] { 446, 449, 447 }, v.Findings.Select(x => x.Code).ToArray());
        StringAssert.Contains(v.Message, "; also over-assigned: 2 of 1 devices; M365 Business Standard reaches its end of life");
        StringAssert.EndsWith(v.Key, "|also 449,447");
        var eolPast = LicenseVerdict.Evaluate(Facts(expires: Today.AddDays(5), eol: Today.AddDays(-1)), Today)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_EOL, eolPast.Code, "448 before 445");
        Assert.AreNotEqual(LicenseVerdict.Evaluate(Facts(fromHosts: true, seats: 1, used: 2), Today)!.Key,
            LicenseVerdict.Evaluate(Facts(fromHosts: true, seats: 1, used: 3), Today)!.Key, "a new count is a new event");
    }

    [TestMethod]
    public void ARenewalShows246Once()
    {
        var renewed = LicenseVerdict.Evaluate(Facts(expires: new DateOnly(2027, 12, 31), announced: new DateOnly(2026, 12, 31)), Today)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_RENEWED, renewed.Code);
        Assert.AreEqual("Renewed until 31 Dec 2027 (was 31 Dec 2026) · M365 Business Standard (LIC-12)", renewed.Message);
        var again = LicenseVerdict.Evaluate(Facts(expires: new DateOnly(2027, 12, 31), announced: new DateOnly(2027, 12, 31)), Today)!;
        Assert.AreEqual(PNMTStatusCodes.LICENSE_OK, again.Code);
        Assert.AreEqual(renewed.Key, again.Key, "the same key: no new event, so the state stays 246");
        StringAssert.StartsWith(LicenseVerdict.Evaluate(Facts(expires: new DateOnly(2027, 12, 31), announced: new DateOnly(2026, 12, 31), lastAutomatic: true),
            Today)!.Message, "Renewed automatically until");
    }

    [TestMethod]
    public void TheOwnersNoticeKeyRemindsOnce()
    {
        var expires = Today.AddDays(20);
        var v = LicenseVerdict.Evaluate(Facts(expires: expires), Today)!;
        Assert.AreEqual(v.Key, LicenseEvaluator.NoticeKey(v, false, Today, 7, expires));
        Assert.AreEqual($"{v.Key}|remind", LicenseEvaluator.NoticeKey(v, false, expires.AddDays(-7), 7, expires));
        Assert.AreEqual(v.Key, LicenseEvaluator.NoticeKey(v, false, expires.AddDays(-7), 0, expires), "RemindDays 0 is none");
        var ok = LicenseVerdict.Evaluate(Facts(expires: Today.AddYears(1)), Today)!;
        Assert.IsNull(LicenseEvaluator.NoticeKey(ok, false, Today, 7, ok.Findings.FirstOrDefault()?.On), "an OK state tells nothing");
        var renewed = LicenseVerdict.Evaluate(Facts(expires: Today.AddYears(1), announced: Today, lastAutomatic: true), Today)!;
        StringAssert.StartsWith(LicenseEvaluator.NoticeKey(renewed, true, Today, 7, Today.AddYears(1)), "renewed|");
    }

    [TestMethod]
    public void TheNumbersAndTheKeysHints()
    {
        Assert.AreEqual("LIC-12", LicenseWords.Number(LicenseType.License, 12));
        Assert.AreEqual("SVC-31", LicenseWords.Number(LicenseType.Service, 31));
        Assert.AreEqual("HW-40", LicenseWords.Number(LicenseType.Hardware, 40));
        foreach (var text in new[] { "12", "LIC-12", "svc 12", "hw-12", " Lic12 " })
        {
            Assert.AreEqual(12, LicenseWords.ParseNumber(text), text);
        }
        Assert.IsNull(LicenseWords.ParseNumber("KB-12"));
        Assert.AreEqual("CC9DR", LicenseKeyProtector.Hint("NJ4MX-VQQ7Q-FKK44-W8FV9-cc9dr"));
        Assert.AreEqual("AB", LicenseKeyProtector.Hint("a-b"));
        Assert.IsNull(LicenseKeyProtector.Hint("  "));
        Assert.AreEqual("Licence: M365 Business Standard (LIC-12)", LicenseWords.SensorName(LicenseType.License, "M365 Business Standard", 12));
    }
}
