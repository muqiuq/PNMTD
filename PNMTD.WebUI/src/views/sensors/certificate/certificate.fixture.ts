import type { S } from '@/api/resources'

/** An SSL certificate sensor for the component tests (docs/ssl-certificate-plan.md §10): its summary, items and detail. */

export const SENSOR_ID = '11111111-1111-4111-8111-111111111111'
export const HOST_ID = '22222222-2222-4222-8222-222222222222'
export const OLD_SHA = 'A1'.repeat(32)
export const NEW_SHA = 'B2'.repeat(32)
export const NEWER_SHA = 'C3'.repeat(32)

export function certificateName(cn: string, o: string | null = null): S['CertificateName'] {
  return {
    dn: o ? `CN=${cn}, O=${o}` : `CN=${cn}`,
    commonName: cn,
    organization: o,
    organizationalUnit: null,
    locality: null,
    state: null,
    country: null,
    email: null,
  }
}

export function facts(patch: Partial<S['CertificateFacts']> = {}): S['CertificateFacts'] {
  return {
    sha256: NEW_SHA,
    sha1: 'D4'.repeat(20),
    spkiSha256: 'bmV3IGtleQ==',
    serial: '04F1A2',
    version: 3,
    subject: certificateName('shop.example.com'),
    issuer: certificateName('ZeroSSL RSA Domain Secure Site CA', 'ZeroSSL'),
    names: ['shop.example.com', 'www.shop.example.com'],
    notBefore: '2026-11-01T00:00:00Z',
    notAfter: '2027-02-01T12:00:00Z',
    lifetimeDays: 92,
    key: { algorithm: 'ECDSA', size: 256, curve: 'P-256', label: 'ECDSA P-256' },
    signatureAlgorithm: 'SHA-384 with RSA',
    selfSigned: false,
    isCa: false,
    validationLevel: 'DV',
    keyUsage: ['Digital signature'],
    extendedKeyUsage: ['TLS server'],
    subjectKeyId: 'AB:CD',
    authorityKeyId: 'EF:01',
    ocspUrls: ['http://ocsp.example.com'],
    caIssuerUrls: [],
    crlUrls: [],
    policies: ['2.23.140.1.2.1'],
    scts: 2,
    mustStaple: false,
    extensions: [
      { oid: '2.5.29.17', name: 'Subject Alternative Name', critical: false, value: 'DNS:shop.example.com' },
    ],
    truncated: false,
    ...patch,
  }
}

export function chain(leaf: S['CertificateFacts']): S['CertificateChainEntry'][] {
  return [
    {
      depth: 0,
      subject: leaf.subject,
      issuer: leaf.issuer,
      notBefore: leaf.notBefore,
      notAfter: leaf.notAfter,
      sha256: leaf.sha256,
      key: 'ECDSA P-256',
      source: 'Server',
      sourceName: null,
      trustAnchor: false,
      selfSigned: leaf.selfSigned,
    },
    {
      depth: 1,
      subject: leaf.issuer,
      issuer: certificateName('USERTrust RSA Certification Authority'),
      notBefore: '2020-01-30T00:00:00Z',
      notAfter: '2030-01-29T23:59:59Z',
      sha256: 'E5'.repeat(32),
      key: 'RSA 2048',
      source: 'Server',
      sourceName: null,
      trustAnchor: false,
      selfSigned: false,
    },
    {
      depth: 2,
      subject: certificateName('USERTrust RSA Certification Authority'),
      issuer: certificateName('USERTrust RSA Certification Authority'),
      notBefore: '2010-02-01T00:00:00Z',
      notAfter: '2038-01-18T23:59:59Z',
      sha256: 'F6'.repeat(32),
      key: 'RSA 4096',
      source: 'PublicRoot',
      sourceName: null,
      trustAnchor: true,
      selfSigned: true,
    },
  ]
}

export function item(f: S['CertificateFacts'], id = `v-${f.sha256.slice(0, 4)}`): S['CertificateItem'] {
  return { id, facts: f, chain: chain(f), firstSeenAt: '2026-11-17T10:00:00Z', lastSeenAt: '2026-11-17T10:00:00Z' }
}

