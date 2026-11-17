using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PNMTD.Lib.Certificates;

/// <summary>What makes a certificate valid for a sensor (docs/ssl-certificate-plan.md §5.1): public CAs, private CAs, or the pin alone.</summary>
public enum CertificateTrust { Public, Private, Pinned }

/// <summary>A distinguished name and its usual parts.</summary>
public sealed record CertificateName(string Dn, string? CommonName, string? Organization, string? OrganizationalUnit, string? Locality,
    string? State, string? Country, string? Email)
{
    /// <summary>For messages: the common name, else the organisation, else the whole name.</summary>
    public string Short => CommonName ?? Organization ?? Dn;
}

/// <summary>A public key: RSA 2048, ECDSA P-256, Ed25519, or another by its OID.</summary>
public sealed record CertificateKey(string Algorithm, int? Size, string? Curve)
{
    public string Label => Curve is not null ? $"{Algorithm} {Curve}" : Size is { } size ? $"{Algorithm} {size}" : Algorithm;
}

/// <summary>An extension as stored: OID, name, critical, and its text.</summary>
public sealed record CertificateExtension(string Oid, string Name, bool Critical, string Value);

/// <summary>Everything a certificate says (docs/ssl-certificate-plan.md §4.4), as stored in <c>certificate_versions.Facts</c> and returned by the API.</summary>
public sealed record CertificateFacts(
    string Sha256,
    string Sha1,
    string SpkiSha256,
    string Serial,
    int Version,
    CertificateName Subject,
    CertificateName Issuer,
    IReadOnlyList<string> Names,
    DateTime NotBefore,
    DateTime NotAfter,
    int LifetimeDays,
    CertificateKey Key,
    string SignatureAlgorithm,
    bool SelfSigned,
    bool IsCa,
    string? ValidationLevel,
    IReadOnlyList<string> KeyUsage,
    IReadOnlyList<string> ExtendedKeyUsage,
    string? SubjectKeyId,
    string? AuthorityKeyId,
    IReadOnlyList<string> OcspUrls,
    IReadOnlyList<string> CaIssuerUrls,
    IReadOnlyList<string> CrlUrls,
    IReadOnlyList<string> Policies,
    int? Scts,
    bool MustStaple,
    IReadOnlyList<CertificateExtension> Extensions,
    bool Truncated);

/// <summary>Where a certificate of a built chain came from.</summary>
public enum CertificateSource { Server, PublicRoot, PrivateCa, Store }

/// <summary>One certificate of the chain, from the leaf (depth 0) to the root.</summary>
public sealed record CertificateChainEntry(int Depth, CertificateName Subject, CertificateName Issuer, DateTime NotBefore, DateTime NotAfter,
    string Sha256, string Key, CertificateSource Source, string? SourceName, bool TrustAnchor, bool SelfSigned);

/// <summary>Reads <see cref="CertificateFacts"/> from a certificate, with the limits of §4.4 against a hostile server.</summary>
public static class CertificateParser
{
    public const int MaxNames = 500;
    public const int MaxExtensions = 50;
    public const int MaxNameField = 1000;
    public const int MaxExtensionText = 4000;

    private const string OidCommonName = "2.5.4.3";
    private const string OidOrganization = "2.5.4.10";
    private const string OidOrganizationalUnit = "2.5.4.11";
    private const string OidLocality = "2.5.4.7";
    private const string OidState = "2.5.4.8";
    private const string OidCountry = "2.5.4.6";
    private const string OidEmail = "1.2.840.113549.1.9.1";

    private const string OidSan = "2.5.29.17";
    private const string OidKeyUsage = "2.5.29.15";
    private const string OidExtendedKeyUsage = "2.5.29.37";
    private const string OidBasicConstraints = "2.5.29.19";
    private const string OidSubjectKeyId = "2.5.29.14";
    private const string OidAuthorityKeyId = "2.5.29.35";
    private const string OidAia = "1.3.6.1.5.5.7.1.1";
    private const string OidCrl = "2.5.29.31";
    private const string OidPolicies = "2.5.29.32";
    private const string OidScts = "1.3.6.1.4.1.11129.2.4.2";
    private const string OidTlsFeature = "1.3.6.1.5.5.7.1.24";

    private static readonly Dictionary<string, string> PolicyLevels = new()
    {
        ["2.23.140.1.1"] = "EV",
        ["2.23.140.1.2.1"] = "DV",
        ["2.23.140.1.2.2"] = "OV",
        ["2.23.140.1.2.3"] = "IV",
    };

