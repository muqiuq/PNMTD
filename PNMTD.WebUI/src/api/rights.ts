import type { Schemas } from './client'

/** The areas and levels of docs/audit-redmine-zabbix-plan.md §5.2 (the server's RightsCatalog). */
export type Area = Schemas['Area']
export type Level = Schemas['Level']

/** A level in an area. */
export type AreaLevel = readonly [Area, Level]

/**
 * What a route, a drawer entry or a button needs: a level in an area, the admin flag, or any of several levels (Admin →
 * Cookie opens for Cookie: Manage or Redmine: Manage, docs/cookie-redmine-tab-plan.md §3.2).
 */
export type Right = AreaLevel | 'admin' | { readonly anyOf: readonly AreaLevel[] }

/** Each area's levels in order: a level includes the ones before it. The server decides; this only spares dead ends. */
export const levelOrder: Record<Area, readonly Level[]> = {
  Monitoring: ['View', 'Details', 'Edit'],
  Commands: ['Run'],
  Mail: ['Read'],
  Decisions: ['Decide'],
  Locations: ['View', 'Edit', 'Manage'],
  KnowledgeBase: ['View', 'Edit', 'Manage'],
  // docs/cookie-redmine-tab-plan.md §3.1: Manage is Admin → Cookie → Redmine.
  Redmine: ['View', 'Link', 'Manage'],
  Zabbix: ['View', 'Details'],
  UdpNodes: ['Manage'],
  Mikrotik: ['Manage'],
  // docs/linux-os-updates-plan.md §8.2 (D2): Run installs OS updates and acknowledges high-risk findings; Manage includes it.
  Servers: ['Run', 'Manage'],
  SshKeys: ['Manage'],
  Diagnostics: ['Manage'],
  Ai: ['Manage'],
  System: ['Manage'],
  AuditLog: ['View'],
  Cookie: ['Use', 'Instruct', 'Manage'],
  Availability: ['View', 'Edit', 'Manage'],
  Mcp: ['View', 'Manage'],
  // docs/ssl-certificate-plan.md §6.4: the Certificate tab, certificate sensors with Monitoring: Edit, the private CAs.
  Certificates: ['View', 'Edit', 'Manage'],
  // docs/connectors-plan.md §12.1: every call and connector; Manage changes them, and the callers and their keys.
  Connectors: ['View', 'Manage'],
  // docs/network-map-plan.md §9.1: the interfaces are Details, which name people (DHCP host names, Wi-Fi clients).
  NetworkMap: ['View', 'Details', 'Edit', 'Manage'],
  // docs/lounge-plan.md §3.1: writing in the lounge; reading it needs no right.
  Lounge: ['Use'],
  // docs/responsibilities-plan.md §12.1: information only; Manage changes them and sets the global people.
  Responsibilities: ['View', 'Manage'],
}

/** Admin → Cookie: its Redmine tab is Redmine: Manage's, every other tab Cookie: Manage's. */
export const cookieAdminRight: Right = {
  anyOf: [
    ['Cookie', 'Manage'],
    ['Redmine', 'Manage'],
  ],
}

/** Whether a right is met, given how one level in an area is checked. */
export function meets(right: Right, isAdmin: boolean, can: (area: Area, level: Level) => boolean): boolean {
  if (right === 'admin') return isAdmin
  if ('anyOf' in right) return right.anyOf.some(([area, level]) => can(area, level))
  return can(right[0], right[1])
}

/** Whether these rights (the admin flag, or the levels per area) include the level asked for. */
export function holds(isAdmin: boolean, rights: Partial<Record<Area, Level>>, area: Area, level: Level): boolean {
  if (isAdmin) return true
  const order = levelOrder[area]
  const held = rights[area]
  return !!held && order.indexOf(held) >= order.indexOf(level) && order.includes(level)
}
