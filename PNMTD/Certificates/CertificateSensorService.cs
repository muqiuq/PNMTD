using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Auth.Rights;
using PNMTD.Data;
using PNMTD.Lib.Certificates;
using PNMTD.Lib.Helper;
using PNMTD.Lib.Models.Enum;
using PNMTD.Models.Db;
using PNMTD.Services.Domain;

namespace PNMTD.Certificates;

/// <summary>A sensor's settings after a request: the request's values, else the trust mode's defaults, else the existing ones.</summary>
public sealed record CertificateSettings(CertificateTrust Trust, IReadOnlyList<Guid>? AuthorityIds, bool CheckName, bool CheckExpiry,
    bool IncompleteChainFails, string? ServerName, string? ConnectTo, int WarnDays, bool AlertOnCertificateChange, bool AlertOnKeyChange,
    bool AlertOnIssuerChange, CertificateIssuerMatch IssuerMatch);

/// <summary>
/// The SSL_CERTIFICATE sensor through <c>/sensors</c> and its own endpoints (docs/ssl-certificate-plan.md §6): the settings
/// with the trust modes' defaults, the resets when the endpoint or the trust changes, the summary and the detail, Check now,
/// accepting a change (only with the person's confirmation), the certificates seen and their PEM.
/// </summary>
public sealed class CertificateSensorService(PnmtdDbContext db, CertificateCheckService checks, IOptionsMonitor<CertificateOptions> options,
    IHttpContextAccessor accessor)
{
    public const int MaxNote = 500;

    /// <summary>The defaults of a trust mode (§5.4): (check the name, check expiry, incomplete fails, certificate, key, issuer).</summary>
    public static (bool CheckName, bool CheckExpiry, bool IncompleteChainFails, bool Certificate, bool Key, bool Issuer) Defaults(CertificateTrust trust) =>
        trust switch
        {
            CertificateTrust.Pinned => (false, true, false, true, false, false),
            _ => (true, true, false, false, false, true),
        };

    /// <summary>Every write to a certificate sensor needs Certificates: Edit besides Monitoring: Edit (§6.1); the CLI isn't asked.</summary>
    public void EnsureMayEdit()
    {
        if (accessor.HttpContext is { } http && !http.User.Has(Area.Certificates, Level.Edit))
        {
            throw ApiException.MissingRight(Area.Certificates, Level.Edit);
        }
    }

    public CertificateSettings Resolve(CertificateSensorRequest? request, CertificateSensorEntity? existing)
    {
        var trust = request?.Trust ?? existing?.Trust ?? CertificateTrust.Public;
        var fresh = existing is null || existing.Trust != trust;
        var d = Defaults(trust);
        bool Pick(bool? value, bool old, bool @default) => value ?? (fresh ? @default : old);
        if (request is null)
        {
            return existing is null
                ? new CertificateSettings(CertificateTrust.Public, [], d.CheckName, d.CheckExpiry, d.IncompleteChainFails, null, null,
                    options.CurrentValue.DefaultWarnDays, d.Certificate, d.Key, d.Issuer, CertificateIssuerMatch.Organization)
                : new CertificateSettings(existing.Trust, null, existing.CheckName, existing.CheckExpiry, existing.IncompleteChainFails,
                    existing.ServerName, existing.ConnectTo, existing.WarnDays, existing.AlertOnCertificateChange, existing.AlertOnKeyChange,
                    existing.AlertOnIssuerChange, existing.IssuerMatch);
        }
        return new CertificateSettings(
            trust,
            request.AuthorityIds?.Distinct().ToList() ?? (existing is null ? [] : null),
            Pick(request.CheckName, existing?.CheckName ?? d.CheckName, d.CheckName),
            Pick(request.CheckExpiry, existing?.CheckExpiry ?? d.CheckExpiry, d.CheckExpiry),
            Pick(request.IncompleteChainFails, existing?.IncompleteChainFails ?? d.IncompleteChainFails, d.IncompleteChainFails),
            Text.Trimmed(request.ServerName)?.TrimEnd('.'),
            Text.Trimmed(request.ConnectTo),
            request.WarnDays ?? existing?.WarnDays ?? options.CurrentValue.DefaultWarnDays,
            Pick(request.AlertOnCertificateChange, existing?.AlertOnCertificateChange ?? d.Certificate, d.Certificate),
            Pick(request.AlertOnKeyChange, existing?.AlertOnKeyChange ?? d.Key, d.Key),
            Pick(request.AlertOnIssuerChange, existing?.AlertOnIssuerChange ?? d.Issuer, d.Issuer),
            request.IssuerMatch ?? existing?.IssuerMatch ?? CertificateIssuerMatch.Organization);
    }

    /// <summary>§6.1: the address, the interval, and the settings as they will be.</summary>
    public async Task ValidateAsync(SensorRequest request, CertificateSensorEntity? existing, ApiValidator v, CancellationToken ct)
    {
        v.Check(TlsEndpoint.TryParse(request.Parameters, out _, out var error), "parameters", error ?? "host, host:port or an https:// URL.");
        v.Check(request.Interval == 0 || request.Interval is >= 3_600 and <= 2_592_000, "interval",
            "From 1 hour to 30 days (3600 to 2592000 seconds); 0 is daily.");
        var c = request.Certificate;
        v.Check(c is null || Enum.IsDefined(c.Trust), "certificate.trust", "Public, Private or Pinned.");
        v.Check(c?.IssuerMatch is null || Enum.IsDefined(c.IssuerMatch.Value), "certificate.issuerMatch", "Organization or Exact.");
        if (c is not null && !Enum.IsDefined(c.Trust))
        {
            return;
        }
        var s = Resolve(c, existing);
        v.Check(s.WarnDays is >= 1 and <= 365, "certificate.warnDays", "From 1 to 365 days.");
        v.Check(s.ServerName is null || (TlsEndpoint.IsHost(s.ServerName) && !TlsEndpoint.IsIp(s.ServerName)), "certificate.serverName",
            "A DNS name, as the certificate names it.");
        v.Check(s.ConnectTo is null || TlsEndpoint.IsHost(s.ConnectTo), "certificate.connectTo", "A host name or an IP address.");
        if (s.Trust == CertificateTrust.Pinned)
        {
            v.Check(s.AlertOnCertificateChange || s.AlertOnKeyChange, "certificate.alertOnCertificateChange",
                "A pinned certificate is trusted by its pin alone: fail on a new certificate or on a new key.");
        }
        if (s.Trust == CertificateTrust.Private)
        {
            var ids = s.AuthorityIds ?? (existing is null ? [] : await db.CertificateSensorAuthorities.AsNoTracking()
                .Where(a => a.SensorId == existing.SensorId).Select(a => a.AuthorityId).ToListAsync(ct));
            var known = ids.Count == 0 ? 0 : await db.CertificateAuthorities.CountAsync(a => ids.Contains(a.Id), ct);
            v.Check(ids.Count >= 1, "certificate.authorityIds", "The private mode needs at least one private CA (Admin → Certificates).");
            v.Check(known == ids.Count, "certificate.authorityIds", "No such private CA.");
        }
    }

    /// <summary>
    /// A new sensor's row, or its settings changed (§5.2): another endpoint starts over (baseline, versions' pointers, state);
    /// another trust or other CAs check at once; another interval moves the next check; other rules write the state anew.
    /// </summary>
    public async Task ApplyAsync(SensorEntity sensor, CertificateSensorRequest? request, string? oldParameters, int oldInterval, CancellationToken ct)
    {
        var endpoint = TlsEndpoint.Parse(sensor.Parameters!);
        sensor.Parameters = endpoint.Normalized;
        sensor.GracePeriod = 0;
        if (sensor.Interval <= 0)
        {
            sensor.Interval = CertificateCheckService.DefaultIntervalSeconds;
        }
        var row = await db.CertificateSensors.Include(c => c.Authorities).SingleOrDefaultAsync(c => c.SensorId == sensor.Id, ct);
        var isNew = row is null;
        var s = Resolve(request, row);
        if (row is null)
        {
            row = new CertificateSensorEntity { SensorId = sensor.Id, Created = Clock.UtcNow };
            db.CertificateSensors.Add(row);
        }
        var before = (row.Trust, row.ServerName, row.ConnectTo, Cas: string.Join(",", row.Authorities.Select(a => a.AuthorityId).Order()));
        var rulesBefore = (row.CheckName, row.CheckExpiry, row.IncompleteChainFails, row.WarnDays, row.AlertOnCertificateChange, row.AlertOnKeyChange,
            row.AlertOnIssuerChange, row.IssuerMatch);
        row.Trust = s.Trust;
        row.CheckName = s.CheckName;
        row.CheckExpiry = s.CheckExpiry;
        row.IncompleteChainFails = s.IncompleteChainFails;
        row.ServerName = s.ServerName;
        row.ConnectTo = s.ConnectTo;
        row.WarnDays = s.WarnDays;
        row.AlertOnCertificateChange = s.AlertOnCertificateChange;
        row.AlertOnKeyChange = s.AlertOnKeyChange;
        row.AlertOnIssuerChange = s.AlertOnIssuerChange;
        row.IssuerMatch = s.IssuerMatch;
        var wanted = s.Trust != CertificateTrust.Private ? [] : s.AuthorityIds ?? row.Authorities.Select(a => a.AuthorityId).ToList();
        foreach (var link in row.Authorities.Where(a => !wanted.Contains(a.AuthorityId)).ToList())
        {
            row.Authorities.Remove(link);
        }
        foreach (var id in wanted.Where(id => row.Authorities.All(a => a.AuthorityId != id)))
        {
            row.Authorities.Add(new CertificateSensorAuthorityEntity { SensorId = sensor.Id, AuthorityId = id });
        }
        if (isNew)
        {
            return;
        }
        var after = (row.Trust, row.ServerName, row.ConnectTo, Cas: string.Join(",", wanted.Order()));
        if (oldParameters != sensor.Parameters || before.ServerName != after.ServerName || before.ConnectTo != after.ConnectTo)
        {
            // Another endpoint: the next check pins anew. The versions stay in the history.
            row.AcceptedVersionId = null;
            row.AcceptedAt = null;
            row.AcceptedBy = null;
            row.AcceptedNote = null;
            row.CurrentVersionId = null;
            row.AnnouncedVersionId = null;
            row.Connection = null;
            row.Validation = null;
            row.ConsecutiveFailures = 0;
            row.FirstFailureAt = null;
            row.LastError = null;
            row.LastCode = null;
            row.LastVerdictKey = null;
            row.NextCheckAt = null;
            return;
        }
        if (before.Trust != after.Trust || before.Cas != after.Cas)
        {
            row.NextCheckAt = null;
            row.LastVerdictKey = null;
        }
        else if (oldInterval != sensor.Interval && row.LastCheckAt is { } last)
        {
            row.NextCheckAt = last + CertificateCheckService.IntervalOf(sensor.Interval);
        }
        if (rulesBefore != (row.CheckName, row.CheckExpiry, row.IncompleteChainFails, row.WarnDays, row.AlertOnCertificateChange,
                row.AlertOnKeyChange, row.AlertOnIssuerChange, row.IssuerMatch))
        {
            row.LastVerdictKey = null;
        }
    }

    /// <summary>The type changed away from SSL certificate: the row, its CA links and its versions go.</summary>
    public async Task RemoveAsync(Guid sensorId, CancellationToken ct)
    {
        await db.CertificateSensors.Where(c => c.SensorId == sensorId).ExecuteDeleteAsync(ct);
        await db.CertificateVersions.Where(v => v.SensorId == sensorId).ExecuteDeleteAsync(ct);
    }

    /// <summary>After a save: the verdict at once, from what is stored (a new sensor's first check comes within 15 seconds).</summary>
    public Task EvaluateAsync(Guid sensorId, CancellationToken ct) => checks.EvaluateAsync(sensorId, ct);

    // --- reading ---------------------------------------------------------------------------------------------------------

    private sealed record Loaded(CertificateSensorEntity Row, SensorEntity Sensor, CertificateVersionEntity? Current, CertificateVersionEntity? Accepted,
        IReadOnlyList<Ref> Authorities);

    private async Task<Loaded?> LoadAsync(Guid sensorId, CancellationToken ct)
    {
        var row = await db.CertificateSensors.AsNoTracking().Include(c => c.Sensor).SingleOrDefaultAsync(c => c.SensorId == sensorId, ct);
        if (row is null)
        {
            return null;
        }
        Guid?[] ids = [row.CurrentVersionId, row.AcceptedVersionId];
        var versions = await db.CertificateVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var authorities = await db.CertificateSensorAuthorities.AsNoTracking().Where(a => a.SensorId == sensorId)
            .OrderBy(a => a.Authority.Name).Select(a => new Ref(a.AuthorityId, a.Authority.Name)).ToListAsync(ct);
        return new Loaded(row, row.Sensor, row.CurrentVersionId is { } c ? versions.GetValueOrDefault(c) : null,
            row.AcceptedVersionId is { } a ? versions.GetValueOrDefault(a) : null, authorities);
    }

    private static CertificateRules RulesOf(Loaded l) =>
        CertificateRules.From(l.Row, l.Row.ServerName ?? (TlsEndpoint.TryParse(l.Sensor.Parameters, out var e, out _) ? e.Host : ""));

    private CertificateSensorSummary Summary(Loaded l)
    {
        var row = l.Row;
        var endpoint = TlsEndpoint.TryParse(l.Sensor.Parameters, out var e, out _) ? e : new TlsEndpoint(l.Sensor.Parameters ?? "", TlsEndpoint.DefaultPort);
        var validation = CertificateJson.Read<CertificateValidation>(row.Validation);
        var expiresAt = l.Current is null ? (DateTime?)null : validation?.ChainExpiresAt ?? l.Current.NotAfter;
        CertificatePendingChange? pending = null;
        if (l.Current is not null && l.Accepted is not null && l.Current.Id != l.Accepted.Id)
        {
            var comparison = CertificateVerdict.Compare(RulesOf(l), CertificateSeen.From(l.Accepted), CertificateSeen.From(l.Current));
            if (comparison.Alarm)
            {
                pending = new CertificatePendingChange(comparison.Kinds, l.Current.FirstSeenAt, comparison.Certificate, comparison.Key, comparison.Issuer);
            }
        }
        return new CertificateSensorSummary(endpoint.Host, endpoint.Port, row.ServerName, row.ConnectTo, row.Trust, l.Authorities, row.CheckName,
            row.CheckExpiry, row.IncompleteChainFails, row.WarnDays, row.AlertOnCertificateChange, row.AlertOnKeyChange, row.AlertOnIssuerChange,
            row.IssuerMatch, row.LastCheckAt, row.NextCheckAt, row.LastSuccessAt, checks.IsChecking(row.SensorId), row.ConsecutiveFailures,
            row.FirstFailureAt, row.LastError, expiresAt, expiresAt is { } x ? (int)Math.Floor((x - Clock.UtcNow).TotalDays) : null,
            l.Current is null ? null : CertificateSeen.From(l.Current).IssuerLabel, l.Current?.IssuerOrganization, l.Current?.SubjectCommonName,
            l.Current?.SelfSigned ?? false, row.AcceptedAt, row.AcceptedBy, pending);
    }

    public async Task<CertificateSensorSummary?> SummaryAsync(Guid sensorId, CancellationToken ct) =>
        await LoadAsync(sensorId, ct) is { } l ? Summary(l) : null;

    public async Task<CertificateSensorDetail> DetailAsync(Guid sensorId, CancellationToken ct)
    {
        var l = await LoadAsync(sensorId, ct) ?? throw ApiException.NotFound("SSL certificate sensor");
        var current = l.Current is null ? null : Item(l.Current);
        var accepted = l.Accepted is null || l.Accepted.Id == l.Current?.Id ? null : Item(l.Accepted);
        CertificateChange? change = null;
        if (l.Current is not null && l.Accepted is not null && l.Current.Id != l.Accepted.Id)
        {
            var comparison = CertificateVerdict.Compare(RulesOf(l), CertificateSeen.From(l.Accepted), CertificateSeen.From(l.Current));
            change = new CertificateChange(comparison.Certificate, comparison.Key, comparison.Issuer, comparison.Alarm, comparison.Kinds,
                l.Current.FirstSeenAt, Fields(accepted!.Facts, current!.Facts));
        }
        var count = await db.CertificateVersions.CountAsync(v => v.SensorId == sensorId, ct);
        return new CertificateSensorDetail(Summary(l), current, accepted, l.Row.AcceptedNote, change,
            CertificateJson.Read<CertificateConnection>(l.Row.Connection), CertificateJson.Read<CertificateValidation>(l.Row.Validation), count);
    }

    private static CertificateItem Item(CertificateVersionEntity v) => new(v.Id,
        CertificateJson.Read<CertificateFacts>(v.Facts) ?? throw new InvalidOperationException($"Version {v.Id} has no facts."),
        CertificateJson.Read<List<CertificateChainEntry>>(v.Chain) ?? [], v.FirstSeenAt, v.LastSeenAt);

    /// <summary>The comparison of §8.4: each field accepted against current, and whether it changed.</summary>
    public static IReadOnlyList<CertificateFieldChange> Fields(CertificateFacts accepted, CertificateFacts current)
    {
        var fields = new List<CertificateFieldChange>();
        void Add(string field, string? a, string? c) => fields.Add(new CertificateFieldChange(field, a, c, !string.Equals(a, c, StringComparison.Ordinal)));
        Add("SHA-256", accepted.Sha256, current.Sha256);
        Add("Key", accepted.Key.Label, current.Key.Label);
        Add("Key (SPKI SHA-256)", accepted.SpkiSha256, current.SpkiSha256);
        Add("Issuer", accepted.Issuer.Dn, current.Issuer.Dn);
        Add("Subject", accepted.Subject.Dn, current.Subject.Dn);
        Add("Names", string.Join(", ", accepted.Names), string.Join(", ", current.Names));
        var added = current.Names.Except(accepted.Names, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = accepted.Names.Except(current.Names, StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0)
        {
            fields.Add(new CertificateFieldChange("Names added", null, string.Join(", ", added), true));
        }
        if (removed.Count > 0)
        {
            fields.Add(new CertificateFieldChange("Names removed", string.Join(", ", removed), null, true));
        }
        Add("Serial", accepted.Serial, current.Serial);
        Add("Valid from", CertificateVerdict.Format(accepted.NotBefore), CertificateVerdict.Format(current.NotBefore));
        Add("Valid until", CertificateVerdict.Format(accepted.NotAfter), CertificateVerdict.Format(current.NotAfter));
        return fields;
    }

    public async Task<Paged<CertificateVersionItem>> VersionsAsync(Guid sensorId, CertificateVersionQuery query, CancellationToken ct)
    {
        var row = await db.CertificateSensors.AsNoTracking().SingleOrDefaultAsync(c => c.SensorId == sensorId, ct)
            ?? throw ApiException.NotFound("SSL certificate sensor");
        var page = await Paging.PageAsync(db.CertificateVersions.AsNoTracking().Where(v => v.SensorId == sensorId).OrderByDescending(v => v.LastSeenAt), query, ct);
        return new Paged<CertificateVersionItem>(page.Items.Select(v => new CertificateVersionItem(v.Id, v.Sha256, v.Serial,
            CertificateSeen.From(v).IssuerLabel, v.SubjectCommonName, v.NotBefore, v.NotAfter, v.FirstSeenAt, v.LastSeenAt,
            v.Id == row.CurrentVersionId, v.Id == row.AcceptedVersionId)).ToList(), page.Page, page.PageSize, page.TotalCount);
    }

    public async Task<CertificateItem> VersionAsync(Guid sensorId, Guid versionId, CancellationToken ct) =>
        Item(await db.CertificateVersions.AsNoTracking().SingleOrDefaultAsync(v => v.SensorId == sensorId && v.Id == versionId, ct)
            ?? throw ApiException.NotFound("Certificate"));

    /// <summary>The leaf, or the chain as sent, and a file name: <c>host-20261117-1A2B3C4D.pem</c>.</summary>
    public async Task<(string FileName, string Pem)> PemAsync(Guid sensorId, Guid versionId, bool chain, CancellationToken ct)
    {
        var v = await db.CertificateVersions.AsNoTracking().SingleOrDefaultAsync(x => x.SensorId == sensorId && x.Id == versionId, ct)
            ?? throw ApiException.NotFound("Certificate");
        var host = await db.Sensors.AsNoTracking().Where(s => s.Id == sensorId).Select(s => s.Parameters).SingleOrDefaultAsync(ct);
        var name = TlsEndpoint.TryParse(host, out var e, out _) ? e.Host.Replace(':', '_') : "certificate";
        var pem = v.ChainPem;
        if (!chain)
        {
            var end = pem.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal);
            pem = end < 0 ? pem : pem[..(end + "-----END CERTIFICATE-----".Length)] + "\n";
        }
        return ($"{name}-{v.FirstSeenAt:yyyyMMdd}-{v.Sha256[..8]}{(chain ? "-chain" : "")}.pem", pem);
    }

    // --- acting ----------------------------------------------------------------------------------------------------------

    /// <summary>Checks now (Certificates: Edit) and answers with the detail once done.</summary>
    public async Task<CertificateSensorDetail> CheckNowAsync(Guid sensorId, CancellationToken ct)
    {
        var target = await TargetAsync(sensorId, ct);
        AuditLog.Target(target);
        if (!options.CurrentValue.Enabled)
        {
            throw ApiException.Conflict("certificates_off", "Certificate checks are off (Certificates__Enabled=false).");
        }
        AuditLog.Summary($"{target.Name}: certificate checked now");
        if (!await checks.CheckAsync(sensorId, wait: true, ct))
        {
            throw ApiException.Conflict("check_busy", "A check of this sensor is still running: try again in a moment.");
        }
        return await DetailAsync(sensorId, ct);
    }

    /// <summary>
    /// Accepts the current certificate as the sensor's baseline (§8.4): only with <c>confirmed</c>, only the SHA-256 the
    /// person saw, audit-logged with both fingerprints and the note.
    /// </summary>
    public async Task<CertificateSensorDetail> AcceptAsync(Guid sensorId, CertificateAcceptRequest request, CancellationToken ct)
    {
        var target = await TargetAsync(sensorId, ct);
        AuditLog.Target(target);
        if (!request.Confirmed)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "confirmation_required",
                "Accepting a changed certificate needs the explicit confirmation that the change is expected (confirmed: true).");
        }
        var note = Text.Trimmed(request.Note);
        new ApiValidator().Check(note is null || note.Length <= MaxNote, "note", $"At most {MaxNote} characters.").ThrowIfInvalid();
        var row = await db.CertificateSensors.SingleAsync(c => c.SensorId == sensorId, ct);
        var l = await LoadAsync(sensorId, ct);
        if (l?.Current is not { } current || l.Accepted?.Id == current.Id)
        {
            throw ApiException.Conflict("no_change", "The sensor's certificate hasn't changed: there is nothing to accept.");
        }
        if (!string.Equals(request.Sha256?.Replace(":", "").Trim(), current.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw ApiException.Conflict("certificate_changed_again",
                "The certificate changed again since you looked: review the new comparison before accepting.");
        }
        var kinds = l.Accepted is null ? [] : CertificateVerdict.Compare(RulesOf(l), CertificateSeen.From(l.Accepted), CertificateSeen.From(current)).Kinds;
        AuditLog.Summary($"{target.Name}: certificate SHA256 {current.Sha256} accepted, was {l.Accepted?.Sha256 ?? "none"}"
            + (kinds.Count > 0 ? $"; {string.Join(", ", kinds)}" : "") + (note is null ? "" : $"; note: \"{note}\""));
        row.AcceptedVersionId = current.Id;
        row.AcceptedAt = Clock.UtcNow;
        row.AcceptedBy = accessor.HttpContext?.User.Identity?.Name is { Length: > 0 } who ? who : "an admin";
        row.AcceptedNote = note;
        await db.SaveChangesAsync(ct);
        await checks.EvaluateAsync(sensorId, ct);
        return await DetailAsync(sensorId, ct);
    }

    private async Task<Audit.AuditTarget> TargetAsync(Guid sensorId, CancellationToken ct)
    {
        var s = await db.Sensors.AsNoTracking().Where(x => x.Id == sensorId && x.Type == SensorType.SSL_CERTIFICATE)
            .Select(x => new { x.Name, Host = x.Parent.Name, x.ParentId, x.Parent.LocationId }).SingleOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("SSL certificate sensor");
        return Audit.AuditTarget.Sensor(sensorId, $"{s.Host} / {s.Name}", s.ParentId, s.LocationId);
    }
}