    private static readonly Dictionary<string, string> Curves = new()
    {
        ["1.2.840.10045.3.1.7"] = "P-256",
        ["1.3.132.0.34"] = "P-384",
        ["1.3.132.0.35"] = "P-521",
    };

    private static readonly Dictionary<string, string> Signatures = new()
    {
        ["1.2.840.113549.1.1.5"] = "SHA-1 with RSA",
        ["1.2.840.113549.1.1.11"] = "SHA-256 with RSA",
        ["1.2.840.113549.1.1.12"] = "SHA-384 with RSA",
        ["1.2.840.113549.1.1.13"] = "SHA-512 with RSA",
        ["1.2.840.113549.1.1.10"] = "RSASSA-PSS",
        ["1.2.840.10045.4.1"] = "ECDSA with SHA-1",
        ["1.2.840.10045.4.3.2"] = "ECDSA with SHA-256",
        ["1.2.840.10045.4.3.3"] = "ECDSA with SHA-384",
        ["1.2.840.10045.4.3.4"] = "ECDSA with SHA-512",
        ["1.3.101.112"] = "Ed25519",
        ["1.3.101.113"] = "Ed448",
    };

    private static readonly Dictionary<string, string> Usages = new()
    {
        ["1.3.6.1.5.5.7.3.1"] = "TLS server",
        ["1.3.6.1.5.5.7.3.2"] = "TLS client",
        ["1.3.6.1.5.5.7.3.3"] = "Code signing",
        ["1.3.6.1.5.5.7.3.4"] = "E-mail protection",
        ["1.3.6.1.5.5.7.3.8"] = "Time stamping",
        ["1.3.6.1.5.5.7.3.9"] = "OCSP signing",
    };

    public static string Sha256(byte[] der) => Convert.ToHexString(SHA256.HashData(der));

    public static string Sha256(X509Certificate2 certificate) => Sha256(certificate.RawData);

    public static string SpkiSha256(X509Certificate2 certificate) =>
        Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public static CertificateFacts Parse(X509Certificate2 certificate)
    {
        var truncated = false;
        var names = new List<string>();
        var keyUsage = new List<string>();
        var extendedKeyUsage = new List<string>();
        var ocsp = new List<string>();
        var caIssuers = new List<string>();
        var crl = new List<string>();
        var policies = new List<string>();
        var extensions = new List<CertificateExtension>();
        string? subjectKeyId = null, authorityKeyId = null;
        int? scts = null;
        var mustStaple = false;
        var isCa = false;

        foreach (var extension in certificate.Extensions)
        {
            var oid = extension.Oid?.Value ?? "";
            try
            {
                switch (oid)
                {
                    case OidSan:
                        var san = new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
                        foreach (var name in san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(a => a.ToString())))
                        {
                            if (names.Count >= MaxNames)
                            {
                                truncated = true;
                                break;
                            }
                            names.Add(Cut(name, 253));
                        }
                        break;
                    case OidKeyUsage:
                        keyUsage.AddRange(KeyUsages(new X509KeyUsageExtension(extension, extension.Critical).KeyUsages));
                        break;
                    case OidExtendedKeyUsage:
                        foreach (var usage in new X509EnhancedKeyUsageExtension(extension, extension.Critical).EnhancedKeyUsages)
                        {
                            extendedKeyUsage.Add(usage.Value is { } v && Usages.TryGetValue(v, out var label) ? label : usage.FriendlyName ?? usage.Value ?? "?");
                        }
                        break;
                    case OidBasicConstraints:
                        isCa = new X509BasicConstraintsExtension(extension, extension.Critical).CertificateAuthority;
                        break;
                    case OidSubjectKeyId:
                        subjectKeyId = new X509SubjectKeyIdentifierExtension(extension, extension.Critical).SubjectKeyIdentifier;
                        break;
                    case OidAuthorityKeyId:
                        var aki = new X509AuthorityKeyIdentifierExtension(extension.RawData, extension.Critical);
                        authorityKeyId = aki.KeyIdentifier is { } id ? Convert.ToHexString(id.Span) : null;
                        break;
                    case OidAia:
                        var aia = new X509AuthorityInformationAccessExtension(extension.RawData, extension.Critical);
                        ocsp.AddRange(aia.EnumerateOcspUris().Take(10).Select(u => Cut(u, 1000)));
                        caIssuers.AddRange(aia.EnumerateCAIssuersUris().Take(10).Select(u => Cut(u, 1000)));
                        break;
                    case OidCrl:
                        crl.AddRange(Uris(extension.RawData).Take(10).Select(u => Cut(u, 1000)));
                        break;
                    case OidPolicies:
                        policies.AddRange(PolicyOids(extension.RawData).Take(20));
                        break;
                    case OidScts:
                        scts = SctCount(extension.RawData);
                        break;
                    case OidTlsFeature:
                        mustStaple = TlsFeatures(extension.RawData).Contains(5);
                        break;
                }
            }
            catch (Exception e) when (e is CryptographicException or AsnContentException or ArgumentException or FormatException)
            {
                // A malformed extension stays in the list below with its raw text; the typed fields leave it out.
            }
            if (extensions.Count >= MaxExtensions)
            {
                truncated = true;
                continue;
            }
            string text;
            try
            {
                text = extension.Format(multiLine: true).Trim();
            }
            catch (CryptographicException)
            {
                text = Convert.ToHexString(extension.RawData);
            }
            extensions.Add(new CertificateExtension(oid, extension.Oid?.FriendlyName ?? oid, extension.Critical, Cut(text, MaxExtensionText)));
        }

