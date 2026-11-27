using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;

namespace PNMTD.Licenses;

/// <summary>An item's number as people read it, and the words its type and kind give the labels and messages (§3.3).</summary>
public static class LicenseWords
{
    public static string Prefix(LicenseType type) => type switch
    {
        LicenseType.Service => "SVC",
        LicenseType.Hardware => "HW",
        _ => "LIC",
    };

    /// <summary><c>LIC-12</c>, <c>SVC-31</c>, <c>HW-40</c>.</summary>
    public static string Number(LicenseType type, int number) => $"{Prefix(type)}-{number}";

    /// <summary><c>12</c>, <c>LIC-12</c>, <c>svc 12</c>, <c>hw-12</c>: the number, whatever the prefix; null for anything else.</summary>
    public static int? ParseNumber(string? text)
    {
        var t = text?.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "") ?? "";
        foreach (var prefix in new[] { "LIC", "SVC", "HW" })
        {
            if (t.StartsWith(prefix, StringComparison.Ordinal))
            {
                t = t[prefix.Length..];
                break;
            }
        }
        return t.Length is > 0 and <= 9 && t.All(char.IsAsciiDigit) ? int.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public static string TypeLabel(LicenseType type) => type switch
    {
        LicenseType.Service => "Service",
        LicenseType.Hardware => "Hardware",
        _ => "Licence",
    };

    /// <summary>The sensor's name (§5.3): <i>Licence: Microsoft 365 Business Standard (LIC-12)</i>.</summary>
    public static string SensorName(LicenseType type, string name, int number)
    {
        var text = $"{TypeLabel(type)}: {name} ({Number(type, number)})";
        return text.Length <= 200 ? text : text[..199] + "…";
    }

    /// <summary>
    /// What the expiry is, by kind: the subject of the messages (<i>Warranty ends on …</i>), and what an OK state says while it runs
    /// (<i>Warranty until …</i>).
    /// </summary>
    public static (string Subject, string Until) Expiry(LicenseKind kind) => kind switch
    {
        LicenseKind.Purchase => ("Warranty", "Warranty until"),
        LicenseKind.Lease => ("Lease", "Lease runs until"),
        LicenseKind.Trial => ("Trial", "Trial until"),
        LicenseKind.Support => ("Support contract", "Support contract valid until"),
        LicenseKind.Contract => ("Contract", "Contract runs until"),
        LicenseKind.Subscription => ("Subscription", "Subscription valid until"),
        _ => ("", "Valid until"),
    };

    public static readonly IReadOnlyDictionary<LicenseType, LicenseKind[]> Kinds = new Dictionary<LicenseType, LicenseKind[]>
    {
        [LicenseType.License] = [LicenseKind.Perpetual, LicenseKind.Subscription, LicenseKind.Support, LicenseKind.Trial, LicenseKind.Free, LicenseKind.Other],
        [LicenseType.Service] = [LicenseKind.Subscription, LicenseKind.Contract, LicenseKind.Support, LicenseKind.Usage, LicenseKind.OneOff, LicenseKind.Trial,
            LicenseKind.Free, LicenseKind.Other],
        [LicenseType.Hardware] = [LicenseKind.Purchase, LicenseKind.Lease, LicenseKind.Support, LicenseKind.Other],
    };
}

/// <summary>What decides an item's state (§4): the item, its template's end of life, its used seats and its newest renewal.</summary>
public sealed record LicenseFacts(
    LicenseType Type,
    LicenseKind Kind,
    int Number,
    string Name,
    LicenseStatus Status,
    DateOnly? ExpiresOn,
    LicenseRenewal Renewal,
    int WarnDays,
    int? NoticeDays,
    bool WarnBeforeRenewal,
    DateOnly? EndOfLifeOn,
    string? EndOfLifeNote,
    int EolWarnDays,
    bool SeatsFromHosts,
    int? Seats,
    int UsedSeats,
    string SeatUnit,
    DateOnly? AnnouncedExpiresOn,
    bool LastRenewalAutomatic)
{
    /// <summary>The cancellation deadline (§5.6): the expiry minus the notice period, for an active item that renews automatically.</summary>
    public DateOnly? CancelBy => Status == LicenseStatus.Active && Renewal == LicenseRenewal.Automatic && ExpiresOn is { } e && NoticeDays is { } n
        ? e.AddDays(-n) : null;
}

