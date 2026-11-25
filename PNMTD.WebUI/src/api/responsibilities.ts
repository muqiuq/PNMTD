import { call, client, type Schemas } from './client'
import type { ListQuery } from './resources'

/**
 * Responsibilities' calls (docs/responsibilities-plan.md §11): who owns what, with a deputy, its links and the global people.
 * Information only: nothing is sent to these people. Kept beside resources.ts, as kb.ts is.
 */

type S = Schemas
type Id = string

export type Responsibility = S['ResponsibilityDetail']
export type ResponsibilityItem = S['ResponsibilityItem']
export type ResponsibilityList = S['ResponsibilityList']
export type ResponsibilityPerson = S['ResponsibilityPerson']
export type ResponsibilityGlobal = S['ResponsibilityGlobal']
export type ResponsibilityLink = S['ResponsibilityLinkItem']
export type ResponsibilityRequest = S['ResponsibilityRequest']
export type ResponsibilityHistoryItem = S['ResponsibilityHistoryItem']
export type ResponsibilityHistoryKind = S['ResponsibilityHistoryKind']
export type ResponsibilityPersonOption = S['ResponsibilityPersonOption']
export type RedmineCombination = S['RedmineCombinationItem']
export type RedmineCombinationRequest = S['RedmineCombinationRequest']
export type RedmineCombinationProject = S['RedmineCombinationProject']
export type ResponsibilityLinkRow = S['ResponsibilityLinkRow']
export type ResponsibilityUnlinkedRow = S['ResponsibilityUnlinkedRow']
export type ResponsibilityBrief = S['ResponsibilityBrief']
export type ResponsibilityBulkResult = S['ResponsibilityBulkResult']

/** The Links tab's filters (docs/responsibilities-extensions-plan.md §5.2). */
export interface LinkRowsFilters {
  responsibility?: Id[]
  person?: Id
  kind?: S['KbLinkKind'][]
  location?: Id
  below?: boolean
}

export interface UnlinkedFilters {
  location?: Id
  below?: boolean
  covered?: boolean
}

export const responsibilitiesApi = {
  list: (query: { q?: string; attention?: boolean } = {}) =>
    call(client.GET('/api/v1/responsibilities', { params: { query } })),
  get: (id: Id) => call(client.GET('/api/v1/responsibilities/{id}', { params: { path: { id } } })),
  create: (body: ResponsibilityRequest) => call(client.POST('/api/v1/responsibilities', { body })),
  update: (id: Id, body: ResponsibilityRequest) =>
    call(client.PUT('/api/v1/responsibilities/{id}', { params: { path: { id } }, body })),
  /** The colour alone, #rrggbb or null: the overview's side panel (docs/responsibilities-extensions-plan.md §4.2). */
  setColor: (id: Id, color: string | null) =>
    call(client.PUT('/api/v1/responsibilities/{id}/color', { params: { path: { id } }, body: { color } })),
  remove: (id: Id) => call(client.DELETE('/api/v1/responsibilities/{id}', { params: { path: { id } } })),
  /** The long description, saved as a page's text: 409 edit_conflict, 422 looks_like_credential. */
  describe: (id: Id, body: S['ResponsibilityDescriptionRequest']) =>
    call(client.PUT('/api/v1/responsibilities/{id}/description', { params: { path: { id } }, body })),
  /** The description from before a change in the history. */
  restore: (id: Id, rowId: Id) =>
    call(client.POST('/api/v1/responsibilities/{id}/history/{rowId}/restore', { params: { path: { id, rowId } } })),
  link: (id: Id, kind: S['KbLinkKind'], targetId: Id) =>
    call(
      client.POST('/api/v1/responsibilities/{id}/links', { params: { path: { id } }, body: { kind, id: targetId } }),
    ),
  unlink: (id: Id, linkId: Id) =>
    call(client.DELETE('/api/v1/responsibilities/{id}/links/{linkId}', { params: { path: { id, linkId } } })),
  history: (id: Id, page = 1, pageSize = 50) =>
    call(client.GET('/api/v1/responsibilities/{id}/history', { params: { path: { id }, query: { page, pageSize } } })),
  historyRow: (id: Id, rowId: Id) =>
    call(client.GET('/api/v1/responsibilities/{id}/history/{rowId}', { params: { path: { id, rowId } } })),
  setGlobal: (responsibleId: Id | null, deputyId: Id | null) =>
    call(client.PUT('/api/v1/responsibilities/global', { body: { responsibleId, deputyId } })),
  people: () => call(client.GET('/api/v1/responsibilities/people')),
  /** The projects PNMT reads and Redmine's trackers, for the combination dialog (docs/responsibilities-plan.md §6.2). */
  redmine: () => call(client.GET('/api/v1/responsibilities/redmine')),
  combinations: (query: { project?: number; responsibility?: Id; user?: Id } = {}) =>
    call(client.GET('/api/v1/responsibilities/redmine/combinations', { params: { query } })),
  addCombination: (id: Id, body: RedmineCombinationRequest) =>
    call(client.POST('/api/v1/responsibilities/{id}/redmine', { params: { path: { id } }, body })),
  updateCombination: (id: Id, combinationId: Id, body: RedmineCombinationRequest) =>
    call(
      client.PUT('/api/v1/responsibilities/{id}/redmine/{combinationId}', {
        params: { path: { id, combinationId } },
        body,
      }),
    ),
  removeCombination: (id: Id, combinationId: Id) =>
    call(
      client.DELETE('/api/v1/responsibilities/{id}/redmine/{combinationId}', {
        params: { path: { id, combinationId } },
      }),
    ),
  /** The responsibilities that link one host, location or page, for its panel (docs/responsibilities-plan.md §8). */
  linked: (query: { host?: Id; location?: Id; page?: Id }) =>
    call(client.GET('/api/v1/responsibilities/linked', { params: { query } })),
  /** Every link of every responsibility, paged (docs/responsibilities-extensions-plan.md §5.2). */
  linkRows: (query: ListQuery & LinkRowsFilters) =>
    call(client.GET('/api/v1/responsibilities/links', { params: { query } })),
  /** The hosts (Monitoring: View) or locations (Locations: View) no responsibility links itself. */
  unlinked: (kind: 'Host' | 'Location', query: ListQuery & UnlinkedFilters) =>
    kind === 'Host'
      ? call(client.GET('/api/v1/responsibilities/unlinked/hosts', { params: { query } }))
      : call(client.GET('/api/v1/responsibilities/unlinked/locations', { params: { query } })),
  /** Pages, locations and hosts linked to one responsibility at once; those linked already are skipped. */
  linkMany: (responsibilityId: Id, targets: { kind: S['KbLinkKind']; id: Id }[]) =>
    call(client.POST('/api/v1/responsibilities/links', { body: { responsibilityId, targets } })),
  unlinkMany: (linkIds: Id[]) => call(client.POST('/api/v1/responsibilities/links/unlink', { body: { linkIds } })),
  /** The links' objects linked to another responsibility: moved, or with keep also linked there. */
  moveLinks: (linkIds: Id[], responsibilityId: Id, keep: boolean) =>
    call(client.POST('/api/v1/responsibilities/links/move', { body: { linkIds, responsibilityId, keep } })),
  /** Pages, locations and hosts by name, for the link picker, without Monitoring: View. */
  targets: (q: string) => call(client.GET('/api/v1/responsibilities/targets', { params: { query: { q } } })),
}

