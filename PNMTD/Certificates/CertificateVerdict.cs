using System.Globalization;
using PNMTD.Lib.Certificates;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models;
using PNMTD.Models.Db;

namespace PNMTD.Certificates;

/// <summary>A sensor's trust, checks and change rules, as the verdict reads them.</summary>
public sealed record CertificateRules(CertificateTrust Trust, bool CheckName, bool CheckExpiry, bool IncompleteChainFails, int WarnDays,
    bool AlertOnCertificateChange, bool AlertOnKeyChange, bool AlertOnIssuerChange, CertificateIssuerMatch IssuerMatch, string ExpectedName)
{
    public static CertificateRules From(CertificateSensorEntity row, string expectedName) => new(row.Trust, row.CheckName, row.CheckExpiry,
        row.IncompleteChainFails, row.WarnDays, row.AlertOnCertificateChange, row.AlertOnKeyChange, row.AlertOnIssuerChange, row.IssuerMatch,
        expectedName);
}

/// <summary>What the verdict needs of a version: its identity, its key, its issuer, its validity, its names.</summary>
public sealed record CertificateSeen(Guid Id, string Sha256, string SpkiSha256, string Issuer, string? IssuerOrganization, string? IssuerCommonName,
    string? SubjectCommonName, IReadOnlyList<string> Names, DateTime NotBefore, DateTime NotAfter, bool SelfSigned, DateTime FirstSeenAt)
{
    public static CertificateSeen From(CertificateVersionEntity v) => new(v.Id, v.Sha256, v.SpkiSha256, v.Issuer,
        v.IssuerOrganization, v.IssuerCommonName, v.SubjectCommonName, v.Names, v.NotBefore, v.NotAfter, v.SelfSigned, v.FirstSeenAt);

    /// <summary>"Let's Encrypt R11", "DigiCert Global G2 TLS RSA SHA256 2020 CA1", "self-signed".</summary>
    public string IssuerLabel => CertificateVerdict.IssuerLabel(IssuerOrganization, IssuerCommonName, Issuer, SelfSigned);
}

/// <summary>The schedule's side of the verdict: failures in a row, since when, why; and the last validation.</summary>
public sealed record CertificateCheckState(int ConsecutiveFailures, DateTime? FirstFailureAt, string? LastError, CertificateValidation? Validation);

/// <summary>Which kinds of change a current certificate has against the accepted one (§5.3), and whether the rules alarm on them.</summary>
public sealed record CertificateComparison(bool Certificate, bool Key, bool Issuer, bool Alarm, IReadOnlyList<string> Kinds)
{
    public bool Any => Certificate;
}

/// <summary>A verdict (§3, §5.3): the code, the message, the key an event is written by, and whether the baseline moves.</summary>
public sealed record CertificateVerdictResult(int Code, string Message, string Key, bool MoveBaseline, CertificateComparison? Change);

/// <summary>
/// The SSL certificate sensor's verdict (docs/ssl-certificate-plan.md §5.3), pure: nothing else decides the state. Worst
/// first: 489, 486, 488, 487, 485, then 286 or 285. Messages carry absolute dates in PNMT's time zone, never "in 12 days".
/// </summary>
public static class CertificateVerdict
{
    public const int MaxMessage = 500;

    private static readonly int[] Precedence =
    [
        PNMTStatusCodes.SSL_EXPIRED, PNMTStatusCodes.SSL_INVALID, PNMTStatusCodes.SSL_CHANGED_UNEXPECTEDLY, PNMTStatusCodes.SSL_ABOUT_TO_EXPIRE,
    ];