/// <summary>One finding: its code, its words, and the day it is about.</summary>
public sealed record LicenseFinding(int Code, string Text, DateOnly? On);

/// <summary>The verdict (§4): the worst finding's code, the message with the others, the key an event is written for.</summary>
public sealed record LicenseVerdictResult(int Code, string Message, string Key, IReadOnlyList<LicenseFinding> Findings)
{
    public bool Ok => Code < 400;
}

/// <summary>
/// An item's state (docs/licences-plan.md §4), pure: nothing else decides it. Messages carry names, numbers, days and seats, never a
/// price, a key or the note: anyone with Monitoring: View reads a sensor's messages. Days are absolute, so an event never goes stale.
/// </summary>
public static class LicenseVerdict
{
    /// <summary>Worst first: 446 → 448 → 449 → 445 → 447.</summary>
    private static readonly int[] Precedence =
    [
        PNMTStatusCodes.LICENSE_EXPIRED, PNMTStatusCodes.LICENSE_EOL, PNMTStatusCodes.LICENSE_OVER_ASSIGNED,
        PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE, PNMTStatusCodes.LICENSE_EOL_SOON,
    ];

    /// <summary>Null for a retired item, which has no verdict.</summary>
    public static LicenseVerdictResult? Evaluate(LicenseFacts f, DateOnly today)
    {
        if (f.Status == LicenseStatus.Retired)
        {
            return null;
        }
        var suffix = $"{f.Name} ({LicenseWords.Number(f.Type, f.Number)})";
        var findings = new List<LicenseFinding>();
        var (subject, until) = LicenseWords.Expiry(f.Kind);
        var cancelled = f.Status == LicenseStatus.Cancelled;
        var automatic = f.Renewal == LicenseRenewal.Automatic && !cancelled;

        if (f.ExpiresOn is { } expires)
        {
            if (today > expires)
            {
                var what = subject.Length == 0 ? "Expired" : $"{subject} ended";
                findings.Add(new(PNMTStatusCodes.LICENSE_EXPIRED, $"{what} on {Clock.FormatDay(expires)}{(cancelled ? " (cancelled)" : "")}", expires));
            }
            else if (!automatic && expires.DayNumber - today.DayNumber <= f.WarnDays)
            {
                var what = cancelled ? "Ends" : subject.Length == 0 ? "Expires" : $"{subject} ends";
                var warning = f.WarnDays == 0 ? "today is its last day" : $"within the {f.WarnDays} days' warning";
                findings.Add(new(PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE,
                    cancelled ? $"Ends on {Clock.FormatDay(expires)} (cancelled)" : $"{what} on {Clock.FormatDay(expires)}, {warning}", expires));
            }
            else if (automatic && f.WarnBeforeRenewal && f.CancelBy is { } cancelBy && today <= cancelBy
                && cancelBy.DayNumber - today.DayNumber <= f.WarnDays)
            {
                findings.Add(new(PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE,
                    $"Renews automatically on {Clock.FormatDay(expires.AddDays(1))} unless cancelled by {Clock.FormatDay(cancelBy)}", cancelBy));
            }
        }

        if (f.EndOfLifeOn is { } eol)
        {
            var life = f.Type == LicenseType.Service ? "end of service" : "end of life";
            var note = string.IsNullOrWhiteSpace(f.EndOfLifeNote) ? "" : $" ({f.EndOfLifeNote.Trim()})";
            if (today > eol)
            {
                findings.Add(new(PNMTStatusCodes.LICENSE_EOL, $"{f.Name} reached its {life}{note} on {Clock.FormatDay(eol)}", eol));
            }
            else if (eol.DayNumber - today.DayNumber <= f.EolWarnDays)
            {
                findings.Add(new(PNMTStatusCodes.LICENSE_EOL_SOON,
                    $"{f.Name} reaches its {life}{note} on {Clock.FormatDay(eol)}, within the {f.EolWarnDays} days' warning", eol));
            }
        }

        if (f.SeatsFromHosts && f.Seats is { } seats && f.UsedSeats > seats)
        {
            findings.Add(new(PNMTStatusCodes.LICENSE_OVER_ASSIGNED, $"Over-assigned: {f.UsedSeats} of {seats} {f.SeatUnit}", null));
        }

        if (findings.Count == 0)
        {
            return Ok(f, suffix, until, automatic);
        }
        findings.Sort((a, b) => Array.IndexOf(Precedence, a.Code).CompareTo(Array.IndexOf(Precedence, b.Code)));
        var worst = findings[0];
        var also = findings.Count > 1 ? "; also " + string.Join("; ", findings.Skip(1).Select(x => Lower(x.Text))) : "";
        var key = KeyOf(worst, f, findings);
        return new LicenseVerdictResult(worst.Code, $"{worst.Text}{also} · {suffix}", key, findings);
    }

