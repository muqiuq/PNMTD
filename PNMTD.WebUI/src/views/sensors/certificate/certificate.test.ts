import { describe, expect, it } from 'vitest'
import {
  acceptedText,
  compareFacts,
  daysLeft,
  daysLeftText,
  derToPem,
  fingerprint,
  intervalError,
  intervalLabel,
  opensslCommand,
  parseAddress,
  pemBlocks,
  pemOfFile,
  shortFingerprint,
  trustDefaults,
  trustLabel,
  validationLevelLabel,
  validityHeadline,
  warningLengthNote,
} from './certificate'
import type { S } from '@/api/resources'

const DAY = 86_400_000
const now = Date.parse('2026-11-17T12:00:00Z')

describe('the address (docs/ssl-certificate-plan.md §4.3)', () => {
  const ok = (text: string) => {
    const parsed = parseAddress(text)
    if ('error' in parsed) throw new Error(parsed.error)
    return parsed.normalized
  }
  const error = (text: string) => {
    const parsed = parseAddress(text)
    return 'error' in parsed ? parsed.error : null
  }

  it('takes a host, host:port and an https:// URL, without its path', () => {
    expect(ok('Shop.Example.com')).toBe('shop.example.com:443')
    expect(ok('example.com:8443')).toBe('example.com:8443')
    expect(ok('https://example.com/path?q=1#top')).toBe('example.com:443')
    expect(ok('https://user@example.com:9443/')).toBe('example.com:9443')
    expect(ok('imaps://mail.example.com')).toBe('mail.example.com:993')
    expect(ok('  example.com.  ')).toBe('example.com:443')
  })

  it('takes IPv4, IPv6 in brackets with a port, and a bare IPv6 address on 443', () => {
    expect(ok('10.0.0.5:443')).toBe('10.0.0.5:443')
    expect(ok('[2001:db8::1]:8443')).toBe('[2001:db8::1]:8443')
    expect(ok('[2001:db8::1]')).toBe('[2001:db8::1]:443')
    expect(ok('2001:db8::1')).toBe('[2001:db8::1]:443')
    expect(ok('2001:DB8:0:0::1')).toBe('[2001:db8::1]:443')
  })

  it('keeps an international name as typed', () => {
    expect(ok('bücher.example')).toBe('bücher.example:443')
  })

  it('says what is wrong', () => {
    expect(error('')).toContain('host, host:port')
    expect(error('ftp://example.com')).toContain('ftp:// is not one')
    expect(error('example.com:0')).toContain('A port from 1 to 65535')
    expect(error('example.com:99999')).toContain('A port from 1 to 65535')
    expect(error('[2001:db8::1')).toContain('in brackets')
    expect(error('[2001:db8::1]443')).toContain('after the address')
    expect(error('2001:db8::zz')).toContain('in brackets')
    expect(error('exa mple.com')).toBe('A host name or an IP address.')
    expect(error('-bad.example')).toBe('A host name or an IP address.')
    expect(error('host.123')).toBe('A host name or an IP address.')
  })
})

describe('the defaults per trust mode (§5.4)', () => {
  it('fails on a new issuer and checks the name in the CA modes', () => {
    for (const trust of ['Public', 'Private'] as const) {
      expect(trustDefaults(trust)).toEqual({
        checkName: true,
        checkExpiry: true,
        incompleteChainFails: false,
        alertOnCertificateChange: false,
        alertOnKeyChange: false,
        alertOnIssuerChange: true,
      })
    }
  })

  it('pins the certificate, without the name, in the pinned mode', () => {
    expect(trustDefaults('Pinned')).toEqual({
      checkName: false,
      checkExpiry: true,
      incompleteChainFails: false,
      alertOnCertificateChange: true,
      alertOnKeyChange: false,
      alertOnIssuerChange: false,
    })
  })

  it('names the trust for the status strip', () => {
    expect(trustLabel({ trust: 'Public', authorities: [] })).toBe('public CAs')
    expect(trustLabel({ trust: 'Pinned', authorities: [] })).toBe('pinned')
    expect(trustLabel({ trust: 'Private', authorities: [{ id: '1', name: 'Contoso Root CA' }] })).toBe(
      'Contoso Root CA',
    )
  })

  it('says who accepted the certificate', () => {
    expect(acceptedText('first check', true)).toBe('Pinned at the first check')
    expect(acceptedText('automatically (renewal)', false)).toBe('Accepted automatically (renewal)')
    expect(acceptedText('anna', false)).toBe('Accepted by anna')
  })
})

