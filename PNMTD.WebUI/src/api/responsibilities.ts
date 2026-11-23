import { call, client, type Schemas } from './client'

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

export const responsibilitiesApi = {
  list: (query: { q?: string; attention?: boolean } = {}) =>
    call(client.GET('/api/v1/responsibilities', { params: { query } })),
  get: (id: Id) => call(client.GET('/api/v1/responsibilities/{id}', { params: { path: { id } } })),
  create: (body: ResponsibilityRequest) => call(client.POST('/api/v1/responsibilities', { body })),
  update: (id: Id, body: ResponsibilityRequest) =>
    call(client.PUT('/api/v1/responsibilities/{id}', { params: { path: { id } }, body })),
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
}
