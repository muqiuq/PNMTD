import type { LocationQuery } from 'vue-router'
import type { KbLinkKind } from '@/api/kb'

/**
 * The Links tab's state in the URL (docs/responsibilities-extensions-plan.md §5.1), so a filtered view can be shared or bookmarked:
 * ?show=hosts, ?r=<id>,<id>, ?person=<id>, ?kinds=Host,Page, ?location=<id>, ?below=0, ?covered=1. The page, the sort and the
 * search are useServerTable's (?page, ?sort, ?q).
 */

export type LinksView = 'links' | 'hosts' | 'locations'

export interface LinksState {
  show: LinksView
  responsibilities: string[]
  person: string | null
  kinds: KbLinkKind[]
  location: string | null
  /** With a location: its sub-locations too. */
  below: boolean
  /** Without a responsibility: leave out those whose location or a parent of it is linked. */
  covered: boolean
}

const views: readonly LinksView[] = ['links', 'hosts', 'locations']
const linkKinds: readonly KbLinkKind[] = ['Page', 'Location', 'Host']

function one(value: LocationQuery[string] | undefined): string {
  const v = Array.isArray(value) ? value[0] : value
  return typeof v === 'string' ? v : ''
}

const list = (value: LocationQuery[string] | undefined) =>
  one(value)
    .split(',')
    .map((v) => v.trim())
    .filter(Boolean)

export function linksFromQuery(query: LocationQuery): LinksState {
  const show = one(query.show) as LinksView
  return {
    show: views.includes(show) ? show : 'links',
    responsibilities: list(query.r),
    person: one(query.person) || null,
    kinds: list(query.kinds).filter((k): k is KbLinkKind => linkKinds.includes(k as KbLinkKind)),
    location: one(query.location) || null,
    below: one(query.below) !== '0',
    covered: one(query.covered) === '1',
  }
}

/** The state's keys for the URL; undefined removes a key, so defaults leave nothing behind. */
export function linksToQuery(state: LinksState): Record<string, string | undefined> {
  const links = state.show === 'links'
  return {
    show: links ? undefined : state.show,
    r: links && state.responsibilities.length ? state.responsibilities.join(',') : undefined,
    person: links ? (state.person ?? undefined) : undefined,
    kinds: links && state.kinds.length ? state.kinds.join(',') : undefined,
    location: state.location ?? undefined,
    below: state.location && !state.below ? '0' : undefined,
    covered: !links && state.covered ? '1' : undefined,
  }
}