    private static LicenseVerdictResult Ok(LicenseFacts f, string suffix, string until, bool automatic)
    {
        var key = $"ok|{Day(f.ExpiresOn)}|{Day(f.EndOfLifeOn)}";
        if (f.ExpiresOn is { } expires && f.AnnouncedExpiresOn is { } announced && announced != expires)
        {
            var how = f.LastRenewalAutomatic ? "Renewed automatically" : "Renewed";
            return new LicenseVerdictResult(PNMTStatusCodes.LICENSE_RENEWED,
                $"{how} until {Clock.FormatDay(expires)} (was {Clock.FormatDay(announced)}) · {suffix}", key, []);
        }
        var text = f.ExpiresOn switch
        {
            null => "Doesn't expire",
            { } e when automatic => $"{(LicenseWords.Expiry(f.Kind).Subject is { Length: > 0 } s ? s : "It")} renews automatically on {Clock.FormatDay(e.AddDays(1))}",
            { } e => $"{until} {Clock.FormatDay(e)}",
        };
        return new LicenseVerdictResult(PNMTStatusCodes.LICENSE_OK, $"{text} · {suffix}", key, []);
    }

    /// <summary>What the message is about (§4): an event is written when, and only when, it changes.</summary>
    private static string KeyOf(LicenseFinding worst, LicenseFacts f, IReadOnlyList<LicenseFinding> findings)
    {
        var main = worst.Code switch
        {
            PNMTStatusCodes.LICENSE_ABOUT_TO_EXPIRE or PNMTStatusCodes.LICENSE_EXPIRED => $"{worst.Code}|{Day(f.ExpiresOn)}|{f.Status}|{Day(f.CancelBy)}",
            PNMTStatusCodes.LICENSE_EOL_SOON or PNMTStatusCodes.LICENSE_EOL => $"{worst.Code}|{Day(f.EndOfLifeOn)}",
            _ => $"{worst.Code}|{f.UsedSeats}|{f.Seats}",
        };
        var others = string.Join(",", findings.Skip(1).Select(x => x.Code));
        return others.Length == 0 ? main : $"{main}|also {others}";
    }

    private static string Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    private static string Lower(string text) => text.Length == 0 || text.StartsWith("Over-", StringComparison.Ordinal) ? text.ToLowerInvariant()
        : char.IsUpper(text[0]) && (text.Length < 2 || !char.IsUpper(text[1])) && !IsName(text) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    /// <summary>An end-of-life finding starts with the item's name, which keeps its capital.</summary>
    private static bool IsName(string text) => text.Contains(" its end of ", StringComparison.Ordinal);
}
