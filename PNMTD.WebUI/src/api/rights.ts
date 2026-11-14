import type { Schemas } from './client'

/** The areas and levels of docs/audit-redmine-zabbix-plan.md §5.2 (the server's RightsCatalog). */
export type Area = Schemas['Area']
export type Level = Schemas['Level']

/** What a route, a drawer entry or a button needs: a level in an area, or the admin flag. */
export type Right = readonly [Area, Level] | 'admin'

/** Each area's levels in order: a level includes the ones before it. The server decides; this only spares dead ends. */
export const levelOrder: Record<Area, readonly Level[]> = {
  Monitoring: ['View', 'Edit'],
  Commands: ['Run'],
  Mail: ['Read'],
  Decisions: ['Decide'],
  Locations: ['View', 'Edit', 'Manage'],
  KnowledgeBase: ['View', 'Edit', 'Manage'],
  Redmine: ['View', 'Link'],
  Zabbix: ['View', 'Details'],
  UdpNodes: ['Manage'],
  Mikrotik: ['Manage'],
  Servers: ['Manage'],
  SshKeys: ['Manage'],
  Diagnostics: ['Manage'],
  Ai: ['Manage'],
  System: ['Manage'],
  AuditLog: ['View'],
  Cookie: ['Use', 'Instruct', 'Manage'],
  Availability: ['View', 'Edit', 'Manage'],
}

/** Whether these rights (the admin flag, or the levels per area) include the level asked for. */
export function holds(isAdmin: boolean, rights: Partial<Record<Area, Level>>, area: Area, level: Level): boolean {
  if (isAdmin) return true
  const order = levelOrder[area]
  const held = rights[area]
  return !!held && order.indexOf(held) >= order.indexOf(level) && order.includes(level)
}
