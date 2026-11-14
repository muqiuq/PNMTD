import type { S } from '@/api/resources'

/**
 * The locations' tree in the browser (docs/locations-knowledge-plan.md §3.3, §3.6): GET /locations answers the whole tree
 * flat, depth-first, each with its parent; the list, the pickers and the filters build on it here.
 */

export type LocationItem = S['LocationListItem']

/** Between the names of a path, as the server writes it: Zurich DC › Building A › Room 2.13. */
export const pathSeparator = ' › '

/** As PrimeVue's TreeTable wants a node. */
export interface LocationTreeNode {
  key: string
  data: LocationItem
  children: LocationTreeNode[]
}

/** The flat list as nested nodes, in its order; an item whose parent isn't in the list is at the top. */
export function toTree(items: readonly LocationItem[]): LocationTreeNode[] {
  const nodes = new Map<string, LocationTreeNode>()
  for (const item of items) {
    nodes.set(item.id, { key: item.id, data: item, children: [] })
  }
  const roots: LocationTreeNode[] = []
  for (const item of items) {
    const node = nodes.get(item.id)!
    const parent = item.parentId ? nodes.get(item.parentId) : undefined
    ;(parent ? parent.children : roots).push(node)
  }
  return roots
}

/** A location and everything below it: what its hosts and state include (§3.3). Empty for an unknown id. */
export function subtreeIds(items: readonly Pick<LocationItem, 'id' | 'parentId'>[], id: string): Set<string> {
  const children = new Map<string, string[]>()
  for (const item of items) {
    if (item.parentId) {
      children.set(item.parentId, [...(children.get(item.parentId) ?? []), item.id])
    }
  }
  const result = new Set<string>()
  if (!items.some((i) => i.id === id)) {
    return result
  }
  const stack = [id]
  while (stack.length) {
    const next = stack.pop()!
    if (!result.has(next)) {
      result.add(next)
      stack.push(...(children.get(next) ?? []))
    }
  }
  return result
}

/**
 * A path below an ancestor's: <i>Cage B › Rack 4</i> on Zurich DC's page for <i>Zurich DC › Cage B › Rack 4</i>; the
 * location itself is empty; a path elsewhere stays whole.
 */
export function relativePath(path: string, ancestorPath: string): string {
  if (path === ancestorPath) {
    return ''
  }
  const prefix = ancestorPath + pathSeparator
  return path.startsWith(prefix) ? path.slice(prefix.length) : path
}

/** A picker's choice: a location by its path, indented by its depth. */
export interface LocationOption {
  value: string
  label: string
  name: string
  depth: number
}

/**
 * The locations to pick from, in the tree's order. <code>exclude</code> leaves a location and its sub-locations out:
 * where it can't move (§3.3).
 */
export function pickerOptions(items: readonly LocationItem[], exclude?: string | null): LocationOption[] {
  const skip = exclude ? subtreeIds(items, exclude) : new Set<string>()
  return items.filter((i) => !skip.has(i.id)).map((i) => ({ value: i.id, label: i.path, name: i.name, depth: i.depth }))
}

/** The tree's search, in the browser: matches flat, with their paths, in the tree's order. */
export function matching(items: readonly LocationItem[], search: string): LocationItem[] {
  const text = search.trim().toLowerCase()
  if (!text) {
    return [...items]
  }
  return items.filter((i) => i.name.toLowerCase().includes(text) || (i.description ?? '').toLowerCase().includes(text))
}

/** How many of the fields are filled, inherited values counted (§3.6): <i>6 of 9</i>. */
export function filledText(item: Pick<LocationItem, 'fieldsFilled'>, fields: number): string {
  return `${item.fieldsFilled} of ${fields}`
}