    /// <summary>
    /// The verdict, or null while there is nothing to say (no success yet, and fewer failures than
    /// <paramref name="failuresBeforeUnreachable"/>).
    /// </summary>
    public static CertificateVerdictResult? Evaluate(CertificateRules rules, CertificateCheckState state, CertificateSeen? current,
        CertificateSeen? accepted, Guid? announced, DateTime now, int failuresBeforeUnreachable = 2)
    {
        if (state.ConsecutiveFailures >= failuresBeforeUnreachable)
        {
            var since = state.FirstFailureAt is { } at ? $" since {Format(at)}" : "";
            var last = current is null ? "" : $" (the last certificate seen is valid until {Format(current.NotAfter)})";
            return new CertificateVerdictResult(PNMTStatusCodes.SSL_UNREACHABLE, Cut($"Unreachable{since}: {state.LastError ?? "no answer"}{last}"),
                "489", false, null);
        }
        if (current is null)
        {
            return null;
        }

        var findings = new List<(int Code, string Text)>();
        var validation = state.Validation;
        var expiresAt = validation?.ChainExpiresAt ?? current.NotAfter;
        var expiresIsLeaf = validation?.ChainExpiresIsLeaf ?? true;
        var expiring = expiresIsLeaf ? null : validation!.ChainExpiresSubject;

        if (rules.CheckExpiry && now >= expiresAt)
        {
            findings.Add((PNMTStatusCodes.SSL_EXPIRED, expiring is null
                ? $"Expired on {Format(expiresAt)}"
                : $"The intermediate {expiring} expired on {Format(expiresAt)}"));
        }

        var invalid = new List<string>();
        if (rules.Trust != CertificateTrust.Pinned && validation is { Trusted: false })
        {
            invalid.Add($"Not trusted: {(validation.Problems.Count > 0 ? string.Join("; ", validation.Problems) : "the chain doesn't verify")}");
        }
        if (rules.CheckName && validation is { NameMatches: false })
        {
            var names = current.Names.Count > 0 ? current.Names : current.SubjectCommonName is { } cn ? [cn] : [];
            var shown = string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : "");
            invalid.Add($"Name mismatch: the certificate is for {(shown.Length > 0 ? shown : "no name")}, not {rules.ExpectedName}");
        }
        if (rules.CheckExpiry && now < current.NotBefore)
        {
            invalid.Add($"Not valid before {Format(current.NotBefore)}");
        }
        if (rules.IncompleteChainFails && rules.Trust != CertificateTrust.Pinned && validation is { Complete: false })
        {
            invalid.Add("Incomplete chain: the server doesn't send its intermediate");
        }
        if (invalid.Count > 0)
        {
            findings.Add((PNMTStatusCodes.SSL_INVALID, string.Join("; ", invalid)));
        }

        var change = accepted is null || accepted.Id == current.Id ? null : Compare(rules, accepted, current);
        var moveBaseline = false;
        if (change is { Any: true })
        {
            if (change.Alarm)
            {
                findings.Add((PNMTStatusCodes.SSL_CHANGED_UNEXPECTEDLY,
                    $"Changed unexpectedly on {Format(current.FirstSeenAt, dateOnly: true)}: {string.Join(", ", change.Kinds)}. "
                    + "Accept the new certificate on the sensor's page if this was expected."));
            }
            else
            {
                moveBaseline = true;
            }
        }

        if (rules.CheckExpiry && now < expiresAt && expiresAt - now <= TimeSpan.FromDays(rules.WarnDays))
        {
            findings.Add((PNMTStatusCodes.SSL_ABOUT_TO_EXPIRE, expiring is null
                ? $"Expires on {Format(expiresAt)}, within the {rules.WarnDays} days' warning"
                : $"The intermediate {expiring} expires on {Format(expiresAt)}, before the certificate"));
        }

        if (findings.Count > 0)
        {
            var ordered = findings.OrderBy(f => Array.IndexOf(Precedence, f.Code)).ToList();
            var (code, text) = ordered[0];
            var also = ordered.Skip(1).Select(f => Lower(f.Text)).ToList();
            var message = text + string.Concat(also.Select(a => "; also " + a)) + $" · {current.IssuerLabel}";
            var key = code == PNMTStatusCodes.SSL_CHANGED_UNEXPECTEDLY
                ? $"{code}|{current.Id}|{accepted?.Id}"
                : $"{code}|{current.Id}";
            return new CertificateVerdictResult(code, Cut(message), key, moveBaseline, change);
        }

        if (announced is { } previous && previous != current.Id)
        {
            var was = change is { Issuer: true } && accepted is not null ? $" (was {accepted.IssuerLabel})" : "";
            return new CertificateVerdictResult(PNMTStatusCodes.SSL_CHANGED,
                Cut($"New certificate: valid until {Format(current.NotAfter)} · {current.IssuerLabel}{was}"), $"ok|{current.Id}", moveBaseline, change);
        }
        var subject = current.SubjectCommonName ?? current.Names.FirstOrDefault();
        return new CertificateVerdictResult(PNMTStatusCodes.SSL_OK,
            Cut($"Valid until {Format(current.NotAfter)} · {current.IssuerLabel}{(subject is null ? "" : $" · {subject}")}"), $"ok|{current.Id}", moveBaseline, change);
    }

