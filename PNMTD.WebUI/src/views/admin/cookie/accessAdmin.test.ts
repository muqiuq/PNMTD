import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  accessWarnings,
  contactKey,
  contactOf,
  knownForm,
  knownProblems,
  knownRequest,
  knownState,
  normalizeAccess,
  prepareAccess,
  routerLocations,
  routersToSwitch,
  type TargetOption,
} from './accessAdmin'

const now = Date.parse('2026-11-15T10:00:00Z')
const day = 86_400_000

const state = (patch: Partial<S['AccessAdminState']> = {}): S['AccessAdminState'] => ({
  cookieReadsLogins: true,
  cookieReadsSignIns: true,
  publicBaseUrl: true,
  nightContact: true,
  noticeAuto: true,
  pnmtOneAddress: false,
  routersNotWatched: 0,
  routersWithGap: [],
  contacts: [],
  openCases: 0,
  reviewsToday: 0,
  noticesToday: 0,
  ...patch,
})

const router = (sensorId: string, locationId: string | null, watch: boolean): S['AccessRouterItem'] => ({
  sensorId,
  name: 'MikroTik',
  hostName: `rtr-${sensorId}`,
  locationId,
  locationName: locationId ? `Site ${locationId}` : null,
  watch,
  firstReadAt: null,
  gap: false,
})

describe('Admin → Cookie → Access (docs/cookie-access-plan.md §8)', () => {
  it('fills what the settings lack with the defaults of §6.1 and §8', () => {
    const v = normalizeAccess({
      on: true,
      notices: { unusual: { notify: false, email: true, pushover: false, ask: true } },
    })
    expect(v.on).toBe(true)
    expect(v.notices.unusual).toEqual({ notify: false, email: true, pushover: false, hours: 'Wait', ask: true })
    expect(v.notices.suspicious).toEqual({ notify: true, email: true, pushover: true, hours: 'Allowed', ask: true })
    expect(v.notices.alarming?.hours).toBe('Everyone')
    expect(v.kinds.adminNetwork).toBe(true)
    expect(v.kinds.explicit).toBe(false)
    expect(v.wasThisYou.validHours).toBe(24)
    expect(v.historyDays).toBe(90)
    expect(v.learningDays).toBe(14)
    expect(v.limits).toEqual({ perContactDay: 20, perContactHour: 5 })
  })

  it('sends the texts trimmed', () => {
    const v = normalizeAccess({
      trusted: [{ name: ' Office LAN ', cidr: ' 10.0.0.0/8 ' }],
      excluded: [{ account: ' backup ', comment: ' Veeam ' }],
    })
    const sent = prepareAccess(v)
    expect(sent.trusted).toEqual([{ name: 'Office LAN', cidr: '10.0.0.0/8' }])
    expect(sent.excluded).toEqual([{ account: 'backup', sensorId: null, comment: 'Veeam' }])
    expect(sent.links).toEqual({ key: true, serverAccounts: true, username: true })
  })

  it('warns as §8 lists, and not when all is well', () => {
    const v = normalizeAccess({ contacts: [contactOf('user:u1')!] })
    expect(accessWarnings(state(), v)).toEqual([])
    const keys = accessWarnings(
      state({
        cookieReadsLogins: false,
        cookieReadsSignIns: false,
        publicBaseUrl: false,
        nightContact: false,
        noticeAuto: false,
        pnmtOneAddress: true,
        routersNotWatched: 2,
        routersWithGap: ['rtr-zurich'],
        contacts: [{ name: 'Anna', problem: 'has neither an e-mail address nor a Pushover key.' }],
      }),
      normalizeAccess({}),
    ).map((w) => w.key)
    expect(keys).toEqual([
      'no-contact',
      'cookie-logins',
      'cookie-sign-ins',
      'public-base-url',
      'night',
      'notice-level',
      'one-address',
      'routers',
      'gap',
      'contact-0',
    ])
  })

  it('says when no way links accounts to people, and that Was this you? then asks nobody on servers', () => {
    const v = normalizeAccess({
      contacts: [contactOf('group:g1')!],
      links: { key: false, serverAccounts: false, username: false },
    })
    expect(accessWarnings(state(), v).map((w) => w.key)).toEqual(['ask-links', 'links'])
    const pnmtOnly = { ...v, wasThisYou: { ...v.wasThisYou, servers: false, routers: false } }
    expect(accessWarnings(state(), pnmtOnly).map((w) => w.key)).toEqual(['links'])
  })

  it('makes a contact for every target, reached in working hours only', () => {
    expect(contactOf('user:u1')).toEqual({
      userId: 'u1',
      groupId: null,
      servers: true,
      routers: true,
      pnmt: true,
      outsideHours: false,
    })
    expect(contactOf('group:g1')?.groupId).toBe('g1')
    expect(contactOf('nobody')).toBeNull()
    expect(contactKey({ userId: null, groupId: 'g1' })).toBe('group:g1')
    expect(contactKey(contactOf('user:u1')!)).toBe('user:u1')
  })
})

