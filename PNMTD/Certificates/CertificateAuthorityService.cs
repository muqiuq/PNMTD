using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using PNMTD.Api;
using PNMTD.Api.V1.Contracts;
using PNMTD.Data;
using PNMTD.Lib.Certificates;
using PNMTD.Lib.Helper;
using PNMTD.Models.Db;

namespace PNMTD.Certificates;

/// <summary>A CA's PEM as §4.5 reads it: its root, its intermediates, or why it isn't one.</summary>
public sealed record CertificateAuthorityBundle(X509Certificate2? Root, IReadOnlyList<X509Certificate2> Intermediates, string? Error, bool PrivateKeyLeftOut)
    : IDisposable
{
    public bool Valid => Error is null && Root is not null;

    /// <summary>The certificates only, root first: what is stored (never a private key that came with them).</summary>
    public string Pem => Root is null ? "" : string.Concat(new[] { Root }.Concat(Intermediates).Select(c => c.ExportCertificatePem() + "\n"));

    public void Dispose()
    {
        Root?.Dispose();
        foreach (var c in Intermediates)
        {
            c.Dispose();
        }
    }
}

/// <summary>
/// The private CAs (docs/ssl-certificate-plan.md §4.5, §6.3): the list, the preview of a PEM, adding and removing. A CA is
/// never edited; one in use can't be removed.
/// </summary>
public sealed class CertificateAuthorityService(PnmtdDbContext db, IHttpContextAccessor accessor)
{
    public const int MaxPem = 64 * 1024;
    public const int MaxCertificates = 10;

