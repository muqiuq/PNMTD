import type { S } from '@/api/resources'

/**
 * The skip rules' words and checks (docs/network-suggestion-rules-plan.md §4.1, §4.5): a draft as the form holds it, the
 * request it sends, the checks the server makes too, the chips the table shows, and the draft *Skip like this…* starts from.
 */

export type SkipRule = S['NetworkSkipRuleItem']
export type SkipEvidence = S['NetworkSkipEvidence']

export const evidenceLabels: Record<SkipEvidence, string> = {
  MacTable: 'MAC table',
  Arp: 'ARP',
  Mndp: 'MNDP',
  Lldp: 'LLDP',
  Cdp: 'CDP',
  Wifi: 'Wi-Fi',
  WireGuard: 'WireGuard',
  Tunnel: 'Tunnel',
}

export const evidenceOptions = (Object.keys(evidenceLabels) as SkipEvidence[]).map((value) => ({
  value,
  label: evidenceLabels[value],
}))

export interface RuleDraft {
  name: string
  enabled: boolean
  scope: S['NetworkSkipRuleScope']
  locationId: string | null
  sensorId: string | null
  interfacePattern: string
  interfaceTypes: string[]
  vlans: string
  evidence: SkipEvidence[]
  macPrefix: string
}

export function blankDraft(): RuleDraft {
  return {
    name: '',
    enabled: true,
    scope: 'All',
    locationId: null,
    sensorId: null,
    interfacePattern: '',
    interfaceTypes: [],
    vlans: '',
    evidence: [],
    macPrefix: '',
  }
}

export function draftOf(rule: SkipRule): RuleDraft {
  return {
    name: rule.name,
    enabled: rule.enabled,
    scope: rule.scope,
    locationId: rule.locationId,
    sensorId: rule.sensorId,
    interfacePattern: rule.interfacePattern ?? '',
    interfaceTypes: [...rule.interfaceTypes],
    vlans: rule.vlans ?? '',
    evidence: [...rule.evidence],
    macPrefix: rule.macPrefix ?? '',
  }
}

const trimmed = (text: string) => (text.trim() ? text.trim() : null)

export function requestOf(d: RuleDraft): S['NetworkSkipRuleRequest'] {
  return {
    name: d.name.trim(),
    enabled: d.enabled,
    scope: d.scope,
    locationId: d.scope === 'Location' ? d.locationId : null,
    sensorId: d.scope === 'Router' ? d.sensorId : null,
    interfacePattern: trimmed(d.interfacePattern),
    interfaceTypes: d.interfaceTypes.map((t) => t.trim().toLowerCase()).filter(Boolean),
    vlans: trimmed(d.vlans),
    evidence: d.evidence,
    macPrefix: trimmed(d.macPrefix),
  }
}

/** "10,20-29, 99" as ranges; null when a part is no VLAN ID (1–4094) or range. An empty text is no ranges. */
export function parseVlans(text: string): { from: number; to: number }[] | null {
  if (!text.trim()) return []
  const ranges: { from: number; to: number }[] = []
  for (const part of text
    .split(',')
    .map((p) => p.trim())
    .filter(Boolean)) {
    const ends = part.split('-').map((e) => e.trim())
    if (ends.length > 2 || !ends.every((e) => /^\d+$/.test(e))) return null
    const [from, to] = [Number(ends[0]), Number(ends[ends.length - 1])]
    if (from < 1 || to > 4094 || to < from) return null
    ranges.push({ from, to })
  }
  return ranges.length ? ranges : null
}

/** "00:50:56", "00-50-56", "005056": 1 to 6 octets, upper case with colons; null when it is none. */
export function macPrefix(text: string): string | null {
  const hex = text.replace(/[:.\-\s]/g, '').toUpperCase()
  if (!hex || hex.length > 12 || hex.length % 2 !== 0 || !/^[0-9A-F]+$/.test(hex)) return null
  return hex.match(/../g)!.join(':')
}

