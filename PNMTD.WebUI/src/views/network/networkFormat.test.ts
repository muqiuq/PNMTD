import { describe, expect, it } from 'vitest'
import {
  attachedLabel,
  edgeDash,
  edgeWidth,
  endLabel,
  kindLabel,
  matchLabel,
  nodeGlyph,
  parseFocus,
  portsLabel,
  progressLabel,
  stepLabel,
} from './networkFormat'

describe('networkFormat (docs/network-map-plan.md §8)', () => {
  it('names ends and parses the focus', () => {
    expect(endLabel({ name: 'office-gw', interface: 'ether1' })).toBe('office-gw · ether1')
    expect(endLabel({ name: 'srv-db', interface: null })).toBe('srv-db')
    expect(parseFocus('router:0f8fad5b-d9cb-469f-a165-70867728950e')).toEqual({
      kind: 'router',
      id: '0f8fad5b-d9cb-469f-a165-70867728950e',
    })
    expect(parseFocus('connection:0F8FAD5B-D9CB-469F-A165-70867728950E')?.kind).toBe('connection')
    expect(parseFocus('sensor:x')).toBeNull()
    expect(parseFocus(undefined)).toBeNull()
  })

  it('labels a device and the ports of its connections', () => {
    expect([nodeGlyph('Router', null), nodeGlyph('Host', 'Windows'), nodeGlyph('Host', 'Linux')]).toEqual([
      '⇄',
      'W',
      'L',
    ])
    expect(nodeGlyph('Host', null)).toBe('')
    expect([kindLabel('Router', null), kindLabel('Host', 'Linux'), kindLabel('Host', null)]).toEqual([
      'Router',
      'Linux server',
      'Host',
    ])
    expect(portsLabel('ether1', 'sfp1')).toBe('ether1 → sfp1')
    expect(portsLabel('ether1', null)).toBe('ether1')
    expect(portsLabel(null, 'sfp1')).toBe('→ sfp1')
    expect(portsLabel(null, undefined)).toBe('')
  })

  it('draws faster links wider, virtual ones dashed and suggestions dotted', () => {
    expect([edgeWidth(100), edgeWidth(1000), edgeWidth(10_000), edgeWidth(40_000)]).toEqual([1, 2, 3, 4.5])
    expect(edgeDash({ physical: true, type: 'baset-1g', suggestion: false })).toBeUndefined()
    expect(edgeDash({ physical: false, type: 'wireguard', suggestion: false })).toBe('7 5')
    expect(edgeDash({ physical: true, type: 'wifi', suggestion: false })).toBe('2 4')
    expect(edgeDash({ physical: true, type: null, suggestion: true })).toBe('1 5')
  })

  it('says where an interface is and what it is', () => {
    expect(attachedLabel({ attachedRouter: 'gw', attachedPort: 'ether7', attachedVlan: 20, attachedNote: null })).toBe(
      'gw · ether7 · VLAN 20',
    )
    expect(
      attachedLabel({ attachedRouter: 'gw', attachedPort: 'sfp1', attachedVlan: null, attachedNote: 'behind core-sw' }),
    ).toBe('gw · sfp1 (behind core-sw)')
    expect(
      matchLabel({ match: 'ServerAdapter', matchName: 'srv-db', matchInterface: 'eno1', assignedByName: null }),
    ).toBe('srv-db · eno1')
    expect(matchLabel({ match: 'Manual', matchName: 'printer', matchInterface: null, assignedByName: 'anna' })).toBe(
      'printer (by anna)',
    )
    expect(matchLabel({ match: 'None', matchName: null, matchInterface: null, assignedByName: null })).toBe(
      'Unassigned',
    )
  })

  it('tells how far a scan is', () => {
    expect(progressLabel({ total: 40, succeeded: 11, failed: 1, skipped: 2 })).toBe(
      '14 of 40 routers · 1 failed · 2 skipped · 26 left',
    )
    expect(progressLabel({ total: 1, succeeded: 1, failed: 0, skipped: 0 })).toBe('1 of 1 router')
    expect(stepLabel({ currentStep: 'the MAC table', currentStepNumber: 4, stepCount: 10 })).toBe(
      'the MAC table (step 4 of 10)',
    )
  })
})