describe('the days left, counted live (§3, §8.3)', () => {
  it('counts whole days as the server does', () => {
    expect(daysLeft(new Date(now + 76 * DAY + 3_600_000).toISOString(), now)).toBe(76)
    expect(daysLeft(new Date(now + 3_600_000).toISOString(), now)).toBe(0)
    expect(daysLeft(new Date(now - 3_600_000).toISOString(), now)).toBe(-1)
    expect(daysLeft(null, now)).toBeNull()
  })

  it('words the days left and the headline', () => {
    expect(daysLeftText(new Date(now + 76 * DAY + 60_000).toISOString(), now)).toBe('76 days left')
    expect(daysLeftText(new Date(now + DAY + 60_000).toISOString(), now)).toBe('1 day left')
    expect(daysLeftText(new Date(now - 2 * DAY - 60_000).toISOString(), now)).toBe('expired 2 days ago')
    expect(validityHeadline(new Date(now + 76 * DAY + 60_000).toISOString(), 14, now)).toEqual({
      text: 'Valid · 76 days left',
      tone: 'ok',
    })
    expect(validityHeadline(new Date(now + 5 * DAY + 60_000).toISOString(), 14, now)).toEqual({
      text: 'Expires in 5 days',
      tone: 'warn',
    })
    // At exactly the warning's days, it warns (§10: the warning at exactly WarnDays).
    expect(validityHeadline(new Date(now + 14 * DAY + 60_000).toISOString(), 14, now).tone).toBe('warn')
    expect(validityHeadline(new Date(now - 2 * DAY - 60_000).toISOString(), 14, now)).toEqual({
      text: 'Expired 2 days ago',
      tone: 'fail',
    })
  })

  it("warns when the warning is long for the certificate's lifetime (§5.4)", () => {
    expect(warningLengthNote(14, 90)).toBeNull()
    expect(warningLengthNote(14, 30)).toContain('more than a third')
    expect(warningLengthNote(14, 6)).toContain('always about to expire')
    expect(warningLengthNote(14, null)).toBeNull()
  })
})

describe('the interval (R18: 1 hour to 30 days)', () => {
  it('names the presets, 0 as daily, and other intervals', () => {
    expect(intervalLabel(3_600)).toBe('Hourly')
    expect(intervalLabel(86_400)).toBe('Daily')
    expect(intervalLabel(0)).toBe('Daily')
    expect(intervalLabel(2_592_000)).toBe('Monthly (30 days)')
    expect(intervalLabel(3 * 86_400)).toBe('Every 3 days')
    expect(intervalLabel(2 * 3_600)).toBe('Every 2 hours')
  })

  it('takes 3600 to 2592000 seconds only', () => {
    expect(intervalError(3_600)).toBeUndefined()
    expect(intervalError(2_592_000)).toBeUndefined()
    expect(intervalError(3_599)).toBeTruthy()
    expect(intervalError(2_592_001)).toBeTruthy()
    expect(intervalError(undefined)).toBeTruthy()
  })
})

