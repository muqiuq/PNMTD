import type { LocationQuery } from 'vue-router'
import type { KbGraphEdge, KbGraphNode, KbMapQuery, KbStatus } from '@/api/kb'
import { parseFocus } from '@/components/graph/focus'

/**
 * The knowledge map's state and formats (docs/knowledge-base-extensions-plan.md §8): what the URL holds, the nodes' sizes and labels.
 */

/** A node's radius: a page and a location larger than a host, a sensor smallest. */
export function nodeRadius(kind: KbGraphNode['kind']): number {
  return kind === 'Page' ? 13 : kind === 'Location' ? 12 : kind === 'Host' ? 10 : 7
}

/** Room around a node for its name below it. */
export const LABEL_ROOM = 26

const MAX_LABEL = 32

/** "KB-42 Printer setup, 2nd fl…", a location's, host's or sensor's name. */
export function nodeLabel(n: Pick<KbGraphNode, 'kind' | 'name' | 'number'>): string {
  const text = n.kind === 'Page' && n.number != null ? `KB-${n.number} ${n.name}` : n.name
  return text.length > MAX_LABEL ? `${text.slice(0, MAX_LABEL - 1)}…` : text
}

/** An edge's key: unique per pair and kind. */
export function edgeKey(e: Pick<KbGraphEdge, 'source' | 'target' | 'kind'>): string {
  return `${e.kind}|${e.source}|${e.target}`
}

export const edgeLabel: Record<KbGraphEdge['kind'], string> = {
  PageLink: 'Linked pages',
  TextLink: 'Linked in the text',
  ObjectLink: 'Linked to',
  Structure: 'Belongs to',
}

export const kindLabel: Record<KbGraphNode['kind'], string> = {
  Page: 'Page',
  Location: 'Location',
  Host: 'Host',
  Sensor: 'Sensor',
}

export interface KbMapState {
  /** page:42, location:<id>, host:<id>, sensor:<id>; null: the whole knowledge base. */
  focus: string | null
  depth: number
  tags: string[]
  location: string | null
  status: KbStatus | null
  ai: boolean | null
  archived: boolean
  unlinked: boolean
  circles: boolean
}

function one(value: LocationQuery[string]): string {
  const v = Array.isArray(value) ? value[0] : value
  return typeof v === 'string' ? v : ''
}

const statuses = new Set<string>(['Draft', 'Published', 'NeedsReview'])

export function mapFromQuery(query: LocationQuery): KbMapState {
  const focus = parseFocus(one(query.focus), ['page', 'location', 'host', 'sensor'] as const, ['page'] as const)
  const depth = Number.parseInt(one(query.depth), 10)
  const status = one(query.status)
  const ai = one(query.ai)
  return {
    focus: focus ? `${focus.kind}:${focus.id}` : null,
    depth: depth >= 1 && depth <= 4 ? depth : 2,
    tags: one(query.tags)
      .split(',')
      .map((t) => t.trim().replace(/^#+/, '').toLowerCase())
      .filter((t) => t.length > 0),
    location: one(query.location) || null,
    status: statuses.has(status) ? (status as KbStatus) : null,
    ai: ai === 'true' ? true : ai === 'false' ? false : null,
    archived: one(query.archived) === 'true',
    unlinked: one(query.unlinked) === 'true',
    circles: one(query.circles) === 'true',
  }
}

export function mapToQuery(s: KbMapState): Record<string, string> {
  const q: Record<string, string> = {}
  if (s.focus) q.focus = s.focus
  if (s.focus && s.depth !== 2) q.depth = String(s.depth)
  if (!s.focus) {
    if (s.tags.length) q.tags = s.tags.join(',')
    if (s.location) q.location = s.location
    if (s.status) q.status = s.status
    if (s.ai !== null) q.ai = String(s.ai)
    if (s.unlinked) q.unlinked = 'true'
  }
  if (s.archived) q.archived = 'true'
  if (s.circles) q.circles = 'true'
  return q
}

/** The API's query: the filters only without a focus. */
export function mapToApi(s: KbMapState): KbMapQuery {
  return s.focus
    ? { focus: s.focus, depth: s.depth, archived: s.archived || undefined }
    : {
        tags: s.tags.length ? s.tags.join(',') : undefined,
        location: s.location ?? undefined,
        status: s.status ?? undefined,
        ai: s.ai ?? undefined,
        archived: s.archived || undefined,
        unlinked: s.unlinked || undefined,
      }
}