describe("The routers' switches (docs/cookie-access-plan.md §4.2)", () => {
  const routers = [router('a', 'l2', true), router('b', 'l1', false), router('c', null, false), router('d', 'l1', true)]

  it('lists their locations by name, those without one last', () => {
    expect(routerLocations(routers)).toEqual([
      { label: 'Site l1', value: 'l1' },
      { label: 'Site l2', value: 'l2' },
      { label: 'Without a location', value: 'none' },
    ])
    expect(routerLocations([router('a', 'l1', true)])).toEqual([{ label: 'Site l1', value: 'l1' }])
  })

  it('switches only those not already as asked: all, a location, or those without one', () => {
    expect(routersToSwitch(routers, null, true)).toEqual(['b', 'c'])
    expect(routersToSwitch(routers, null, false)).toEqual(['a', 'd'])
    expect(routersToSwitch(routers, 'l1', true)).toEqual(['b'])
    expect(routersToSwitch(routers, 'none', true)).toEqual(['c'])
    expect(routersToSwitch(routers, 'l2', true)).toEqual([])
  })
})

describe('Known access (docs/cookie-access-plan.md §4.4, §7)', () => {
  const targets: TargetOption[] = [
    { label: 'PNMT', value: 'pnmt', kind: 'Pnmt' },
    { label: 'srv-01', value: 's1', kind: 'Server' },
    { label: 'rtr-zurich', value: 'r1', kind: 'Router' },
  ]

  it('wants a target or a location, and a reason', () => {
    expect(knownProblems(knownForm(), now)).toEqual({ sensorId: 'Choose the target.', comment: 'Say why.' })
    expect(knownProblems({ ...knownForm(), scope: 'location', comment: 'Vendor' }, now)).toEqual({
      locationId: 'Choose the location.',
    })
    expect(knownProblems({ ...knownForm(), scope: 'all', comment: 'Vendor' }, now)).toEqual({})
    expect(
      knownProblems({ ...knownForm(), scope: 'all', comment: 'x', until: new Date(now - 1) }, now).until,
    ).toBeTruthy()
  })

  it("sends a router's kind with its sensor, PNMT without one, and empty fields as any", () => {
    const f = { ...knownForm(), target: 'r1', account: ' admin ', source: ' 203.0.113.0/24 ', comment: ' Vendor ' }
    expect(knownRequest(f, targets)).toEqual({
      scope: 'target',
      target: 'Router',
      sensorId: 'r1',
      locationId: null,
      account: 'admin',
      source: '203.0.113.0/24',
      groups: null,
      until: null,
      comment: 'Vendor',
    })
    expect(knownRequest({ ...knownForm(), target: 'pnmt', comment: 'x' }, targets)).toMatchObject({
      target: 'Pnmt',
      sensorId: null,
    })
    const until = new Date(now + 30 * day)
    expect(
      knownRequest(
        {
          ...knownForm(),
          scope: 'location',
          locationId: 'l1',
          kind: 'Server',
          groups: ['remote'],
          until,
          comment: 'x',
        },
        targets,
      ),
    ).toMatchObject({
      scope: 'location',
      target: 'Server',
      sensorId: null,
      locationId: 'l1',
      groups: ['remote'],
      until: until.toISOString(),
    })
  })

  it('tells active entries from ended and expired ones', () => {
    expect(knownState({ endedAt: null, until: null }, now)).toBe('active')
    expect(knownState({ endedAt: null, until: new Date(now + day).toISOString() }, now)).toBe('active')
    expect(knownState({ endedAt: null, until: new Date(now - day).toISOString() }, now)).toBe('expired')
    expect(knownState({ endedAt: new Date(now).toISOString(), until: null }, now)).toBe('ended')
  })
})