    /// <summary>
    /// The kinds of change (§5.3): the certificate (SHA-256), its key (SPKI), its issuer (by organisation or full name), and
    /// whether a switched-on rule alarms. The pinned mode has no issuer rule: a self-signed certificate is its own issuer.
    /// </summary>
    public static CertificateComparison Compare(CertificateRules rules, CertificateSeen accepted, CertificateSeen current)
    {
        var certificate = !string.Equals(accepted.Sha256, current.Sha256, StringComparison.OrdinalIgnoreCase);
        var key = !string.Equals(accepted.SpkiSha256, current.SpkiSha256, StringComparison.Ordinal);
        var issuer = IssuerKey(accepted, rules.IssuerMatch) != IssuerKey(current, rules.IssuerMatch);
        var issuerRule = rules.AlertOnIssuerChange && rules.Trust != CertificateTrust.Pinned;
        var alarm = (rules.AlertOnCertificateChange && certificate) || (rules.AlertOnKeyChange && key) || (issuerRule && issuer);
        var kinds = new List<string>();
        if (certificate && !key && !issuer)
        {
            kinds.Add("new certificate");
        }
        if (key)
        {
            kinds.Add("new key");
        }
        if (issuer)
        {
            kinds.Add($"issuer {accepted.IssuerLabel} → {current.IssuerLabel}");
        }
        return new CertificateComparison(certificate, key, issuer, alarm, kinds);
    }

    /// <summary>The issuer, as compared: its organisation (trimmed, case ignored), else its full name; or always its full name.</summary>
    public static string IssuerKey(CertificateSeen seen, CertificateIssuerMatch match) =>
        match == CertificateIssuerMatch.Organization && !string.IsNullOrWhiteSpace(seen.IssuerOrganization)
            ? "o:" + seen.IssuerOrganization.Trim().ToLowerInvariant()
            : "dn:" + seen.Issuer.Trim().ToLowerInvariant();

    public static string IssuerLabel(string? organization, string? commonName, string issuer, bool selfSigned)
    {
        if (selfSigned)
        {
            return "self-signed";
        }
        if (organization is null)
        {
            return commonName ?? issuer;
        }
        if (commonName is null)
        {
            return organization;
        }
        var firstWord = organization.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? organization;
        return commonName.Contains(firstWord, StringComparison.OrdinalIgnoreCase) ? commonName : $"{organization} {commonName}";
    }

    /// <summary>"1 Feb 2027 13:00" in PNMT's time zone (TZ), or "1 Feb 2027".</summary>
    public static string Format(DateTime instant, bool dateOnly = false) =>
        Clock.ToLocal(instant).ToString(dateOnly ? "d MMM yyyy" : "d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Lower(string text) => text.Length > 0 && char.IsUpper(text[0]) && !(text.Length > 1 && char.IsUpper(text[1]))
        ? char.ToLowerInvariant(text[0]) + text[1..]
        : text;

    private static string Cut(string text) => text.Length <= MaxMessage ? text : text[..(MaxMessage - 1)] + "…";
}
