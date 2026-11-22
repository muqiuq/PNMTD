import type { LocationQuery } from 'vue-router'
import type { KbListQuery, KbStatus } from '@/api/kb'

/**
 * The knowledge base list's state (docs/locations-knowledge-plan.md §6.6), kept in the URL so a list can be shared:
 * `/kb?tags=backup,zrh&sort=views`. Only what differs from the defaults is written.
 */
export type KbSort = 'newest' | 'updated' | 'views' | 'edits' | 'likes' | 'uses' | 'title' | 'relevance'
export type KbPeriod = '30d' | 'all'

export interface KbListState {
  q: string
  tags: string[]
  status: KbStatus | null
  location: string | null
  host: string | null
  sensor: string | null
  archived: boolean
  /** Only sensitive pages (true), only the others (false), or all (docs/knowledge-base-extensions-plan.md §4). */
  sensitive: boolean | null
  /** Only pages written mostly with AI (true), only the others (false), or all (§5). */
  ai: boolean | null
  /** Null: newest, or relevance while searching (the server's default). */
  sort: KbSort | null
  period: KbPeriod
  page: number
}

export const sortOptions: { label: string; value: KbSort; searching?: boolean }[] = [
  { label: 'Newest', value: 'newest' },
  { label: 'Recently updated', value: 'updated' },
  { label: 'Most viewed', value: 'views' },
  { label: 'Most edited', value: 'edits' },
  { label: 'Most liked', value: 'likes' },
  { label: 'Most used to solve a problem', value: 'uses' },
  { label: 'Title', value: 'title' },
  { label: 'Relevance', value: 'relevance', searching: true },
]

const sorts = new Set<string>(sortOptions.map((s) => s.value))

/** The orders that count over a period: 30 days or all time (docs/knowledge-base-extensions-plan.md §9). */
export const periodSorts: readonly KbSort[] = ['views', 'edits', 'likes', 'uses']
const hasPeriod = (sort: KbSort | null) => sort !== null && periodSorts.includes(sort)
const statuses = new Set<string>(['Draft', 'Published', 'NeedsReview'])

function one(value: LocationQuery[string]): string {
  const v = Array.isArray(value) ? value[0] : value
  return typeof v === 'string' ? v : ''
}

/** "true" or "false" in the URL; anything else: no filter. */
function yesNo(value: LocationQuery[string]): boolean | null {
  const v = one(value)
  return v === 'true' ? true : v === 'false' ? false : null
}

/** A tag as the server stores it, for the URL: lower case, no leading #. */
export function normalizeTag(tag: string): string {
  return tag
    .trim()
    .replace(/^#+/, '')
    .toLowerCase()
    .replace(/[\s_]+/g, '-')
}

export function fromQuery(query: LocationQuery): KbListState {
  const sort = one(query.sort)
  const status = one(query.status)
  const page = Number.parseInt(one(query.page), 10)
  return {
    q: one(query.q),
    tags: [
      ...new Set(
        one(query.tags)
          .split(',')
          .map(normalizeTag)
          .filter((t) => t.length > 0),
      ),
    ],
    status: statuses.has(status) ? (status as KbStatus) : null,
    location: one(query.location) || null,
    host: one(query.host) || null,
    sensor: one(query.sensor) || null,
    archived: one(query.archived) === 'true',
    sensitive: yesNo(query.sensitive),
    ai: yesNo(query.ai),
    sort: sorts.has(sort) ? (sort as KbSort) : null,
    period: one(query.period) === 'all' ? 'all' : '30d',
    page: page > 1 ? page : 1,
  }
}

export function toQuery(state: KbListState): Record<string, string> {
  const query: Record<string, string> = {}
  if (state.q.trim()) query.q = state.q.trim()
  if (state.tags.length) query.tags = state.tags.join(',')
  if (state.status) query.status = state.status
  if (state.location) query.location = state.location
  if (state.host) query.host = state.host
  if (state.sensor) query.sensor = state.sensor
  if (state.archived) query.archived = 'true'
  if (state.sensitive !== null) query.sensitive = String(state.sensitive)
  if (state.ai !== null) query.ai = String(state.ai)
  if (state.sort) query.sort = state.sort
  if (state.period !== '30d' && hasPeriod(state.sort)) query.period = state.period
  if (state.page > 1) query.page = String(state.page)
  return query
}

/** The API's query. Relevance needs words: without a search it falls back to the default order. */
export function toApi(state: KbListState, pageSize = 50): KbListQuery {
  const searching = state.q.trim().length > 0
  return {
    q: searching ? state.q.trim() : undefined,
    tags: state.tags.length ? state.tags.join(',') : undefined,
    status: state.status ?? undefined,
    location: state.location ?? undefined,
    host: state.host ?? undefined,
    sensor: state.sensor ?? undefined,
    archived: state.archived || undefined,
    sensitive: state.sensitive ?? undefined,
    ai: state.ai ?? undefined,
    sort: state.sort === 'relevance' && !searching ? undefined : (state.sort ?? undefined),
    period: hasPeriod(state.sort) ? state.period : undefined,
    page: state.page,
    pageSize,
  }
}

/** A tag added to the filter, or taken out when it is in it already: a click in the tag browser. */
export function toggleTag(state: KbListState, tag: string): KbListState {
  const t = normalizeTag(tag)
  const tags = state.tags.includes(t) ? state.tags.filter((x) => x !== t) : [...state.tags, t]
  return { ...state, tags, page: 1 }
}