/** Query keys: everything of responsibilities under one prefix, so a change refreshes the list, the page and the panels. */
export const responsibilityKeys = {
  all: ['responsibilities'] as const,
  list: (query: { q?: string; attention?: boolean }) => ['responsibilities', 'list', query] as const,
  one: (id: Id) => ['responsibilities', 'one', id] as const,
  history: (id: Id, page: number) => ['responsibilities', 'history', id, page] as const,
  people: ['responsibilities', 'people'] as const,
  redmine: ['responsibilities', 'redmine'] as const,
  linked: (query: { host?: Id; location?: Id; page?: Id }) => ['responsibilities', 'linked', query] as const,
  combinations: (query: { project?: number; responsibility?: Id; user?: Id }) =>
    ['responsibilities', 'combinations', query] as const,
}

export const linkKindLabel: Record<'Page' | 'Location' | 'Host', string> = {
  Page: 'Page',
  Location: 'Location',
  Host: 'Host',
}

/** A linked object as the Links tab names it: "KB-42 Printer setup" for a page, else its name. */
export function linkObjectLabel(row: { kind: string; name: string; number?: number | null }): string {
  return row.kind === 'Page' && row.number != null ? `KB-${row.number} ${row.name}` : row.name
}

/**
 * What a bulk change did, for its toast: "Linked 5 objects to Network; 2 were linked there already", or for a move "Moved 3 links to
 * Network".
 */
export function bulkSummary(result: ResponsibilityBulkResult, to: string, move = false): string {
  const n = (count: number, one: string, many: string) => `${count} ${count === 1 ? one : many}`
  const head = move
    ? `Moved ${n(result.unlinked, 'link', 'links')} to ${to}`
    : `Linked ${n(result.linked, 'object', 'objects')} to ${to}`
  const already = result.skipped.filter((s) => s.reason === 'already_linked').length
  const gone = result.skipped.length - already
  return [
    head,
    already ? `${n(already, 'was', 'were')} linked there already` : null,
    gone ? `${n(gone, 'is', 'are')} gone` : null,
  ]
    .filter(Boolean)
    .join('; ')
}

/** "IT Support › Printers · every tracker". */
export function combinationLabel(c: {
  projectPath: string
  trackerName?: string | null
  trackerId?: number | null
}): string {
  return `${c.projectPath} · ${c.trackerId == null ? 'every tracker' : (c.trackerName ?? `#${c.trackerId}`)}`
}

/** A person as the page shows them: their name, *inactive* when deactivated, *nobody* without one. */
export function personLabel(person: ResponsibilityPerson | null | undefined): string {
  if (!person) return 'Nobody'
  return person.active ? person.name : `${person.name} (inactive)`
}

/**
 * Who answers for a responsibility (§5): its people, or the global people standing in when neither of its own is set and
 * active. Never "the responsible" alone: that word is the availability plan's (docs/responsibilities-plan.md §2.2).
 */
export function standsInLabel(global: Pick<ResponsibilityGlobal, 'responsible' | 'deputy'>): string {
  const { responsible, deputy } = global
  if (responsible && deputy)
    return `Global responsible stands in: ${personLabel(responsible)}, deputy ${personLabel(deputy)}`
  if (responsible) return `Global responsible stands in: ${personLabel(responsible)}`
  if (deputy) return `Global deputy stands in: ${personLabel(deputy)}`
  return 'Nobody: no global responsible is set'
}

export const historyKindLabel: Record<ResponsibilityHistoryKind, string> = {
  Created: 'Created',
  Renamed: 'Renamed',
  SummaryChanged: 'Short description changed',
  PeopleChanged: 'People changed',
  DescriptionChanged: 'Description changed',
  LinkAdded: 'Linked',
  LinkRemoved: 'Unlinked',
  RedmineAdded: 'Redmine combination added',
  RedmineChanged: 'Redmine combination changed',
  RedmineRemoved: 'Redmine combination removed',
  ColorChanged: 'Colour changed',
}