describe('labels and the command (§8.3, §8.4)', () => {
  it('names the validation level', () => {
    expect(validationLevelLabel('DV')).toBe('Domain validated (DV)')
    expect(validationLevelLabel('EV')).toBe('Extended validation (EV)')
    expect(validationLevelLabel(null)).toBeNull()
  })

  it('shows fingerprints as openssl does', () => {
    expect(fingerprint('ab01cd')).toBe('AB:01:CD')
    expect(shortFingerprint('00112233445566778899AABB')).toBe('00:11:22:33:44:55:66:77…')
  })

  it('fills the openssl command in for the endpoint', () => {
    expect(opensslCommand({ host: 'shop.example.com', port: 443, serverName: null, connectTo: null })).toBe(
      'openssl s_client -connect shop.example.com:443 -servername shop.example.com </dev/null 2>/dev/null \\\n' +
        '  | openssl x509 -noout -fingerprint -sha256',
    )
    expect(
      opensslCommand({ host: 'shop.example.com', port: 8443, serverName: 'www.example.com', connectTo: '10.0.0.7' }),
    ).toContain('-connect 10.0.0.7:8443 -servername www.example.com')
    expect(opensslCommand({ host: '2001:db8::1', port: 443, serverName: null, connectTo: null })).toContain(
      '-connect [2001:db8::1]:443 </dev/null',
    )
  })
})

describe('the comparison (§8.4)', () => {
  const facts = (patch: Partial<S['CertificateFacts']> = {}): S['CertificateFacts'] => ({
    sha256: 'AA',
    sha1: 'BB',
    spkiSha256: 'key1',
    serial: '01',
    version: 3,
    subject: name('CN=shop.example.com'),
    issuer: name("CN=R11, O=Let's Encrypt"),
    names: ['shop.example.com', 'www.example.com'],
    notBefore: '2026-08-01T00:00:00Z',
    notAfter: '2026-10-30T00:00:00Z',
    lifetimeDays: 90,
    key: { algorithm: 'ECDSA', size: 256, curve: 'P-256', label: 'ECDSA P-256' },
    signatureAlgorithm: 'ECDSA with SHA-384',
    selfSigned: false,
    isCa: false,
    validationLevel: 'DV',
    keyUsage: [],
    extendedKeyUsage: [],
    subjectKeyId: null,
    authorityKeyId: null,
    ocspUrls: [],
    caIssuerUrls: [],
    crlUrls: [],
    policies: [],
    scts: 2,
    mustStaple: false,
    extensions: [],
    truncated: false,
    ...patch,
  })
  function name(dn: string): S['CertificateName'] {
    return {
      dn,
      commonName: null,
      organization: null,
      organizationalUnit: null,
      locality: null,
      state: null,
      country: null,
      email: null,
    }
  }

  it('marks the fields that differ, and the names added and removed', () => {
    const fields = compareFacts(
      facts(),
      facts({ sha256: 'CC', serial: '02', names: ['shop.example.com', 'api.example.com'] }),
    )
    const changed = fields.filter((f) => f.changed).map((f) => f.field)
    expect(changed).toEqual(['SHA-256', 'Names', 'Names added', 'Names removed', 'Serial'])
    expect(fields.find((f) => f.field === 'Names added')?.current).toBe('api.example.com')
    expect(fields.find((f) => f.field === 'Names removed')?.accepted).toBe('www.example.com')
  })
})

describe('PEM (§8.5)', () => {
  it('splits a chain, and wraps binary DER as PEM', () => {
    const one = derToPem(Uint8Array.from([0x30, ...Array.from({ length: 100 }, (_, i) => i)]))
    expect(one.startsWith('-----BEGIN CERTIFICATE-----\nM')).toBe(true)
    expect(one.trim().endsWith('-----END CERTIFICATE-----')).toBe(true)
    expect(one.split('\n')[1]!.length).toBe(64)
    expect(pemBlocks(`${one}${one}`)).toHaveLength(2)
    expect(pemOfFile(Uint8Array.from([0x30, ...Array.from({ length: 100 }, (_, i) => i)]))).toBe(one)
    expect(pemOfFile(new TextEncoder().encode(one))).toBe(one)
  })
})