export function hasCondition(d: RuleDraft): boolean {
  return (
    !!d.interfacePattern.trim() ||
    d.interfaceTypes.length > 0 ||
    (parseVlans(d.vlans)?.length ?? 0) > 0 ||
    d.evidence.length > 0 ||
    !!d.macPrefix.trim()
  )
}

/** The form's checks, as the server makes them: field → message. */
export function check(d: RuleDraft): Record<string, string> {
  const errors: Record<string, string> = {}
  if (!d.name.trim()) errors.name = 'A name.'
  else if (d.name.trim().length > 100) errors.name = 'At most 100 characters.'
  if (d.scope === 'Location' && !d.locationId) errors.locationId = 'A location.'
  if (d.scope === 'Router' && !d.sensorId) errors.sensorId = 'A router.'
  if (d.interfacePattern.trim().length > 100) errors.interfacePattern = 'At most 100 characters.'
  if (parseVlans(d.vlans) === null) errors.vlans = 'VLAN IDs from 1 to 4094 and ranges, as 10,20-29.'
  if (d.macPrefix.trim() && !macPrefix(d.macPrefix)) errors.macPrefix = '1 to 6 octets of a MAC address, as 00:50:56.'
  if (!hasCondition(d) && d.scope !== 'Router')
    errors.scope = 'At least one condition: a rule without one would skip everything. Only on one router may it do so.'
  return errors
}

/** The table's chips: "vlan*", "type vlan", "VLAN 99", "CDP", "MAC 00:50:56…". */
export function chips(
  rule: Pick<SkipRule, 'interfacePattern' | 'interfaceTypes' | 'vlans' | 'evidence' | 'macPrefix'>,
): string[] {
  return [
    ...(rule.interfacePattern ? [rule.interfacePattern] : []),
    ...(rule.interfaceTypes.length ? [`type ${rule.interfaceTypes.join(', ')}`] : []),
    ...(rule.vlans ? [`VLAN ${rule.vlans}`] : []),
    ...rule.evidence.map((e) => evidenceLabels[e]),
    ...(rule.macPrefix ? [`MAC ${rule.macPrefix}…`] : []),
  ]
}

/** "All routers", "Main building", "office-gw". */
export function scopeLabel(rule: Pick<SkipRule, 'scope' | 'locationName' | 'routerName'>): string {
  if (rule.scope === 'Location') return rule.locationName ?? '(deleted location)'
  if (rule.scope === 'Router') return rule.routerName ?? '(deleted router)'
  return 'All routers'
}

const ruleEvidence: Record<string, SkipEvidence> = {
  'mac-table': 'MacTable',
  'mac-table-host': 'MacTable',
  arp: 'Arp',
  'arp-host': 'Arp',
  wifi: 'Wifi',
  wireguard: 'WireGuard',
  tunnel: 'Tunnel',
}

function evidenceOf(e: S['Evidence']): SkipEvidence[] {
  if (e.rule === 'neighbor' || e.rule === 'neighbor-host') {
    const by = (e.by ?? '').split(',').map((p) => p.trim().toLowerCase())
    return (['Mndp', 'Lldp', 'Cdp'] as const).filter((p) => by.includes(p.toLowerCase()))
  }
  return ruleEvidence[e.rule] ? [ruleEvidence[e.rule]!] : []
}

/**
 * *Skip like this…* (§4.5): a rule on the router the evidence came from, with its interface as that router printed it, its
 * VLAN and its table. The person narrows or widens it, and the preview shows what it does.
 */
export function draftFromSuggestion(s: S['NetworkSuggestionItem']): RuleDraft {
  const e = s.evidence.find((x) => x.routerId && x.rule !== 'second-path') ?? s.evidence[0]
  const router = e?.routerId ?? (s.a.kind === 'Router' ? s.a.sensorId : s.b.kind === 'Router' ? s.b.sensorId : null)
  const port = e?.interface ?? null
  return {
    ...blankDraft(),
    name: port ? `Skip ${port}` : 'Skip',
    scope: router ? 'Router' : 'All',
    sensorId: router ?? null,
    interfacePattern: port ?? '',
    vlans: e?.vlan != null ? String(e.vlan) : '',
    evidence: e ? evidenceOf(e) : [],
  }
}