export function summary(patch: Partial<S['CertificateSensorSummary']> = {}): S['CertificateSensorSummary'] {
  return {
    host: 'shop.example.com',
    port: 443,
    serverName: null,
    connectTo: null,
    trust: 'Public',
    authorities: [],
    checkName: true,
    checkExpiry: true,
    incompleteChainFails: false,
    warnDays: 14,
    alertOnCertificateChange: false,
    alertOnKeyChange: true,
    alertOnIssuerChange: true,
    issuerMatch: 'Organization',
    lastCheckAt: '2026-11-17T10:00:00Z',
    nextCheckAt: '2026-11-18T10:00:00Z',
    lastSuccessAt: '2026-11-17T10:00:00Z',
    checking: false,
    consecutiveFailures: 0,
    firstFailureAt: null,
    lastError: null,
    expiresAt: '2027-02-01T12:00:00Z',
    daysLeft: 76,
    issuer: 'ZeroSSL RSA Domain Secure Site CA',
    issuerOrganization: 'ZeroSSL',
    subjectCommonName: 'shop.example.com',
    selfSigned: false,
    acceptedAt: '2026-08-01T00:00:00Z',
    acceptedBy: 'first check',
    changePending: null,
    ...patch,
  }
}

export function validation(patch: Partial<S['CertificateValidation']> = {}): S['CertificateValidation'] {
  return {
    trusted: true,
    trustedBy: 'USERTrust RSA Certification Authority',
    publiclyTrusted: true,
    nameMatches: true,
    complete: true,
    sendsRoot: false,
    selfSigned: false,
    chainExpiresAt: '2027-02-01T12:00:00Z',
    chainExpiresSubject: 'shop.example.com',
    chainExpiresIsLeaf: true,
    problems: [],
    ...patch,
  }
}

/** The accepted certificate against a new one: a new key, and another issuer (§8.4). */
export function changed(
  current = facts(),
  patch: Partial<S['CertificateSensorDetail']> = {},
): S['CertificateSensorDetail'] {
  const accepted = facts({
    sha256: OLD_SHA,
    spkiSha256: 'b2xkIGtleQ==',
    serial: '03E0B1',
    issuer: certificateName('R11', "Let's Encrypt"),
  })
  return {
    summary: summary({
      changePending: {
        kinds: ['new key', "issuer Let's Encrypt → ZeroSSL"],
        since: '2026-11-17T10:00:00Z',
        certificate: true,
        key: true,
        issuer: true,
      },
    }),
    current: item(current),
    accepted: item(accepted),
    acceptedNote: null,
    change: {
      certificate: true,
      key: true,
      issuer: true,
      alarm: true,
      kinds: ['new key', "issuer Let's Encrypt → ZeroSSL"],
      since: '2026-11-17T10:00:00Z',
      fields: [
        { field: 'SHA-256', accepted: OLD_SHA, current: current.sha256, changed: true },
        { field: 'Key', accepted: 'ECDSA P-256', current: 'ECDSA P-256', changed: false },
        { field: 'Key (SPKI SHA-256)', accepted: accepted.spkiSha256, current: current.spkiSha256, changed: true },
        { field: 'Issuer', accepted: accepted.issuer.dn, current: current.issuer.dn, changed: true },
        { field: 'Subject', accepted: 'CN=shop.example.com', current: 'CN=shop.example.com', changed: false },
        { field: 'Serial', accepted: '03E0B1', current: current.serial, changed: true },
      ],
    },
    connection: {
      address: '192.0.2.10',
      port: 443,
      serverName: 'shop.example.com',
      protocol: 'TLS 1.3',
      cipherSuite: 'TLS_AES_256_GCM_SHA384',
      handshakeMs: 42,
    },
    validation: validation(),
    versionCount: 2,
    ...patch,
  }
}

export function sensor(c: S['CertificateSensorSummary'], patch: Partial<S['SensorDetail']> = {}): S['SensorDetail'] {
  return {
    id: SENSOR_ID,
    host: { id: HOST_ID, name: 'proxy-01' },
    parentSensorId: null,
    name: 'shop.example.com:443',
    type: 'SSL_CERTIFICATE',
    enabled: true,
    ignore: false,
    interval: 86_400,
    gracePeriod: 0,
    parameters: 'shop.example.com:443',
    textId: null,
    status: null,
    created: '2026-08-01T00:00:00Z',
    state: 'Ok',
    lastCode: 285,
    lastMessage: null,
    lastEventAt: null,
    failingSince: null,
    uptime24Hours: 1,
    uptime30Days: 1,
    days: { start: '2026-10-18', values: [] },
    children: [],
    integration: null,
    certificate: c,
    ...patch,
  }
}
