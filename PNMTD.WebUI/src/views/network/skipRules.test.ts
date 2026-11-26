import { describe, expect, it } from 'vitest'
import type { S } from '@/api/resources'
import {
  blankDraft,
  check,
  chips,
  draftFromSuggestion,
  macPrefix,
  parseVlans,
  requestOf,
  scopeLabel,
} from './skipRules'

const end = (kind: 'Router' | 'Host', id: string): S['NetworkEndItem'] => ({
  kind,
  hostId: `host-${id}`,
  sensorId: kind === 'Router' ? id : null,
  name: id,
  interface: null,
  siteId: null,
  site: null,
})

describe('skip rules', () => {
  it('parses VLAN lists as the server does', () => {
    expect(parseVlans(' 10, 20-29 ,99')).toEqual([
      { from: 10, to: 10 },
      { from: 20, to: 29 },
      { from: 99, to: 99 },
    ])
    expect(parseVlans('')).toEqual([])
    for (const bad of ['0', '4095', '20-10', 'ten', '1-2-3', ',', '-5']) expect(parseVlans(bad), bad).toBeNull()
  })

  it('normalises MAC prefixes', () => {
    expect(macPrefix('00-50-56')).toBe('00:50:56')
    expect(macPrefix('00:00:5e:00:01')).toBe('00:00:5E:00:01')
    for (const bad of ['0', 'zz', '00:11:22:33:44:55:66']) expect(macPrefix(bad), bad).toBeNull()
  })

  it('needs a condition, except on one router', () => {
    const all = { ...blankDraft(), name: 'Everything' }
    expect(check(all).scope).toBeTruthy()
    expect(check({ ...all, scope: 'Router', sensorId: 'r1' })).toEqual({})
    expect(check({ ...all, scope: 'Router' }).sensorId).toBeTruthy()
    expect(check({ ...all, vlans: '99' })).toEqual({})
    expect(check({ ...all, vlans: '5000', macPrefix: 'zz' })).toMatchObject({
      vlans: expect.any(String),
      macPrefix: expect.any(String),
    })
    expect(check({ ...all, name: ' ' }).name).toBeTruthy()
  })

  it('sends only the scope it names, and lower-case types', () => {
    const request = requestOf({
      ...blankDraft(),
      name: ' Guests ',
      scope: 'Location',
      locationId: 'l1',
      sensorId: 'r1',
      interfaceTypes: ['VLAN '],
      vlans: ' 99 ',
    })
    expect(request).toMatchObject({
      name: 'Guests',
      locationId: 'l1',
      sensorId: null,
      interfaceTypes: ['vlan'],
      vlans: '99',
    })
    expect(request.interfacePattern).toBeNull()
  })

  it('names the conditions and the scope', () => {
    expect(
      chips({
        interfacePattern: 'vlan*',
        interfaceTypes: ['vlan'],
        vlans: '99',
        evidence: ['Cdp'],
        macPrefix: '00:50:56',
      }),
    ).toEqual(['vlan*', 'type vlan', 'VLAN 99', 'CDP', 'MAC 00:50:56…'])
    expect(scopeLabel({ scope: 'All', locationName: null, routerName: null })).toBe('All routers')
    expect(scopeLabel({ scope: 'Router', locationName: null, routerName: 'office-gw' })).toBe('office-gw')
  })

  it('starts Skip like this… from the evidence: its router, interface as printed, VLAN and table', () => {
    const suggestion = {
      a: end('Router', 'r1'),
      b: end('Router', 'r2'),
      evidence: [
        { rule: 'second-path', confidence: 'Low', text: 'linked already' },
        {
          rule: 'neighbor',
          confidence: 'High',
          text: 'a sees b',
          routerId: 'r2',
          interface: 'vlan20',
          vlan: 20,
          by: 'mndp,lldp',
        },
      ],
    } as unknown as S['NetworkSuggestionItem']
    const draft = draftFromSuggestion(suggestion)
    expect(draft).toMatchObject({
      scope: 'Router',
      sensorId: 'r2',
      interfacePattern: 'vlan20',
      vlans: '20',
      name: 'Skip vlan20',
    })
    expect(draft.evidence).toEqual(['Mndp', 'Lldp'])
    const mac = draftFromSuggestion({
      a: end('Router', 'r1'),
      b: end('Host', 'h1'),
      evidence: [
        { rule: 'mac-table-host', confidence: 'Medium', text: 'learned', routerId: 'r1', interface: 'ether2' },
      ],
    } as unknown as S['NetworkSuggestionItem'])
    expect(mac).toMatchObject({ sensorId: 'r1', interfacePattern: 'ether2', vlans: '', evidence: ['MacTable'] })
  })
})