        var subject = Name(certificate.SubjectName);
        var issuer = Name(certificate.IssuerName);
        truncated |= subject.Dn.Length >= MaxNameField || issuer.Dn.Length >= MaxNameField;
        var notBefore = certificate.NotBefore.ToUniversalTime();
        var notAfter = certificate.NotAfter.ToUniversalTime();
        var level = policies.Select(p => PolicyLevels.GetValueOrDefault(p)).FirstOrDefault(l => l is not null);
        return new CertificateFacts(
            Sha256(certificate),
            Convert.ToHexString(SHA1.HashData(certificate.RawData)),
            SpkiSha256(certificate),
            certificate.SerialNumber,
            certificate.Version,
            subject,
            issuer,
            names,
            DateTime.SpecifyKind(notBefore, DateTimeKind.Utc),
            DateTime.SpecifyKind(notAfter, DateTimeKind.Utc),
            (int)Math.Round((notAfter - notBefore).TotalDays),
            Key(certificate),
            certificate.SignatureAlgorithm.Value is { } sig && Signatures.TryGetValue(sig, out var signature)
                ? signature : certificate.SignatureAlgorithm.FriendlyName ?? certificate.SignatureAlgorithm.Value ?? "?",
            IsSelfSigned(certificate),
            isCa,
            level,
            keyUsage,
            extendedKeyUsage,
            subjectKeyId,
            authorityKeyId,
            ocsp,
            caIssuers,
            crl,
            policies,
            scts,
            mustStaple,
            extensions,
            truncated);
    }

    /// <summary>A CA certificate: basic constraints with CA:TRUE.</summary>
    public static bool IsCa(X509Certificate2 certificate)
    {
        try
        {
            return certificate.Extensions.Where(e => e.Oid?.Value == OidBasicConstraints)
                .Any(e => new X509BasicConstraintsExtension(e, e.Critical).CertificateAuthority);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The same subject and issuer, and no other key named as the issuer's.</summary>
    public static bool IsSelfSigned(X509Certificate2 certificate)
    {
        if (!certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
        {
            return false;
        }
        try
        {
            string? ski = null, aki = null;
            foreach (var extension in certificate.Extensions)
            {
                if (extension.Oid?.Value == OidSubjectKeyId)
                {
                    ski = new X509SubjectKeyIdentifierExtension(extension, extension.Critical).SubjectKeyIdentifier;
                }
                else if (extension.Oid?.Value == OidAuthorityKeyId)
                {
                    aki = new X509AuthorityKeyIdentifierExtension(extension.RawData, extension.Critical).KeyIdentifier is { } id
                        ? Convert.ToHexString(id.Span) : null;
                }
            }
            return aki is null || ski is null || string.Equals(aki, ski, StringComparison.OrdinalIgnoreCase);
        }
        catch (CryptographicException)
        {
            return true;
        }
    }

    public static CertificateName Name(X500DistinguishedName name)
    {
        string? Part(string oid)
        {
            var values = new List<string>();
            try
            {
                foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
                {
                    if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == oid && rdn.GetSingleElementValue() is { } value)
                    {
                        values.Add(value);
                    }
                }
            }
            catch (CryptographicException)
            {
                return null;
            }
            return values.Count == 0 ? null : Cut(string.Join(", ", values), MaxNameField);
        }
        return new CertificateName(Cut(name.Name, MaxNameField), Part(OidCommonName), Part(OidOrganization), Part(OidOrganizationalUnit),
            Part(OidLocality), Part(OidState), Part(OidCountry), Part(OidEmail));
    }

    public static CertificateKey Key(X509Certificate2 certificate)
    {
        var oid = certificate.PublicKey.Oid.Value;
        try
        {
            switch (oid)
            {
                case "1.2.840.113549.1.1.1":
                case "1.2.840.113549.1.1.10":
                    using (var rsa = certificate.GetRSAPublicKey())
                    {
                        return new CertificateKey("RSA", rsa?.KeySize, null);
                    }
                case "1.2.840.10045.2.1":
                    using (var ec = certificate.GetECDsaPublicKey())
                    {
                        var curve = ec?.ExportParameters(false).Curve.Oid;
                        var label = curve?.Value is { } c && Curves.TryGetValue(c, out var known) ? known : curve?.FriendlyName;
                        return new CertificateKey("ECDSA", ec?.KeySize, label);
                    }
                case "1.3.101.112":
                    return new CertificateKey("Ed25519", 256, null);
                case "1.3.101.113":
                    return new CertificateKey("Ed448", 456, null);
            }
        }
        catch (CryptographicException)
        {
        }
        return new CertificateKey(certificate.PublicKey.Oid.FriendlyName ?? oid ?? "?", null, null);
    }

    private static IEnumerable<string> KeyUsages(X509KeyUsageFlags flags)
    {
        (X509KeyUsageFlags Flag, string Label)[] all =
        [
            (X509KeyUsageFlags.DigitalSignature, "Digital signature"), (X509KeyUsageFlags.NonRepudiation, "Non-repudiation"),
            (X509KeyUsageFlags.KeyEncipherment, "Key encipherment"), (X509KeyUsageFlags.DataEncipherment, "Data encipherment"),
            (X509KeyUsageFlags.KeyAgreement, "Key agreement"), (X509KeyUsageFlags.KeyCertSign, "Certificate signing"),
            (X509KeyUsageFlags.CrlSign, "CRL signing"), (X509KeyUsageFlags.EncipherOnly, "Encipher only"),
            (X509KeyUsageFlags.DecipherOnly, "Decipher only"),
        ];
        return all.Where(a => flags.HasFlag(a.Flag)).Select(a => a.Label);
    }

    /// <summary>Every URI ([6] IA5String) of a structure: the CRL distribution points' full names.</summary>
    public static List<string> Uris(byte[] der)
    {
        var found = new List<string>();
        void Walk(ReadOnlyMemory<byte> data, int depth)
        {
            if (depth > 10)
            {
                return;
            }
            var reader = new AsnReader(data, AsnEncodingRules.DER);
            while (reader.HasData && found.Count < 50)
            {
                var tag = reader.PeekTag();
                if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6 && !tag.IsConstructed)
                {
                    found.Add(System.Text.Encoding.ASCII.GetString(reader.ReadOctetString(tag)));
                }
                else if (tag.IsConstructed)
                {
                    // Into the contents of the constructed value, past its tag and length.
                    Walk(Contents(reader.ReadEncodedValue()), depth + 1);
                }
                else
                {
                    reader.ReadEncodedValue();
                }
            }
        }
        Walk(der, 0);
        return found;
    }

    private static ReadOnlyMemory<byte> Contents(ReadOnlyMemory<byte> encoded)
    {
        AsnDecoder.ReadEncodedValue(encoded.Span, AsnEncodingRules.DER, out var contentOffset, out var contentLength, out _);
        return encoded.Slice(contentOffset, contentLength);
    }

    public static List<string> PolicyOids(byte[] der)
    {
        var result = new List<string>();
        var policies = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        while (policies.HasData)
        {
            var information = policies.ReadSequence();
            result.Add(information.ReadObjectIdentifier());
        }
        return result;
    }

    /// <summary>The SignedCertificateTimestampList inside its OCTET STRING: a 2-byte length, then entries with 2-byte lengths.</summary>
    public static int? SctCount(byte[] der)
    {
        var list = new AsnReader(der, AsnEncodingRules.DER).ReadOctetString();
        if (list.Length < 2)
        {
            return null;
        }
        var total = (list[0] << 8) | list[1];
        var end = Math.Min(list.Length, 2 + total);
        var position = 2;
        var count = 0;
        while (position + 2 <= end)
        {
            var length = (list[position] << 8) | list[position + 1];
            position += 2 + length;
            if (position > end)
            {
                break;
            }
            count++;
        }
        return count;
    }

    public static List<long> TlsFeatures(byte[] der)
    {
        var result = new List<long>();
        var features = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        while (features.HasData)
        {
            if (features.TryReadInt64(out var feature))
            {
                result.Add(feature);
            }
            else
            {
                features.ReadEncodedValue();
            }
        }
        return result;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