    /// <summary>
    /// §4.5's rules: exactly one self-signed CA certificate (the anchor), and CA certificates under it (the intermediates). A
    /// leaf, a second root or a certificate that doesn't chain to the root is refused, with the reason.
    /// </summary>
    public static CertificateAuthorityBundle Read(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return new CertificateAuthorityBundle(null, [], "Paste the CA's certificate in PEM (-----BEGIN CERTIFICATE-----).", false);
        }
        if (pem.Length > MaxPem)
        {
            return new CertificateAuthorityBundle(null, [], $"At most {MaxPem / 1024} KB of PEM.", false);
        }
        var privateKey = pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal);
        var all = new X509Certificate2Collection();
        try
        {
            all.ImportFromPem(pem);
        }
        catch (CryptographicException e)
        {
            return new CertificateAuthorityBundle(null, [], $"Not a PEM certificate: {e.Message.TrimEnd('.')}.", privateKey);
        }
        var certificates = all.Cast<X509Certificate2>().ToList();
        CertificateAuthorityBundle Refuse(string error)
        {
            foreach (var c in certificates)
            {
                c.Dispose();
            }
            return new CertificateAuthorityBundle(null, [], error, privateKey);
        }
        if (certificates.Count == 0)
        {
            return Refuse("No certificate in it: paste the CA's certificate in PEM (-----BEGIN CERTIFICATE-----).");
        }
        if (certificates.Count > MaxCertificates)
        {
            return Refuse($"At most {MaxCertificates} certificates: the root and its intermediates.");
        }
        if (certificates.FirstOrDefault(c => !CertificateParser.IsCa(c)) is { } leaf)
        {
            return Refuse($"{CertificateParser.Name(leaf.SubjectName).Short} is not a CA certificate (a server's certificate?). " +
                "For a self-signed server certificate, use the pinned mode instead.");
        }
        var roots = certificates.Where(CertificateParser.IsSelfSigned).ToList();
        if (roots.Count != 1)
        {
            return Refuse(roots.Count == 0
                ? "No root in it: a private CA starts with its self-signed root certificate."
                : "More than one root: add each root as a CA of its own.");
        }
        var root = roots[0];
        var intermediates = certificates.Where(c => !ReferenceEquals(c, root)).ToList();
        foreach (var intermediate in intermediates)
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.ExtraStore.AddRange(intermediates.ToArray());
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.IgnoreNotTimeNested;
            chain.ChainPolicy.VerificationTime = Clock.UtcNow;
            if (!chain.Build(intermediate))
            {
                return Refuse($"{CertificateParser.Name(intermediate.SubjectName).Short} isn't signed by {CertificateParser.Name(root.SubjectName).Short}.");
            }
        }
        return new CertificateAuthorityBundle(root, intermediates, null, privateKey);
    }

    /// <summary>The trust the probe needs for a sensor's CAs: the roots as anchors, the rest as intermediates, each named.</summary>
    public static CertificateTrustInput TrustFor(CertificateTrust mode, IEnumerable<CertificateAuthorityEntity> authorities)
    {
        if (mode != CertificateTrust.Private)
        {
            return mode == CertificateTrust.Pinned ? CertificateTrustInput.Pinned : CertificateTrustInput.Public;
        }
        var anchors = new List<X509Certificate2>();
        var intermediates = new List<X509Certificate2>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var authority in authorities)
        {
            var bundle = Read(authority.Pem);
            if (!bundle.Valid)
            {
                continue;
            }
            anchors.Add(bundle.Root!);
            names[CertificateParser.Sha256(bundle.Root!)] = authority.Name;
            foreach (var i in bundle.Intermediates)
            {
                intermediates.Add(i);
                names[CertificateParser.Sha256(i)] = authority.Name;
            }
        }
        return new CertificateTrustInput(CertificateTrust.Private, anchors, intermediates, names);
    }

    public async Task<IReadOnlyList<CertificateAuthorityItem>> ListAsync(CancellationToken ct)
    {
        var used = await db.CertificateSensorAuthorities.AsNoTracking().GroupBy(a => a.AuthorityId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var authorities = await db.CertificateAuthorities.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        return authorities.Select(a => ToItem(a, used.GetValueOrDefault(a.Id))).ToList();
    }

    public CertificateAuthorityPreview Preview(CertificateAuthorityRequest request)
    {
        using var bundle = Read(request.Pem);
        var certificates = bundle.Root is null ? [] : new[] { bundle.Root }.Concat(bundle.Intermediates)
            .Select(c => new CertificateAuthorityCertificate(CertificateParser.Name(c.SubjectName), CertificateParser.Name(c.IssuerName),
                CertificateParser.Sha256(c), Utc(c.NotBefore), Utc(c.NotAfter), ReferenceEquals(c, bundle.Root))).ToList();
        return new CertificateAuthorityPreview(bundle.Valid, bundle.Error, certificates, bundle.PrivateKeyLeftOut);
    }

    public async Task<CertificateAuthorityItem> CreateAsync(CertificateAuthorityRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        var v = new ApiValidator().Check(name is { Length: >= 1 and <= 100 }, "name", "The name must have 1 to 100 characters.");
        using var bundle = Read(request.Pem);
        v.Check(bundle.Valid, "pem", bundle.Error ?? "Not a CA.");
        v.ThrowIfInvalid();
        if (await db.CertificateAuthorities.AnyAsync(a => a.Name == name, ct))
        {
            throw ApiException.Invalid("name", "A private CA of this name exists already.");
        }
        var sha = CertificateParser.Sha256(bundle.Root!);
        if (await db.CertificateAuthorities.FirstOrDefaultAsync(a => a.RootSha256 == sha, ct) is { } same)
        {
            throw ApiException.Conflict("authority_exists", $"This root is the private CA {same.Name} already.");
        }
        var entity = new CertificateAuthorityEntity
        {
            Id = Guid.NewGuid(),
            Name = name!,
            Pem = bundle.Pem,
            RootSha256 = sha,
            RootSubject = CertificateParser.Name(bundle.Root!.SubjectName).Dn,
            RootNotBefore = Utc(bundle.Root.NotBefore),
            RootNotAfter = Utc(bundle.Root.NotAfter),
            IntermediateCount = bundle.Intermediates.Count,
            CreatedAt = Clock.UtcNow,
            CreatedBy = accessor.HttpContext?.User.Identity?.Name,
        };
        db.CertificateAuthorities.Add(entity);
        AuditLog.Target(AuditTarget.Of(AuditTargets.CertificateAuthority, entity.Id, entity.Name));
        AuditLog.Summary($"private CA {entity.Name} added: {entity.RootSubject}, SHA256 {sha}");
        await db.SaveChangesAsync(ct);
        return ToItem(entity, 0);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.CertificateAuthorities.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Private CA");
        AuditLog.Target(AuditTarget.Of(AuditTargets.CertificateAuthority, entity.Id, entity.Name));
        var users = await db.CertificateSensorAuthorities.AsNoTracking().Where(a => a.AuthorityId == id)
            .Join(db.Sensors, a => a.SensorId, s => s.Id, (a, s) => s.Parent.Name + " / " + s.Name).OrderBy(n => n).ToListAsync(ct);
        if (users.Count > 0)
        {
            throw ApiException.Conflict("authority_in_use",
                $"{entity.Name} is trusted by {users.Count} sensor{(users.Count == 1 ? "" : "s")}: {string.Join(", ", users.Take(10))}{(users.Count > 10 ? ", …" : "")}. Move them to another CA first.");
        }
        AuditLog.Summary($"private CA {entity.Name} removed (SHA256 {entity.RootSha256})");
        db.CertificateAuthorities.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(string Name, string Pem)> PemAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.CertificateAuthorities.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound("Private CA");
        return (entity.Name, entity.Pem);
    }

    private static CertificateAuthorityItem ToItem(CertificateAuthorityEntity a, int usedBy) => new(a.Id, a.Name, a.RootSubject, a.RootSha256,
        a.RootNotBefore, a.RootNotAfter, a.IntermediateCount, usedBy, a.CreatedAt, a.CreatedBy);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
}
