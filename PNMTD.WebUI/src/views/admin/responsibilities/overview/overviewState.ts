import type { LocationQuery } from 'vue-router'
import { parseFocus } from '@/components/graph/focus'
import { allLines, type Layer, type Lines } from './overviewLayout'

/**
 * The overview's state in the URL (docs/responsibilities-extensions-plan.md §6.1): ?layers=people,hosts and ?focus=responsibility:<id>,
 * as the knowledge map keeps its own. Without ?layers, the defaults: people and notes. ?layers= (empty) is nothing but the
 * responsibilities. ?lines=deputy or ?lines=both chooses the people's lines (docs/responsibilities-mcp-and-map-plan.md §4.1); without
 * it, the first in each escalation order.
 */

export const allLayers: readonly Layer[] = ['people', 'hosts', 'locations', 'pages', 'notes']
export const defaultLayers: readonly Layer[] = ['people', 'notes']
export const focusKinds = ['responsibility', 'person', 'host', 'location', 'page', 'note'] as const

function one(value: LocationQuery[string] | undefined): string | null {
  const v = Array.isArray(value) ? value[0] : value
  return typeof v === 'string' ? v : null
}

export function layersFromQuery(query: LocationQuery): Set<Layer> {
  const text = one(query.layers)
  if (text === null) return new Set(defaultLayers)
  return new Set(
    text
      .split(',')
      .map((l) => l.trim())
      .filter((l): l is Layer => allLayers.includes(l as Layer)),
  )
}

/** undefined for the defaults, so a plain URL stays plain. */
export function layersToQuery(layers: ReadonlySet<Layer>): string | undefined {
  const list = allLayers.filter((l) => layers.has(l))
  const same = list.length === defaultLayers.length && defaultLayers.every((l) => layers.has(l))
  return same ? undefined : list.join(',')
}

/** The node ?focus= names: "responsibility:<id>"; null for nothing or anything else. */
export function focusFromQuery(query: LocationQuery): string | null {
  const f = parseFocus(one(query.focus), focusKinds)
  return f ? `${f.kind}:${f.id}` : null
}

/** The lines ?lines= names; the default, the first in each order, for nothing or anything else. */
export function linesFromQuery(query: LocationQuery): Lines {
  const text = one(query.lines)
  return allLines.includes(text as Lines) ? (text as Lines) : 'responsible'
}

/** undefined for the default, so a plain URL stays plain. */
export function linesToQuery(lines: Lines): string | undefined {
  return lines === 'responsible' ? undefined : lines
}
